using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using KaspaBot.Infrastructure.Repositories;
using KaspaBot.Domain.Interfaces;
using KaspaBot.Domain.Entities;
using Mexc.Net.Enums;
using Telegram.Bot;
using System.Collections.Generic;

namespace KaspaBot.Presentation
{
    public class DcaBuyService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<DcaBuyService> _logger;
        private readonly HashSet<long> _lowBalanceWarnedUsers = new();
        private readonly HashSet<long> _activeUsers = new();
        public DcaBuyService(IServiceProvider serviceProvider, ILogger<DcaBuyService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[DCA-DEBUG] Сервис автоторговли стартует");
            // Дать OrderRecoveryService время восстановить статусы
            await Task.Delay(5000, stoppingToken);
            var loggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>();
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
                    var users = await userRepository.GetAllAsync();
                    var newUsers = users.Where(u => u.Settings.IsAutoTradeEnabled && !_activeUsers.Contains(u.Id)).ToList();
                    foreach (var user in newUsers)
                    {
                        _activeUsers.Add(user.Id);
                        _ = Task.Run(() => RunForUser(user, loggerFactory, stoppingToken), stoppingToken);
                        _logger.LogInformation($"[DCA-DEBUG] Автоторговля запущена для user={user.Id}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"[DCA-DEBUG] Ошибка в основном цикле автоторговли: {ex}");
                }
                await Task.Delay(10000, stoppingToken); // Проверять каждые 10 секунд
            }
        }
        private async Task RunForUser(User user, ILoggerFactory loggerFactory, CancellationToken stoppingToken)
        {
            var mexcLogger = loggerFactory.CreateLogger<KaspaBot.Infrastructure.Services.MexcService>();
            var mexcService = new KaspaBot.Infrastructure.Services.MexcService(
                user.ApiCredentials.ApiKey,
                user.ApiCredentials.ApiSecret,
                mexcLogger);
            _logger.LogInformation($"[DCA-DEBUG] user={user.Id} стартует поток автоторговли");
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var orderPairRepo = scope.ServiceProvider.GetRequiredService<OrderPairRepository>();
                    var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();

                    // Проверка: существует ли пользователь в базе
                    var freshUser = await userRepository.GetByIdAsync(user.Id);
                    if (freshUser == null)
                    {
                        _logger.LogWarning($"[DCA-DEBUG] user={user.Id} удалён из базы, поток автоторговли завершён");
                        _activeUsers.Remove(user.Id);
                        return;
                    }
                    user = freshUser;
                    if (!user.Settings.IsAutoTradeEnabled)
                    {
                        _logger.LogInformation($"[DCA-DEBUG] user={user.Id} автоторговля выключена");
                        await Task.Delay(2000, stoppingToken);
                        continue;
                    }
                    // Проверка баланса USDT
                    var accResult = await mexcService.GetAccountInfoAsync(stoppingToken);
                    if (!accResult.IsSuccess)
                    {
                        _logger.LogWarning($"[DCA-DEBUG] user={user.Id} Не удалось получить баланс, автоторговля пропущена");
                        await Task.Delay(2000, stoppingToken);
                        continue;
                    }
                    var usdtBalance = accResult.Value.Balances.FirstOrDefault(b => b.Asset == "USDT")?.Available ?? 0m;
                    if (usdtBalance < user.Settings.OrderAmount)
                    {
                        if (!_lowBalanceWarnedUsers.Contains(user.Id))
                        {
                            var botClient = scope.ServiceProvider.GetRequiredService<ITelegramBotClient>();
                            await botClient.SendMessage(chatId: user.Id, text: $"Недостаточно USDT для автоторговли. Баланс: {usdtBalance:F2}, требуется: {user.Settings.OrderAmount:F2}. Пополните баланс для продолжения DCA.");
                            _lowBalanceWarnedUsers.Add(user.Id);
                        }
                        _logger.LogWarning($"[DCA-DEBUG] user={user.Id} Недостаточно USDT для автоторговли: {usdtBalance} < {user.Settings.OrderAmount}");
                        await Task.Delay(2000, stoppingToken);
                        continue;
                    }
                    else
                    {
                        _lowBalanceWarnedUsers.Remove(user.Id);
                    }
                    // lastCompletedPair: последняя завершённая пара (buy и sell исполнены)
                    var allPairs = await orderPairRepo.GetAllAsync();
                    // Найти последнюю незавершённую пару (buy Filled, sell не Filled)
                    var unfinishedPair = allPairs
                        .Where(p => p.UserId == user.Id && p.BuyOrder.Status == OrderStatus.Filled && p.SellOrder.Status != OrderStatus.Filled)
                        .OrderByDescending(p => p.BuyOrder.UpdatedAt ?? p.BuyOrder.CreatedAt)
                        .FirstOrDefault();
                    // Найти последнюю завершённую пару (оба Filled)
                    var lastCompletedPair = allPairs
                        .Where(p => p.UserId == user.Id && p.BuyOrder.Status == OrderStatus.Filled && p.SellOrder.Status == OrderStatus.Filled)
                        .OrderByDescending(p => p.SellOrder.UpdatedAt ?? p.SellOrder.CreatedAt)
                        .FirstOrDefault();
                    decimal? lastBuyPrice = unfinishedPair?.BuyOrder.Price ?? lastCompletedPair?.BuyOrder.Price;
                    _logger.LogInformation($"[DCA-DEBUG] user={user.Id} lastBuyPrice={lastBuyPrice}");
                    if (lastBuyPrice == null || lastBuyPrice <= 0)
                    {
                        _logger.LogInformation($"[DCA-DEBUG] user={user.Id} Нет lastBuyPrice, автозапуск первой покупки");
                        // Получить цену для первой покупки
                        decimal? firstBuyPrice = null;
                        var priceResult = await mexcService.GetSymbolPriceAsync("KASUSDT", stoppingToken);
                        if (priceResult.IsSuccess)
                            firstBuyPrice = priceResult.Value;
                        // Маркет-ордер на OrderAmount
                        var orderAmount = user.Settings.OrderAmount;
                        var buyOrder = new Order
                        {
                            Id = string.Empty,
                            Symbol = "KASUSDT",
                            Side = OrderSide.Buy,
                            Type = OrderType.Market,
                            Quantity = orderAmount,
                            Status = OrderStatus.New,
                            CreatedAt = DateTime.UtcNow
                        };
                        var orderPair = new OrderPair
                        {
                            Id = Guid.NewGuid().ToString(),
                            UserId = user.Id,
                            BuyOrder = buyOrder,
                            SellOrder = new Order { Id = string.Empty, Symbol = "KASUSDT", Side = OrderSide.Sell, Type = OrderType.Limit, Quantity = 0, Price = 0, Status = OrderStatus.New, CreatedAt = DateTime.UtcNow, QuantityFilled = 0, QuoteQuantityFilled = 0, Commission = 0 },
                            CreatedAt = DateTime.UtcNow
                        };
                        await orderPairRepo.AddAsync(orderPair);
                        var buyResult = await mexcService.PlaceOrderAsync("KASUSDT", OrderSide.Buy, OrderType.Market, orderAmount, null, TimeInForce.GoodTillCanceled, stoppingToken);
                        if (buyResult.IsSuccess)
                        {
                            buyOrder.Id = buyResult.Value;
                            buyOrder.Status = OrderStatus.Filled;
                            buyOrder.UpdatedAt = DateTime.UtcNow;
                            var orderInfo = await mexcService.GetOrderAsync("KASUSDT", buyOrder.Id, stoppingToken);
                            _logger.LogInformation($"[BUY-DEBUG] orderId={buyOrder.Id} status={orderInfo.Value.Status} qtyFilled={orderInfo.Value.QuantityFilled} quoteQtyFilled={orderInfo.Value.QuoteQuantityFilled} price={orderInfo.Value.Price} orderAmount={orderAmount} raw={System.Text.Json.JsonSerializer.Serialize(orderInfo.Value)}");
                            if (orderInfo.IsSuccess)
                            {
                                buyOrder.QuantityFilled = orderInfo.Value.QuantityFilled;
                                buyOrder.QuoteQuantityFilled = orderInfo.Value.QuoteQuantityFilled;
                                if (orderInfo.Value.QuantityFilled > 0 && orderInfo.Value.QuoteQuantityFilled > 0)
                                    buyOrder.Price = orderInfo.Value.QuoteQuantityFilled / orderInfo.Value.QuantityFilled;
                                else
                                    buyOrder.Price = firstBuyPrice;
                            }
                            else
                            {
                                buyOrder.Price = firstBuyPrice;
                            }
                            orderPair.BuyOrder = buyOrder;
                            await orderPairRepo.UpdateAsync(orderPair);
                            // --- Выставляем sell-ордер после первой покупки ---
                            var percentFirstBuy = user.Settings.PercentProfit / 100m;
                            var sellPrice = buyOrder.Price.GetValueOrDefault() * (1 + percentFirstBuy);
                            decimal minQty = Math.Ceiling((1m / sellPrice) * 1000m) / 1000m;
                            var sellQty = buyOrder.QuantityFilled;
                            if (sellQty * sellPrice < 1m)
                                sellQty = minQty;
                            var sellResult = await mexcService.PlaceOrderAsync("KASUSDT", OrderSide.Sell, OrderType.Limit, sellQty, sellPrice, TimeInForce.GoodTillCanceled, stoppingToken);
                            if (sellResult.IsSuccess)
                            {
                                orderPair.SellOrder.Id = sellResult.Value;
                                orderPair.SellOrder.Price = sellPrice;
                                orderPair.SellOrder.Quantity = sellQty;
                                orderPair.SellOrder.Status = OrderStatus.New;
                                orderPair.SellOrder.CreatedAt = DateTime.UtcNow;
                                await orderPairRepo.UpdateAsync(orderPair);
                                _logger.LogInformation($"[DCA-DEBUG] user={user.Id} Sell-ордер выставлен: {sellResult.Value} qty={sellQty} price={sellPrice}");
                            }
                            else
                            {
                                _logger.LogError($"[DCA-DEBUG] user={user.Id} Ошибка выставления sell-ордера: {string.Join(", ", sellResult.Errors.Select(e => e.Message))}");
                            }
                            // Обновляем lastBuyPrice в user.Settings
                            user.Settings.LastDcaBuyPrice = buyOrder.Price;
                            await userRepository.UpdateAsync(user);
                            // Отправка сообщения в Telegram
                            var botClient = scope.ServiceProvider.GetRequiredService<ITelegramBotClient>();
                            var msg = $"Автоматически совершена первая покупка для DCA:\n\n{buyOrder.QuantityFilled:F2} KAS по {buyOrder.Price:F6} USDT\nПотрачено: {(buyOrder.QuantityFilled * buyOrder.Price):F8} USDT";
                            await botClient.SendMessage(chatId: user.Id, text: msg);
                            // Отправка стандартного сообщения в Telegram
                            var stdMsg = $"КУПЛЕНО\n\n{buyOrder.QuantityFilled:F2} KAS по {buyOrder.Price:F6} USDT\n\nПотрачено\n{(buyOrder.QuantityFilled * buyOrder.Price):F8} USDT\n\nВЫСТАВЛЕНО\n\n{sellQty:F2} KAS по {sellPrice:F6} USDT";
                            await botClient.SendMessage(chatId: user.Id, text: stdMsg);
                        }
                        else
                        {
                            _logger.LogError($"[DCA-DEBUG] user={user.Id} Ошибка автозапуска первой покупки: {string.Join(", ", buyResult.Errors.Select(e => e.Message))}");
                        }
                    }
                    else
                    {
                        // Получаем текущую цену через WebSocket (с подстраховкой REST)
                        decimal? currentPrice = null;
                        var wsCts = new CancellationTokenSource();
                        var wsTask = mexcService._socketClient.SpotApi.SubscribeToMiniTickerUpdatesAsync(
                            "KASUSDT",
                            data => { currentPrice = data.Data.LastPrice; wsCts.Cancel(); },
                            wsCts.Token.ToString()
                        );
                        await Task.WhenAny(wsTask, Task.Delay(500, stoppingToken));
                        wsCts.Cancel();
                        if (currentPrice == null)
                        {
                            var priceResult = await mexcService.GetSymbolPriceAsync("KASUSDT", stoppingToken);
                            if (priceResult.IsSuccess)
                                currentPrice = priceResult.Value;
                        }
                        _logger.LogInformation($"[DCA-DEBUG] user={user.Id} currentPrice={currentPrice}");
                        if (currentPrice == null)
                        {
                            _logger.LogWarning($"[DCA-DEBUG] user={user.Id} Не удалось получить текущую цену, автоторговля пропущена");
                            await Task.Delay(2000, stoppingToken);
                            continue;
                        }
                        var percent = user.Settings.PercentProfit / 100m;
                        _logger.LogInformation($"[DCA-DEBUG] user={user.Id} percent={percent}");
                        if (currentPrice <= lastBuyPrice * (1 - percent))
                        {
                            _logger.LogInformation($"[DCA-DEBUG] user={user.Id} Условие автопокупки выполнено: currentPrice={currentPrice} <= {lastBuyPrice} * (1 - {percent})");
                            // Получить цену для первой покупки
                            decimal? firstBuyPrice = null;
                            var priceResult = await mexcService.GetSymbolPriceAsync("KASUSDT", stoppingToken);
                            if (priceResult.IsSuccess)
                                firstBuyPrice = priceResult.Value;
                            // Маркет-ордер на OrderAmount
                            var orderAmount = user.Settings.OrderAmount;
                            var buyOrder = new Order
                            {
                                Id = string.Empty,
                                Symbol = "KASUSDT",
                                Side = OrderSide.Buy,
                                Type = OrderType.Market,
                                Quantity = orderAmount,
                                Status = OrderStatus.New,
                                CreatedAt = DateTime.UtcNow
                            };
                            var orderPair = new OrderPair
                            {
                                Id = Guid.NewGuid().ToString(),
                                UserId = user.Id,
                                BuyOrder = buyOrder,
                                SellOrder = new Order { Id = string.Empty, Symbol = "KASUSDT", Side = OrderSide.Sell, Type = OrderType.Limit, Quantity = 0, Price = 0, Status = OrderStatus.New, CreatedAt = DateTime.UtcNow, QuantityFilled = 0, QuoteQuantityFilled = 0, Commission = 0 },
                                CreatedAt = DateTime.UtcNow
                            };
                            await orderPairRepo.AddAsync(orderPair);
                            var buyResult = await mexcService.PlaceOrderAsync("KASUSDT", OrderSide.Buy, OrderType.Market, orderAmount, null, TimeInForce.GoodTillCanceled, stoppingToken);
                            if (buyResult.IsSuccess)
                            {
                                buyOrder.Id = buyResult.Value;
                                buyOrder.Status = OrderStatus.Filled;
                                buyOrder.UpdatedAt = DateTime.UtcNow;
                                var orderInfo = await mexcService.GetOrderAsync("KASUSDT", buyOrder.Id, stoppingToken);
                                _logger.LogInformation($"[BUY-DEBUG] orderId={buyOrder.Id} status={orderInfo.Value.Status} qtyFilled={orderInfo.Value.QuantityFilled} quoteQtyFilled={orderInfo.Value.QuoteQuantityFilled} price={orderInfo.Value.Price} orderAmount={orderAmount} raw={System.Text.Json.JsonSerializer.Serialize(orderInfo.Value)}");
                                if (orderInfo.IsSuccess)
                                {
                                    buyOrder.QuantityFilled = orderInfo.Value.QuantityFilled;
                                    buyOrder.QuoteQuantityFilled = orderInfo.Value.QuoteQuantityFilled;
                                    if (orderInfo.Value.QuantityFilled > 0 && orderInfo.Value.QuoteQuantityFilled > 0)
                                        buyOrder.Price = orderInfo.Value.QuoteQuantityFilled / orderInfo.Value.QuantityFilled;
                                    else
                                        buyOrder.Price = firstBuyPrice;
                                }
                                else
                                {
                                    buyOrder.Price = firstBuyPrice;
                                }
                                orderPair.BuyOrder = buyOrder;
                                await orderPairRepo.UpdateAsync(orderPair);
                                // --- Выставляем sell-ордер после первой покупки ---
                                var percentFirstBuy = user.Settings.PercentProfit / 100m;
                                var sellPrice = buyOrder.Price.GetValueOrDefault() * (1 + percentFirstBuy);
                                decimal minQty = Math.Ceiling((1m / sellPrice) * 1000m) / 1000m;
                                var sellQty = buyOrder.QuantityFilled;
                                if (sellQty * sellPrice < 1m)
                                    sellQty = minQty;
                                var sellResult = await mexcService.PlaceOrderAsync("KASUSDT", OrderSide.Sell, OrderType.Limit, sellQty, sellPrice, TimeInForce.GoodTillCanceled, stoppingToken);
                                if (sellResult.IsSuccess)
                                {
                                    orderPair.SellOrder.Id = sellResult.Value;
                                    orderPair.SellOrder.Price = sellPrice;
                                    orderPair.SellOrder.Quantity = sellQty;
                                    orderPair.SellOrder.Status = OrderStatus.New;
                                    orderPair.SellOrder.CreatedAt = DateTime.UtcNow;
                                    await orderPairRepo.UpdateAsync(orderPair);
                                    _logger.LogInformation($"[DCA-DEBUG] user={user.Id} Sell-ордер выставлен: {sellResult.Value} qty={sellQty} price={sellPrice}");
                                }
                                else
                                {
                                    _logger.LogError($"[DCA-DEBUG] user={user.Id} Ошибка выставления sell-ордера: {string.Join(", ", sellResult.Errors.Select(e => e.Message))}");
                                }
                                // Обновляем lastBuyPrice в user.Settings
                                user.Settings.LastDcaBuyPrice = buyOrder.Price;
                                await userRepository.UpdateAsync(user);
                                // Отправка сообщения в Telegram
                                var botClient = scope.ServiceProvider.GetRequiredService<ITelegramBotClient>();
                                var msg = $"Автоматически совершена первая покупка для DCA:\n\n{buyOrder.QuantityFilled:F2} KAS по {buyOrder.Price:F6} USDT\nПотрачено: {(buyOrder.QuantityFilled * buyOrder.Price):F8} USDT";
                                await botClient.SendMessage(chatId: user.Id, text: msg);
                                // Отправка стандартного сообщения в Telegram
                                var stdMsg = $"КУПЛЕНО\n\n{buyOrder.QuantityFilled:F2} KAS по {buyOrder.Price:F6} USDT\n\nПотрачено\n{(buyOrder.QuantityFilled * buyOrder.Price):F8} USDT\n\nВЫСТАВЛЕНО\n\n{sellQty:F2} KAS по {sellPrice:F6} USDT";
                                await botClient.SendMessage(chatId: user.Id, text: stdMsg);
                            }
                            else
                            {
                                _logger.LogError($"[DCA-DEBUG] user={user.Id} Ошибка автозапуска первой покупки: {string.Join(", ", buyResult.Errors.Select(e => e.Message))}");
                            }
                        }
                        else
                        {
                            _logger.LogInformation($"[DCA-DEBUG] user={user.Id} Условие автопокупки НЕ выполнено: currentPrice={currentPrice} > {lastBuyPrice} * (1 - {percent})");
                        }
                    }
                    await Task.Delay(2000, stoppingToken);
                    continue;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"[DCA-DEBUG] user={user.Id} Ошибка в автоторговле: {ex}");
                    await Task.Delay(5000, stoppingToken);
                }
            }
        }
    }
} 