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
using Telegram.Bot;

namespace KaspaBot.Presentation
{
    public class OrderRecoveryService : IOrderRecoveryService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<OrderRecoveryService> _logger;
        private readonly ITelegramBotClient _botClient;

        public OrderRecoveryService(IServiceProvider serviceProvider, ILogger<OrderRecoveryService> logger, ITelegramBotClient botClient)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _botClient = botClient;
        }

        public async Task RunRecoveryForUser(long userId, CancellationToken stoppingToken)
        {
            _logger.LogWarning($"[ORDER-RECOVERY-DBG] RunRecoveryForUser called for userId={userId}");
            using var scope = _serviceProvider.CreateScope();
            var orderPairRepo = scope.ServiceProvider.GetRequiredService<OrderPairRepository>();
            var userRepository = scope.ServiceProvider.GetRequiredService<KaspaBot.Domain.Interfaces.IUserRepository>();
            var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
            var allPairs = (await orderPairRepo.GetAllAsync()).Where(p => p.UserId == userId).ToList();
            _logger.LogWarning($"[ORDER-RECOVERY-DBG] allPairs.Count={allPairs.Count}");
            foreach (var pair in allPairs)
            {
                _logger.LogWarning($"[ORDER-RECOVERY-DBG] PAIR {pair.Id} SellOrder.Id={pair.SellOrder.Id} BuyOrder.Id={pair.BuyOrder.Id} BuyOrder.QuantityFilled={pair.BuyOrder.QuantityFilled} SellOrder.Quantity={pair.SellOrder.Quantity}");
            }
            // 1. Удаляем все пары с пустым BuyOrder.Id
            var emptyBuyPairs = allPairs.Where(p => string.IsNullOrEmpty(p.BuyOrder.Id)).ToList();
            foreach (var pair in emptyBuyPairs)
            {
                await orderPairRepo.DeleteByIdAsync(pair.Id);
                _logger.LogInformation($"[OrderRecovery] Удалён пустой buy-ордер: {pair.Id}");
            }
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
                if (!string.IsNullOrEmpty(pair.SellOrder.Id))
                {
                    _logger.LogWarning($"[ORDER-RECOVERY-DBG] CHECK SellOrder.Id={pair.SellOrder.Id} for pair {pair.Id}");
                    _logger.LogWarning($"[ORDER-RECOVERY-DBG] Try restore SellOrderId={pair.SellOrder.Id}");
                    var sellStatus = await mexcService.GetOrderAsync(pair.SellOrder.Symbol, pair.SellOrder.Id, stoppingToken);
                    _logger.LogWarning($"[ORDER-RECOVERY-DBG] SellOrderId={pair.SellOrder.Id} IsSuccess={sellStatus.IsSuccess} Errors={string.Join(", ", sellStatus.Errors.Select(e => e.Message))} Raw={System.Text.Json.JsonSerializer.Serialize(sellStatus.Value)}");
                    if (!sellStatus.IsSuccess)
                    {
                        _logger.LogWarning($"[ORDER-RECOVERY-DBG] SellOrderId={pair.SellOrder.Id} GetOrderAsync failed: {string.Join(", ", sellStatus.Errors.Select(e => e.Message))}");
                    }
                    else
                    {
                        _logger.LogInformation($"[ORDER-RECOVERY-DBG] SellOrderId={pair.SellOrder.Id} API.Quantity={sellStatus.Value.Quantity} API.Status={sellStatus.Value.Status} API.Price={sellStatus.Value.Price} API.QuantityFilled={sellStatus.Value.QuantityFilled} BEFORE.Quantity={pair.SellOrder.Quantity}");
                        pair.SellOrder.Quantity = sellStatus.Value.Quantity;
                        pair.SellOrder.Status = sellStatus.Value.Status;
                        pair.SellOrder.QuantityFilled = sellStatus.Value.QuantityFilled;
                        pair.SellOrder.Price = sellStatus.Value.Price;
                        pair.SellOrder.UpdatedAt = DateTime.UtcNow;
                        _logger.LogInformation($"[ORDER-RECOVERY-DBG] SellOrderId={pair.SellOrder.Id} AFTER.Quantity={pair.SellOrder.Quantity} Status={pair.SellOrder.Status} Price={pair.SellOrder.Price} QuantityFilled={pair.SellOrder.QuantityFilled}");
                    }
                }
                await orderPairRepo.UpdateAsync(pair);
            }
            // --- ОБНОВЛЯЕМ ВСЕ SELL-ОРДЕРА ДЛЯ ВСЕХ ПАР ---
            foreach (var pair in allPairs.Where(p => !string.IsNullOrEmpty(p.SellOrder.Id)))
            {
                var user = await userRepository.GetByIdAsync(pair.UserId);
                if (user == null) continue;
                var mexcLogger = loggerFactory.CreateLogger<KaspaBot.Infrastructure.Services.MexcService>();
                var mexcService = new KaspaBot.Infrastructure.Services.MexcService(
                    user.ApiCredentials.ApiKey,
                    user.ApiCredentials.ApiSecret,
                    mexcLogger);
                _logger.LogWarning($"[ORDER-RECOVERY-DBG] FORCE UPDATE SellOrder.Id={pair.SellOrder.Id} for pair {pair.Id}");
                var sellStatus = await mexcService.GetOrderAsync(pair.SellOrder.Symbol, pair.SellOrder.Id, stoppingToken);
                _logger.LogWarning($"[ORDER-RECOVERY-DBG] SellOrderId={pair.SellOrder.Id} IsSuccess={sellStatus.IsSuccess} Errors={string.Join(", ", sellStatus.Errors.Select(e => e.Message))} Raw={System.Text.Json.JsonSerializer.Serialize(sellStatus.Value)}");
                if (sellStatus.IsSuccess)
                {
                    pair.SellOrder.Quantity = sellStatus.Value.Quantity;
                    pair.SellOrder.Status = sellStatus.Value.Status;
                    pair.SellOrder.QuantityFilled = sellStatus.Value.QuantityFilled;
                    pair.SellOrder.Price = sellStatus.Value.Price;
                    pair.SellOrder.UpdatedAt = DateTime.UtcNow;
                    if (sellStatus.Value.Status == OrderStatus.Filled)
                    {
                        pair.CompletedAt = DateTime.UtcNow;
                        pair.Profit = (pair.SellOrder.Quantity * pair.SellOrder.Price) - (pair.BuyOrder.QuantityFilled * pair.BuyOrder.Price) - pair.BuyOrder.Commission;
                        var msg = $"ПРОДАНО\n\n{pair.SellOrder.Quantity:F2} KAS по {pair.SellOrder.Price:F6} USDT\n\nПолучено\n{(pair.SellOrder.Quantity * pair.SellOrder.Price):F8} USDT\n\nПРИБЫЛЬ\n{pair.Profit:F8} USDT";
                        await _botClient.SendMessage(chatId: pair.UserId, text: msg);
                    }
                    await orderPairRepo.UpdateAsync(pair);
                    _logger.LogInformation($"[ORDER-RECOVERY-DBG] SellOrderId={pair.SellOrder.Id} UPDATED: Quantity={pair.SellOrder.Quantity} Status={pair.SellOrder.Status} Price={pair.SellOrder.Price} QuantityFilled={pair.SellOrder.QuantityFilled}");
                }
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
                        _logger.LogInformation($"[ORDER-RECOVERY-SELL-RAW] {System.Text.Json.JsonSerializer.Serialize(o)}");
                    }
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
                    pair.SellOrder.Quantity = sellQty;
                    pair.SellOrder.Status = Mexc.Net.Enums.OrderStatus.New;
                    pair.SellOrder.CreatedAt = DateTime.UtcNow;
                    await orderPairRepo.UpdateAsync(pair);
                    _logger.LogInformation($"[OrderRecovery] Выставлен sell-ордер: {pair.SellOrder.Id} для пары {pair.Id}");
                    var buy = pair.BuyOrder;
                    var sell = pair.SellOrder;
                    var msg = $"КУПЛЕНО\n\n{buy.QuantityFilled:F2} KAS по {buy.Price:F6} USDT\n\nПотрачено\n{(buy.QuantityFilled * buy.Price):F8} USDT\n\nВЫСТАВЛЕНО\n\n{sell.Quantity:F2} KAS по {sell.Price:F6} USDT";
                    await _botClient.SendMessage(chatId: pair.UserId, text: msg);
                }
                else
                {
                    _logger.LogError($"[ORDER-RECOVERY-SELL-ERROR] Не удалось выставить sell-ордер для пары {pair.Id}: {string.Join(", ", sellResult.Errors?.Select(e => e.Message) ?? new string[]{"Unknown error"})}");
                }
            }
        }

        private static bool IsFinal(OrderStatus status)
        {
            return status == OrderStatus.Filled || status == OrderStatus.Canceled;
        }
    }
} 