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
            using var scope = _serviceProvider.CreateScope();
            var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
            var users = await userRepository.GetAllAsync();
            _logger.LogInformation($"[DCA-DEBUG] Пользователей для автоторговли: {string.Join(", ", users.Select(u => u.Id))}");
            foreach (var user in users)
            {
                _ = Task.Run(() => RunForUser(user, loggerFactory, stoppingToken), stoppingToken);
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
                    // lastBuyPrice: всегда из последнего buy-ордера
                    decimal? lastBuyPrice = null;
                    var allPairs = await orderPairRepo.GetAllAsync();
                    var lastBuy = allPairs.Where(p => p.UserId == user.Id && p.BuyOrder.Status == OrderStatus.Filled && p.BuyOrder.Price > 0)
                        .OrderByDescending(p => p.BuyOrder.UpdatedAt ?? p.BuyOrder.CreatedAt)
                        .FirstOrDefault();
                    if (lastBuy != null)
                        lastBuyPrice = lastBuy.BuyOrder.Price;
                    _logger.LogInformation($"[DCA-DEBUG] user={user.Id} lastBuyPrice={lastBuyPrice}");
                    if (lastBuyPrice == null || lastBuyPrice <= 0)
                    {
                        _logger.LogWarning($"[DCA-DEBUG] user={user.Id} Нет валидного lastBuyPrice, автоторговля пропущена");
                        await Task.Delay(2000, stoppingToken);
                        continue;
                    }
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
                        // Если не пришло через WebSocket — берём через REST
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
                        // Создаём новую пару (маркет-бай + лимит-селл)
                        var orderAmount = user.Settings.OrderAmount;
                        var pairId = Guid.NewGuid().ToString();
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
                            Id = pairId,
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
                            // Получаем детали ордера
                            var orderInfo = await mexcService.GetOrderAsync("KASUSDT", buyOrder.Id, stoppingToken);
                            _logger.LogInformation($"[BUY-DEBUG] orderId={buyOrder.Id} status={orderInfo.Value.Status} qtyFilled={orderInfo.Value.QuantityFilled} quoteQtyFilled={orderInfo.Value.QuoteQuantityFilled} price={orderInfo.Value.Price} orderAmount={orderAmount} raw={System.Text.Json.JsonSerializer.Serialize(orderInfo.Value)}");
                            if (orderInfo.IsSuccess)
                            {
                                buyOrder.QuantityFilled = orderInfo.Value.QuantityFilled;
                                buyOrder.QuoteQuantityFilled = orderInfo.Value.QuoteQuantityFilled;
                                if (orderInfo.Value.QuantityFilled > 0 && orderInfo.Value.QuoteQuantityFilled > 0)
                                    buyOrder.Price = orderInfo.Value.QuoteQuantityFilled / orderInfo.Value.QuantityFilled;
                                else
                                    buyOrder.Price = orderInfo.Value.Price;
                            }
                            else
                            {
                                buyOrder.Price = currentPrice;
                            }
                            orderPair.BuyOrder = buyOrder;
                            await orderPairRepo.UpdateAsync(orderPair);
                            // Считаем цену продажи
                            var sellPrice = buyOrder.Price.GetValueOrDefault() * (1 + percent);
                            // Округляем количество вверх, чтобы сумма была >= 1 USDT
                            decimal minQty = Math.Ceiling((1m / sellPrice) * 1000m) / 1000m;
                            var sellQty = buyOrder.QuantityFilled;
                            if (sellQty * sellPrice < 1m)
                                sellQty = minQty;
                            // Выставляем sell-ордер
                            var sellResult = await mexcService.PlaceOrderAsync("KASUSDT", OrderSide.Sell, OrderType.Limit, sellQty, sellPrice, TimeInForce.GoodTillCanceled, stoppingToken);
                            if (sellResult.IsSuccess)
                            {
                                orderPair.SellOrder.Id = sellResult.Value;
                                orderPair.SellOrder.Price = sellPrice;
                                orderPair.SellOrder.Status = OrderStatus.New;
                                orderPair.SellOrder.CreatedAt = DateTime.UtcNow;
                                await orderPairRepo.UpdateAsync(orderPair);
                            }
                            // Проверка баланса KAS после выставления sell-ордера
                            var accResultKAS = await mexcService.GetAccountInfoAsync(stoppingToken);
                            if (accResultKAS.IsSuccess)
                            {
                                var kasFree = accResultKAS.Value.Balances.FirstOrDefault(b => b.Asset == "KAS")?.Available ?? 0m;
                                if (kasFree > 0.001m)
                                {
                                    var botClientAdmin = scope.ServiceProvider.GetRequiredService<ITelegramBotClient>();
                                    long adminId = 130822044;
                                    await botClientAdmin.SendMessage(chatId: adminId, text: $"[ALERT] На аккаунте обнаружен свободный KAS: {kasFree:F4}. Возможен рассинхрон между покупками и продажами!");
                                }
                            }
                            // Обновляем lastBuyPrice в user.Settings (для истории)
                            user.Settings.LastDcaBuyPrice = buyOrder.Price;
                            await userRepository.UpdateAsync(user);
                            // Отправка сообщения в Telegram
                            var botClient = scope.ServiceProvider.GetRequiredService<ITelegramBotClient>();
                            var percentStr = user.Settings.PercentProfit.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                            var msg = $"Цена упала на {percentStr}%\n\nКУПЛЕНО\n\n{buyOrder.QuantityFilled:F2} KAS по {buyOrder.Price:F6} USDT\n\nПотрачено\n{(buyOrder.QuantityFilled * buyOrder.Price):F8} USDT\n\nВЫСТАВЛЕНО\n\n{buyOrder.QuantityFilled:F2} KAS по {sellPrice:F6} USDT";
                            await botClient.SendMessage(chatId: user.Id, text: msg);
                        }
                    }
                    else
                    {
                        _logger.LogInformation($"[DCA-DEBUG] user={user.Id} Условие автопокупки НЕ выполнено: currentPrice={currentPrice} > {lastBuyPrice} * (1 - {percent})");
                    }
                    await Task.Delay(2000, stoppingToken);
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