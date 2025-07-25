using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using KaspaBot.Infrastructure.Repositories;
using KaspaBot.Domain.Interfaces;
using Mexc.Net.Enums;
using System.Text.Json;

namespace KaspaBot.Presentation
{
    public class OrderRecoveryService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<OrderRecoveryService> _logger;

        public OrderRecoveryService(IServiceProvider serviceProvider, ILogger<OrderRecoveryService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[OrderRecovery] Старт восстановления статусов ордеров...");
            using var scope = _serviceProvider.CreateScope();
            var orderPairRepo = scope.ServiceProvider.GetRequiredService<OrderPairRepository>();
            var userRepository = scope.ServiceProvider.GetRequiredService<KaspaBot.Domain.Interfaces.IUserRepository>();
            var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
            var allPairs = await orderPairRepo.GetAllAsync();
            // 1. Удаляем все пары с пустым BuyOrder.Id
            var emptyBuyPairs = allPairs.Where(p => string.IsNullOrEmpty(p.BuyOrder.Id)).ToList();
            foreach (var pair in emptyBuyPairs)
            {
                await orderPairRepo.DeleteByIdAsync(pair.Id);
                _logger.LogInformation($"[OrderRecovery] Удалён пустой buy-ордер: {pair.Id}");
            }
            // 2. Оставляем только актуальные пары
            allPairs = allPairs.Except(emptyBuyPairs).ToList();
            var notCompleted = allPairs.Where(p =>
                (!string.IsNullOrEmpty(p.BuyOrder.Id) && !IsFinal(p.BuyOrder.Status)) ||
                (!string.IsNullOrEmpty(p.SellOrder.Id) && !IsFinal(p.SellOrder.Status))
            ).ToList();
            foreach (var pair in notCompleted)
            {
                var user = await userRepository.GetByIdAsync(pair.UserId);
                if (user == null) continue;
                var mexcLogger = loggerFactory.CreateLogger<KaspaBot.Infrastructure.Services.MexcService>();
                var mexcService = new KaspaBot.Infrastructure.Services.MexcService(
                    user.ApiCredentials.ApiKey,
                    user.ApiCredentials.ApiSecret,
                    mexcLogger);
                // Покупка
                if (!string.IsNullOrEmpty(pair.BuyOrder.Id) && !IsFinal(pair.BuyOrder.Status))
                {
                    var buyStatus = await mexcService.GetOrderAsync(pair.BuyOrder.Symbol, pair.BuyOrder.Id, stoppingToken);
                    if (buyStatus.IsSuccess)
                    {
                        pair.BuyOrder.Status = buyStatus.Value.Status;
                        pair.BuyOrder.QuantityFilled = buyStatus.Value.QuantityFilled;
                        pair.BuyOrder.QuoteQuantityFilled = buyStatus.Value.QuoteQuantityFilled;
                        // Fallback для маркет-ордеров: если есть QuoteQuantityFilled/QuantityFilled — используем их для цены
                        if (buyStatus.Value.OrderType == Mexc.Net.Enums.OrderType.Market && buyStatus.Value.Status == Mexc.Net.Enums.OrderStatus.Filled && buyStatus.Value.QuantityFilled > 0 && buyStatus.Value.QuoteQuantityFilled > 0)
                        {
                            pair.BuyOrder.Price = buyStatus.Value.QuoteQuantityFilled / buyStatus.Value.QuantityFilled;
                        }
                        else
                        {
                            pair.BuyOrder.Price = buyStatus.Value.Price;
                        }
                        pair.BuyOrder.UpdatedAt = DateTime.UtcNow;
                        await orderPairRepo.UpdateAsync(pair);
                    }
                }
                // Продажа
                if (!string.IsNullOrEmpty(pair.SellOrder.Id) && !IsFinal(pair.SellOrder.Status))
                {
                    var sellStatus = await mexcService.GetOrderAsync(pair.SellOrder.Symbol, pair.SellOrder.Id, stoppingToken);
                    if (sellStatus.IsSuccess)
                    {
                        pair.SellOrder.Status = sellStatus.Value.Status;
                        pair.SellOrder.QuantityFilled = sellStatus.Value.QuantityFilled;
                        pair.SellOrder.Price = sellStatus.Value.Price;
                        pair.SellOrder.UpdatedAt = DateTime.UtcNow;
                    }
                }
                await orderPairRepo.UpdateAsync(pair);
            }
            // 3. Для всех пар без sell-ордера — пытаемся найти уже выставленный sell-ордер на бирже
            foreach (var pair in allPairs.Where(p => !string.IsNullOrEmpty(p.BuyOrder.Id) && string.IsNullOrEmpty(p.SellOrder.Id)))
            {
                var user = await userRepository.GetByIdAsync(pair.UserId);
                if (user == null) continue;
                var mexcLogger = loggerFactory.CreateLogger<KaspaBot.Infrastructure.Services.MexcService>();
                var mexcService = new KaspaBot.Infrastructure.Services.MexcService(
                    user.ApiCredentials.ApiKey,
                    user.ApiCredentials.ApiSecret,
                    mexcLogger
                );
                var sellPrice = pair.BuyOrder.Price.GetValueOrDefault() * (1 + user.Settings.PercentProfit / 100);
                // Округляем количество вверх, чтобы сумма была >= 1 USDT
                decimal minQty = Math.Ceiling((1m / sellPrice) * 1000m) / 1000m;
                var sellQty = pair.BuyOrder.QuantityFilled;
                if (sellQty * sellPrice < 1m)
                    sellQty = minQty;
                var openOrdersResult = await mexcService.GetOpenOrdersAsync(pair.BuyOrder.Symbol, stoppingToken);
                if (openOrdersResult.IsSuccess)
                {
                    var openSellOrders = openOrdersResult.Value.Where(o => o.Side == Mexc.Net.Enums.OrderSide.Sell).ToList();
                    foreach (var o in openSellOrders)
                    {
                        _logger.LogInformation($"[ORDER-RECOVERY-SELL-RAW] {JsonSerializer.Serialize(o)}");
                    }
                    // Временно ищем только по количеству и цене (без времени)
                    var match = openSellOrders.FirstOrDefault(o =>
                    {
                        var timeProp = o.GetType().GetProperty("time");
                        if (timeProp == null) return false;
                        var timeVal = timeProp.GetValue(o) as string;
                        if (string.IsNullOrEmpty(timeVal)) return false;
                        var orderTime = DateTime.Parse(timeVal).ToLocalTime();
                        return Math.Abs((orderTime - pair.BuyOrder.CreatedAt).TotalMinutes) < 30 &&
                               Math.Abs(o.Quantity - pair.BuyOrder.QuantityFilled) < 0.01m &&
                               Math.Abs(o.Price - sellPrice) < 0.001m;
                    });
                    if (match != null)
                    {
                        var timeVal = match.GetType().GetProperty("time")?.GetValue(match) as string;
                        pair.SellOrder.Id = match.OrderId;
                        pair.SellOrder.Price = match.Price;
                        pair.SellOrder.Quantity = match.Quantity;
                        pair.SellOrder.Status = match.Status;
                        if (!string.IsNullOrEmpty(timeVal))
                            pair.SellOrder.CreatedAt = DateTime.Parse(timeVal);
                        await orderPairRepo.UpdateAsync(pair);
                        _logger.LogInformation($"[OrderRecovery] Привязан найденный sell-ордер {match.OrderId} к паре {pair.Id}");
                        continue;
                    }
                }
                // Если не найден — выставляем новый sell-ордер
                var sellResult = await mexcService.PlaceOrderAsync(
                    symbol: pair.BuyOrder.Symbol,
                    side: Mexc.Net.Enums.OrderSide.Sell,
                    type: Mexc.Net.Enums.OrderType.Limit,
                    amount: sellQty,
                    price: sellPrice,
                    ct: stoppingToken
                );
                if (sellResult.IsSuccess && !string.IsNullOrEmpty(sellResult.Value))
                {
                    pair.SellOrder.Id = sellResult.Value;
                    pair.SellOrder.Price = sellPrice;
                    pair.SellOrder.Status = Mexc.Net.Enums.OrderStatus.New;
                    pair.SellOrder.CreatedAt = DateTime.UtcNow;
                    await orderPairRepo.UpdateAsync(pair);
                    _logger.LogInformation($"[OrderRecovery] Выставлен sell-ордер: {pair.SellOrder.Id} для пары {pair.Id}");
                }
                else
                {
                    _logger.LogError($"[ORDER-RECOVERY-SELL-ERROR] Не удалось выставить sell-ордер для пары {pair.Id}: {string.Join(", ", sellResult.Errors?.Select(e => e.Message) ?? new string[]{"Unknown error"})}");
                }
            }
            _logger.LogInformation($"[OrderRecovery] Восстановлено статусов: {notCompleted.Count}");
        }

        private static bool IsFinal(OrderStatus status)
        {
            return status == OrderStatus.Filled || status == OrderStatus.Canceled;
        }
    }
} 