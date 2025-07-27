using System.Collections.Concurrent;
using System.Text.Json;
using KaspaBot.Domain.Entities;
using KaspaBot.Domain.Interfaces;
using KaspaBot.Infrastructure.Options;
using KaspaBot.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mexc.Net.Enums;

namespace KaspaBot.Infrastructure.Services
{
    public class OrderAuditService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<OrderAuditService> _logger;
        private readonly BlockingCollection<OrderAuditEvent> _queue = new BlockingCollection<OrderAuditEvent>();
        private readonly bool _enabled;

        public OrderAuditService(IServiceProvider serviceProvider, ILogger<OrderAuditService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _enabled = serviceProvider.GetService<IConfiguration>()?.GetSection("Audit")?.GetValue<bool>("Enabled") == true;
            if (_enabled)
            {
                _logger.LogInformation("[AUDIT] OrderAuditService enabled");
            }
        }

        public void Enqueue(OrderAuditEvent evt)
        {
            if (_enabled)
            {
                _queue.Add(evt);
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_enabled)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                OrderAuditEvent evt = null;
                try
                {
                    if (_queue.TryTake(out evt, 1000, stoppingToken) && evt != null)
                    {
                        Task.Run(() => AuditOrder(evt), stoppingToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                await Task.Delay(100, stoppingToken);
            }
        }

        private async Task AuditOrder(OrderAuditEvent evt)
        {
            try
            {
                _logger.LogInformation($"[AUDIT] Начинаем аудит orderId={evt.OrderId} user={evt.UserId}");
                using var scope = _serviceProvider.CreateScope();
                var orderRepo = scope.ServiceProvider.GetRequiredService<OrderPairRepository>();
                var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
                var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
                var mexcLogger = loggerFactory.CreateLogger<MexcService>();

                var user = await userRepository.GetByIdAsync(evt.UserId);
                if (user == null)
                {
                    _logger.LogWarning($"[AUDIT] Пользователь {evt.UserId} не найден в базе");
                    return;
                }

                var options = new MexcOptions
                {
                    ApiKey = user.ApiCredentials.ApiKey,
                    ApiSecret = user.ApiCredentials.ApiSecret
                };
                var mexc = new MexcService(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, mexcLogger);

                _logger.LogInformation("[AUDIT] WS EVENT: " + JsonSerializer.Serialize(evt));
                _logger.LogInformation("[AUDIT] Запрашиваем REST GetOrderAsync для orderId=" + evt.OrderId);
                var restOrder = await mexc.GetOrderAsync(evt.Symbol, evt.OrderId, CancellationToken.None);
                if (!restOrder.IsSuccess)
                {
                    _logger.LogWarning("[AUDIT] REST GetOrderAsync failed для orderId=" + evt.OrderId + ": " + string.Join(", ", restOrder.Errors.Select(e => e.Message)));
                }
                else
                {
                    _logger.LogInformation("[AUDIT] REST ORDER: " + JsonSerializer.Serialize(restOrder.Value));
                }

                _logger.LogInformation("[AUDIT] Запрашиваем REST GetOrderTradesAsync для orderId=" + evt.OrderId);
                var restTrades = await mexc.GetOrderTradesAsync(evt.Symbol, evt.OrderId, CancellationToken.None);
                if (!restTrades.IsSuccess)
                {
                    _logger.LogWarning("[AUDIT] REST GetOrderTradesAsync failed для orderId=" + evt.OrderId + ": " + string.Join(", ", restTrades.Errors.Select(e => e.Message)));
                }
                else
                {
                    _logger.LogInformation("[AUDIT] REST TRADES: " + JsonSerializer.Serialize(restTrades.Value));
                }

                _logger.LogInformation("[AUDIT] Запрашиваем REST GetOrderHistoryAsync для symbol=" + evt.Symbol);
                _logger.LogInformation("[AUDIT] Ищем ордер в базе данных orderId=" + evt.OrderId);
                Order dbOrder = null;
                int retryCount = 3;
                for (int i = 0; i < retryCount; i++)
                {
                    dbOrder = await orderRepo.FindOrderByIdAsync(evt.OrderId);
                    if (dbOrder != null)
                    {
                        break;
                    }
                    await Task.Delay(1000);
                }

                if (dbOrder == null)
                {
                    _logger.LogWarning($"[AUDIT-ERR] Ордер {evt.OrderId} не найден в базе данных после {retryCount} попыток");
                    return;
                }

                _logger.LogInformation("[AUDIT] DB ORDER: " + JsonSerializer.Serialize(dbOrder));

                if (!restOrder.IsSuccess)
                {
                    return;
                }

                var value = restOrder.Value;
                if (dbOrder.Status != value.Status)
                {
                    _logger.LogWarning($"[AUDIT-ERR] Статус ордера {evt.OrderId} не совпадает: БД={dbOrder.Status} Биржа={value.Status}");
                }

                if (Math.Abs(dbOrder.QuantityFilled - value.QuantityFilled) > 0.0001m)
                {
                    _logger.LogWarning($"[AUDIT-ERR] Количество исполнено {evt.OrderId} не совпадает: БД={dbOrder.QuantityFilled} Биржа={value.QuantityFilled}");
                }

                if (!dbOrder.Price.HasValue)
                {
                    return;
                }

                decimal? calculatedPrice;
                if (value.OrderType == OrderType.Market && value.QuantityFilled > 0m && value.QuoteQuantityFilled > 0m)
                {
                    calculatedPrice = value.QuoteQuantityFilled / value.QuantityFilled;
                    if (!restTrades.IsSuccess || !restTrades.Value.Any())
                    {
                        return;
                    }

                    var trades = restTrades.Value.ToList();
                    var totalQuote = trades.Sum(t => t.QuoteQuantity);
                    var totalQty = trades.Sum(t => t.Quantity);
                    if (totalQty > 0m)
                    {
                        var avgPrice = totalQuote / totalQty;
                        if (Math.Abs(avgPrice - calculatedPrice.Value) > 0.0001m)
                        {
                            _logger.LogError($"[AUDIT-ERR] Расчетная цена MARKET ордера {evt.OrderId} не совпадает: REST={calculatedPrice:F6} Трейды={avgPrice:F6}");
                        }
                        if (Math.Abs(dbOrder.Price.Value - avgPrice) > 0.0001m)
                        {
                            _logger.LogError($"[AUDIT-ERR] Цена MARKET ордера {evt.OrderId} не совпадает: БД={dbOrder.Price:F6} Трейды={avgPrice:F6}");
                        }
                        else
                        {
                            _logger.LogInformation($"[AUDIT-OK] Цена MARKET ордера {evt.OrderId} совпадает: БД={dbOrder.Price:F6} Трейды={avgPrice:F6}");
                        }
                    }
                    return;
                }

                calculatedPrice = value.Price;
                if (Math.Abs(dbOrder.Price.Value - calculatedPrice.Value) > 0.0001m)
                {
                    _logger.LogWarning($"[AUDIT-ERR] Цена LIMIT ордера {evt.OrderId} не совпадает: БД={dbOrder.Price:F6} Биржа={calculatedPrice:F6}");
                }
                else
                {
                    _logger.LogInformation($"[AUDIT-OK] Цена LIMIT ордера {evt.OrderId} совпадает: БД={dbOrder.Price:F6} Биржа={calculatedPrice:F6}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AUDIT-ERR] Ошибка аудита для orderId=" + evt.OrderId);
            }
        }
    }
} 