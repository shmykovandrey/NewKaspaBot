using FluentResults;
using KaspaBot.Application.Trading.Commands;
using MediatR;
using Mexc.Net.Enums;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using KaspaBot.Infrastructure.Repositories;
using KaspaBot.Infrastructure.Services;
using System.Globalization;
using KaspaBot.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using KaspaBot.Domain.Interfaces;
using System.Reflection;

namespace KaspaBot.Presentation.Telegram.CommandHandlers
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class BotCommandAttribute : Attribute
    {
        public string Description { get; }
        public bool AdminOnly { get; set; }
        public string? UserIdParameter { get; set; }

        public BotCommandAttribute(string description)
        {
            Description = description;
        }
    }

    public class TradingCommandHandler
    {
        private readonly IMediator _mediator;
        private readonly ITelegramBotClient _botClient;
        private readonly ILogger<TradingCommandHandler> _logger;
        private readonly IServiceProvider _serviceProvider;

        public TradingCommandHandler(
            IMediator mediator,
            ITelegramBotClient botClient,
            ILogger<TradingCommandHandler> logger,
            IServiceProvider serviceProvider)
        {
            _mediator = mediator;
            _botClient = botClient;
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        [BotCommand("Купить KASUSDT по рынку")]
        public async Task HandleBuyCommand(Message message, CancellationToken cancellationToken)
        {
            var userId = message.Chat.Id;
            using var scope = _serviceProvider.CreateScope();
            var userRepository = scope.ServiceProvider.GetRequiredService<KaspaBot.Domain.Interfaces.IUserRepository>();
            var orderPairRepo = scope.ServiceProvider.GetRequiredService<OrderPairRepository>();
            var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
            var mexcLogger = loggerFactory.CreateLogger<MexcService>();
            var user = await userRepository.GetByIdAsync(userId);
            if (user == null)
            {
                await _botClient.SendMessage(chatId: userId, text: "Пользователь не найден.", cancellationToken: cancellationToken);
                return;
            }
            var mexcService = new MexcService(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, mexcLogger);
            var orderAmount = user.Settings.OrderAmount;
            _logger.LogInformation($"[BUY DEBUG] userId={userId} OrderAmount={orderAmount}");
            if (orderAmount < 1)
                orderAmount = 1;
            // 1. Сохраняем OrderPair в статусе Pending
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
                UserId = userId,
                BuyOrder = buyOrder,
                SellOrder = new Order { Id = string.Empty, Symbol = "KASUSDT", Side = OrderSide.Sell, Type = OrderType.Limit, Quantity = 0, Price = 0, Status = OrderStatus.New, CreatedAt = DateTime.UtcNow, QuantityFilled = 0, QuoteQuantityFilled = 0, Commission = 0 },
                CreatedAt = DateTime.UtcNow
            };
            await orderPairRepo.AddAsync(orderPair);
            // 2. Размещаем маркет-ордер на покупку
            var buyResult = await mexcService.PlaceOrderAsync("KASUSDT", OrderSide.Buy, OrderType.Market, orderAmount, null, TimeInForce.GoodTillCanceled, cancellationToken);
            if (!buyResult.IsSuccess)
            {
                await _botClient.SendMessage(chatId: userId, text: $"❌ <b>Ошибка покупки:</b> {buyResult.Errors.FirstOrDefault()?.Message}", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                return;
            }
            buyOrder.Id = buyResult.Value;
            buyOrder.Status = OrderStatus.Filled; // Для MVP считаем что маркет-ордер исполнился сразу
            buyOrder.UpdatedAt = DateTime.UtcNow;
            // Получаем детали ордера для комиссии и фактической цены
            var orderInfo = await mexcService.GetOrderAsync("KASUSDT", buyOrder.Id, cancellationToken);
            if (!orderInfo.IsSuccess)
            {
                _logger.LogError($"[ORDER DEBUG] GetOrderAsync failed: orderId={buyOrder.Id}, errors={string.Join(", ", orderInfo.Errors.Select(e => e.Message))}");
            }
            decimal buyPrice = 0, buyQty = 0;
            decimal totalCommission = 0;
            string commissionAsset = "";
            if (orderInfo.IsSuccess)
            {
                // Если это market-buy и есть оба значения — делим
                if (orderInfo.Value.QuantityFilled > 0 && orderInfo.Value.QuoteQuantityFilled > 0)
                {
                    buyPrice = orderInfo.Value.QuoteQuantityFilled / orderInfo.Value.QuantityFilled;
                }
                else
                {
                    buyPrice = orderInfo.Value.Price;
                }
                buyQty = orderInfo.Value.QuantityFilled;
                // Получаем трейды для комиссии и fallback цены
                var tradesResult = await mexcService.GetOrderTradesAsync("KASUSDT", buyOrder.Id, cancellationToken);
                if (tradesResult.IsSuccess)
                {
                    foreach (var trade in tradesResult.Value)
                    {
                        totalCommission += trade.Fee;
                        commissionAsset = trade.FeeAsset;
                        if (buyPrice == 0 && trade.Price > 0)
                            buyPrice = trade.Price;
                    }
                }
                else
                {
                    _logger.LogError($"[ORDER DEBUG] GetOrderTradesAsync failed: orderId={buyOrder.Id}, errors={string.Join(", ", tradesResult.Errors.Select(e => e.Message))}");
                }
            }
            // 3. Обновляем OrderPair с buy-ордером
            buyOrder.Price = buyPrice;
            buyOrder.QuantityFilled = buyQty;
            buyOrder.Commission = totalCommission;
            buyOrder.Status = OrderStatus.Filled;
            orderPair.BuyOrder = buyOrder;
            await orderPairRepo.UpdateAsync(orderPair);
            // Получаем tickSize для KASUSDT
            var tickSize = await mexcService.GetTickSizeAsync("KASUSDT", cancellationToken);
            _logger.LogError($"[SELL DEBUG] tickSize for KASUSDT: {tickSize}");
            // 4. Считаем цену продажи только с учётом профита (без комиссии)
            var percentProfit = user.Settings.PercentProfit / 100m;
            _logger.LogError($"[SELL DEBUG] percentProfit={percentProfit}, user.Settings.PercentProfit={user.Settings.PercentProfit}");
            var sellPrice = buyPrice * (1 + percentProfit);
            // Округление вниз до кратного tickSize
            sellPrice = Math.Floor(sellPrice / tickSize) * tickSize;
            var sellPriceStr = sellPrice.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
            _logger.LogError($"[SELL DEBUG] Формула sellPrice: buyPrice={buyPrice} * (1 + percentProfit) = {sellPriceStr}");
            // Округление количества вниз до кратного baseSizePrecision
            decimal baseStep = 0.001m;
            decimal sellQty = Math.Floor(buyQty / baseStep) * baseStep;
            var sellOrder = new Order
            {
                Id = string.Empty,
                Symbol = "KASUSDT",
                Side = OrderSide.Sell,
                Type = OrderType.Limit,
                Quantity = sellQty,
                Price = decimal.Parse(sellPriceStr, System.Globalization.CultureInfo.InvariantCulture),
                Status = OrderStatus.New,
                CreatedAt = DateTime.UtcNow
            };
            // 5. Выставляем sell-ордер
            _logger.LogError($"[SELL DEBUG] sellOrder.Price type: {sellOrder.Price?.GetType()}, value: {sellOrder.Price}");
            _logger.LogError($"[SELL DEBUG] sellOrder.Quantity type: {sellOrder.Quantity.GetType()}, value: {sellOrder.Quantity}");
            _logger.LogError($"[SELL DEBUG] Перед PlaceOrderAsync: buyPrice={buyPrice}, sellOrder.Price={sellOrder.Price}, sellOrder.Quantity={sellOrder.Quantity}");
            _logger.LogError($"[SELL DEBUG] PlaceOrderAsync params: symbol=KASUSDT, side=Sell, type=Limit, qty={sellOrder.Quantity}, price={sellOrder.Price}");
            try
            {
                var sellResult = await mexcService.PlaceOrderAsync("KASUSDT", OrderSide.Sell, OrderType.Limit, sellQty, sellOrder.Price, TimeInForce.GoodTillCanceled, cancellationToken);
                if (!sellResult.IsSuccess)
                {
                    await _botClient.SendMessage(chatId: userId, text: $"Ошибка выставления продажи: {sellResult.Errors.FirstOrDefault()?.Message}", cancellationToken: cancellationToken);
                    return;
                }
                sellOrder.Id = sellResult.Value;
                sellOrder.Status = OrderStatus.New;
                orderPair.SellOrder = sellOrder;
                await orderPairRepo.UpdateAsync(orderPair);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[SELL DEBUG] Exception при выставлении sell-ордера: buyPrice={buyPrice}, sellOrder.Price={sellOrder.Price}");
                await _botClient.SendMessage(chatId: userId, text: $"Ошибка при выставлении ордера: {ex.Message}", cancellationToken: cancellationToken);
                return;
            }
            // 6. Информируем пользователя
            var stdMsg = $"КУПЛЕНО\n\n{buyQty:F2} KAS по {buyPrice:F6} USDT\n\nПотрачено\n{(buyQty * buyPrice):F8} USDT\n\nВЫСТАВЛЕНО\n\n{sellQty:F2} KAS по {sellOrder.Price:F6} USDT";
            await _botClient.SendMessage(chatId: userId, text: stdMsg, cancellationToken: cancellationToken);

            // После успешной покупки:
            // Если автоторговля включена — обновить цену последнего buy-ордера для DCA
            var userSettings = await userRepository.GetByIdAsync(userId);
            if (userSettings != null && userSettings.Settings.IsAutoTradeEnabled && buyOrder.Price > 0)
            {
                // Обновить цену последнего buy-ордера (можно просто сохранить buyOrder.Price в user.Settings, если нужно)
                userSettings.Settings.LastDcaBuyPrice = buyOrder.Price;
                await userRepository.UpdateAsync(userSettings);
            }
        }

        [BotCommand("Продать весь KAS по рынку")]
        public async Task HandleSellCommand(Message message, CancellationToken cancellationToken)
        {
            var userId = message.Chat.Id;
            using var scope = _serviceProvider.CreateScope();
            var userRepository = scope.ServiceProvider.GetRequiredService<KaspaBot.Domain.Interfaces.IUserRepository>();
            var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
            var mexcLogger = loggerFactory.CreateLogger<MexcService>();
            var user = await userRepository.GetByIdAsync(userId);
            if (user == null)
            {
                await _botClient.SendMessage(chatId: userId, text: "Пользователь не найден.", cancellationToken: cancellationToken);
                return;
            }
            var mexcService = new MexcService(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, mexcLogger);
            // Получаем баланс KAS
            var accResult = await mexcService.GetAccountInfoAsync(cancellationToken);
            if (!accResult.IsSuccess)
            {
                await _botClient.SendMessage(chatId: userId, text: $"❌ <b>Ошибка получения баланса:</b> {accResult.Errors.FirstOrDefault()?.Message}", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                return;
            }
            var kasBalance = accResult.Value.Balances.FirstOrDefault(b => b.Asset == "KAS")?.Available ?? 0m;
            if (kasBalance <= 0)
            {
                await _botClient.SendMessage(chatId: userId, text: "Нет свободных KAS для продажи.", cancellationToken: cancellationToken);
                return;
            }
            // Продаём весь доступный KAS
            var sellResult = await mexcService.PlaceOrderAsync("KASUSDT", OrderSide.Sell, OrderType.Market, kasBalance, null, TimeInForce.GoodTillCanceled, cancellationToken);
            if (!sellResult.IsSuccess)
            {
                await _botClient.SendMessage(chatId: userId, text: $"❌ <b>Ошибка продажи:</b> {sellResult.Errors.FirstOrDefault()?.Message}", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                return;
            }
            await _botClient.SendMessage(chatId: userId, text: $"✅ <b>Продано {kasBalance:F2} KAS по рынку</b>", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
        }

        [BotCommand("Продать X KAS по рынку")]
        public async Task HandleSellAmountCommand(Message message, decimal quantity, CancellationToken cancellationToken)
        {
            var userId = message.Chat.Id;
            using var scope = _serviceProvider.CreateScope();
            var userRepository = scope.ServiceProvider.GetRequiredService<KaspaBot.Domain.Interfaces.IUserRepository>();
            var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
            var mexcLogger = loggerFactory.CreateLogger<MexcService>();
            var user = await userRepository.GetByIdAsync(userId);
            if (user == null)
            {
                await _botClient.SendMessage(chatId: userId, text: "Пользователь не найден.", cancellationToken: cancellationToken);
                return;
            }
            var mexcService = new MexcService(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, mexcLogger);
            // Получаем баланс KAS
            var accResult = await mexcService.GetAccountInfoAsync(cancellationToken);
            if (!accResult.IsSuccess)
            {
                await _botClient.SendMessage(chatId: userId, text: $"❌ <b>Ошибка получения баланса:</b> {accResult.Errors.FirstOrDefault()?.Message}", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                return;
            }
            var kasBalance = accResult.Value.Balances.FirstOrDefault(b => b.Asset == "KAS")?.Available ?? 0m;
            if (quantity <= 0)
            {
                await _botClient.SendMessage(chatId: userId, text: "Количество для продажи должно быть больше 0.", cancellationToken: cancellationToken);
                return;
            }
            if (kasBalance < quantity)
            {
                await _botClient.SendMessage(chatId: userId, text: $"❌ <b>Недостаточно KAS.</b> Баланс: {kasBalance:F2}, запрошено: {quantity:F2}.", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                return;
            }
            // Продаём указанное количество
            var sellResult = await mexcService.PlaceOrderAsync("KASUSDT", OrderSide.Sell, OrderType.Market, quantity, null, TimeInForce.GoodTillCanceled, cancellationToken);
            if (!sellResult.IsSuccess)
            {
                await _botClient.SendMessage(chatId: userId, text: $"❌ <b>Ошибка продажи:</b> {sellResult.Errors.FirstOrDefault()?.Message}", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                return;
            }
            await _botClient.SendMessage(chatId: userId, text: $"✅ <b>Продано {quantity:F2} KAS по рынку</b>", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
        }

        [BotCommand("Статистика активных ордеров")]
        public async Task HandleStatusCommand(Message message, CancellationToken cancellationToken)
        {
            var userId = message.Chat.Id;
            using var scope = _serviceProvider.CreateScope();
            var orderPairRepo = scope.ServiceProvider.GetRequiredService<OrderPairRepository>();
            var mexcLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<MexcService>();
            var user = await scope.ServiceProvider.GetRequiredService<KaspaBot.Domain.Interfaces.IUserRepository>().GetByIdAsync(userId);
            if (user == null)
            {
                await _botClient.SendMessage(chatId: userId, text: "Пользователь не найден", cancellationToken: cancellationToken);
                return;
            }
            var mexcService = new MexcService(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, mexcLogger);
            decimal makerFee = 0.001m, takerFee = 0.001m;
            var feeResult = await mexcService.GetTradeFeeAsync("KASUSDT", cancellationToken);
            if (feeResult.IsSuccess)
            {
                makerFee = feeResult.Value.Maker;
                takerFee = feeResult.Value.Taker;
            }
            var userPairs = await orderPairRepo.GetOpenByUserIdAsync(userId);
            if (!userPairs.Any())
            {
                await _botClient.SendMessage(chatId: userId, text: "Нет активных ордеров на продажу", cancellationToken: cancellationToken);
                return;
            }
            var sellOrders = userPairs.Where(p => p.SellOrder.Status == OrderStatus.New).ToList();
            if (!sellOrders.Any())
            {
                await _botClient.SendMessage(chatId: userId, text: "Нет активных ордеров на продажу", cancellationToken: cancellationToken);
                return;
            }
            var totalSum = sellOrders.Sum(p => p.SellOrder.Quantity * (p.SellOrder.Price ?? 0m));
            var currentPrice = await mexcService.GetSymbolPriceAsync("KASUSDT", cancellationToken);
            var currentPriceValue = currentPrice.IsSuccess ? currentPrice.Value : 0m;
            var autotradeStatus = user.Settings.IsAutoTradeEnabled ? "🟢 Автоторговля включена" : "🔴 Автоторговля выключена";
            var autoBuyInfo = "";
            if (user.Settings.IsAutoTradeEnabled && user.Settings.LastDcaBuyPrice.HasValue)
            {
                var priceChange = 100m * (currentPriceValue - user.Settings.LastDcaBuyPrice.Value) / user.Settings.LastDcaBuyPrice.Value;
                autoBuyInfo = $"\n\ud83d\udcc9 До автопокупки: {priceChange:F2}% (реальная цель: {user.Settings.PercentPriceChange:F6})";
            }
            var rows = sellOrders.Select((o, i) => (
                Index: i + 1,
                Qty: o.Quantity,
                Price: o.Price.GetValueOrDefault(),
                Sum: o.Quantity * o.Price.GetValueOrDefault(),
                Deviation: currentPriceValue > 0 ? 100m * ((o.Price.GetValueOrDefault()) - currentPriceValue) / currentPriceValue : 0m
            )).ToList();
            var text = NotificationFormatter.StatTable(rows, totalSum, currentPriceValue, autotradeStatus, autoBuyInfo, sellOrders.Count);
            await _botClient.SendMessage(chatId: userId, text: text, parseMode: ParseMode.Html, cancellationToken: cancellationToken);
        }

        [BotCommand("Таблица открытых ордеров")]
        public async Task HandleStatCommand(Message message, CancellationToken cancellationToken)
        {
            var userId = message.Chat.Id;
            using var scope = _serviceProvider.CreateScope();
            var orderPairRepo = scope.ServiceProvider.GetRequiredService<OrderPairRepository>();
            var mexcLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<MexcService>();
            var user = await scope.ServiceProvider.GetRequiredService<KaspaBot.Domain.Interfaces.IUserRepository>().GetByIdAsync(userId);
            if (user == null)
            {
                await _botClient.SendMessage(chatId: userId, text: "Пользователь не найден", cancellationToken: cancellationToken);
                return;
            }
            var mexcService = new MexcService(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, mexcLogger);
            var allPairs = await orderPairRepo.GetAllAsync();
            var userPairs = allPairs.Where(p => p.UserId == userId && !string.IsNullOrEmpty(p.SellOrder.Id)).ToList();
            var sellOrders = userPairs.Select(p => p.SellOrder).Where(o => o.Status == OrderStatus.New).OrderByDescending(o => o.Price).ToList();
            if (!sellOrders.Any())
            {
                await _botClient.SendMessage(chatId: userId, text: "У вас нет открытых ордеров на продажу.", cancellationToken: cancellationToken);
                return;
            }
            var currentPriceResult = await mexcService.GetSymbolPriceAsync("KASUSDT", cancellationToken);
            var currentPrice = currentPriceResult.IsSuccess ? currentPriceResult.Value : 0m;
            var totalSum = sellOrders.Sum(o => o.Quantity * o.Price.GetValueOrDefault());
            var autotradeStatus = user.Settings.IsAutoTradeEnabled ? "🟢 Автоторговля включена" : "🔴 Автоторговля выключена";
            var autoBuyInfo = "";
            if (user.Settings.IsAutoTradeEnabled && user.Settings.LastDcaBuyPrice.HasValue)
            {
                var priceChange = 100m * (currentPrice - user.Settings.LastDcaBuyPrice.Value) / user.Settings.LastDcaBuyPrice.Value;
                autoBuyInfo = $"\n\ud83d\udcc9 До автопокупки: {priceChange:F2}% (реальная цель: {user.Settings.PercentPriceChange:F6})";
            }
            var rows = sellOrders.Select((o, i) => (
                Index: i + 1,
                Qty: o.Quantity,
                Price: o.Price.GetValueOrDefault(),
                Sum: o.Quantity * o.Price.GetValueOrDefault(),
                Deviation: currentPrice > 0 ? 100m * (o.Price.GetValueOrDefault() - currentPrice) / currentPrice : 0m
            )).ToList();
            var text = NotificationFormatter.StatTable(rows, totalSum, currentPrice, autotradeStatus, autoBuyInfo, sellOrders.Count);
            await _botClient.SendMessage(chatId: userId, text: text, parseMode: ParseMode.Html, cancellationToken: cancellationToken);
        }

        [BotCommand("Таблица профита")]
        public async Task HandleProfitCommand(Message message, CancellationToken cancellationToken)
        {
            var userId = message.Chat.Id;
            using var scope = _serviceProvider.CreateScope();
            var orderPairRepo = scope.ServiceProvider.GetRequiredService<OrderPairRepository>();
            var allPairs = await orderPairRepo.GetAllAsync();
            var userPairs = allPairs.Where(p => p.UserId == userId && p.CompletedAt != null && p.Profit != null).ToList();
            if (userPairs.Count == 0)
            {
                await _botClient.SendMessage(chatId: userId, text: "Нет завершённых сделок.", cancellationToken: cancellationToken);
                return;
            }
            var now = DateTime.UtcNow.Date;
            var yesterday = now.AddDays(-1);
            var weekAgo = now.AddDays(-7);
            var byDay = userPairs
                .GroupBy(p => p.CompletedAt.Value.Date)
                .OrderByDescending(g => g.Key)
                .Take(7)
                .ToList();
            var weekPairs = userPairs.Where(p => p.CompletedAt.Value.Date > weekAgo);
            var allProfit = userPairs.Sum(p => p.Profit.GetValueOrDefault());
            var allCount = userPairs.Count;
            var weekProfit = weekPairs.Sum(p => p.Profit.GetValueOrDefault());
            var weekCount = weekPairs.Count();
            var table = "Дата       | Профит   | Кол-во сделок\n-------------------------------------\n";
            foreach (var g in byDay)
            {
                var dateStr = g.Key == yesterday ? "вчера" : g.Key.ToString("dd.MM");
                var profit = g.Sum(p => p.Profit.GetValueOrDefault());
                var count = g.Count();
                table += $"{dateStr,-10}|{profit,9:F2} |{count,13}\n";
            }
            table += "-------------------------------------\n";
            table += $"За неделю  |{weekProfit,9:F2} |{weekCount,13}\n\n";
            table += $"За все время|{allProfit,9:F2} |{allCount,13}";
            var msg = "Полный профит\n\n" + table;
            await _botClient.SendMessage(chatId: userId, text: msg, cancellationToken: cancellationToken);
        }

        [BotCommand("Сводка по комиссиям")]
        public async Task HandleFeeCommand(Message message, CancellationToken cancellationToken)
        {
            var userId = message.Chat.Id;
            using var scope = _serviceProvider.CreateScope();
            var orderPairRepo = scope.ServiceProvider.GetRequiredService<OrderPairRepository>();
            var allPairs = await orderPairRepo.GetAllAsync();
            var userPairs = allPairs.Where(p => p.UserId == userId && p.BuyOrder.Status == OrderStatus.Filled && p.SellOrder.Status == OrderStatus.Filled).ToList();
            if (!userPairs.Any())
            {
                await _botClient.SendMessage(chatId: userId, text: "Нет завершённых сделок для анализа.", cancellationToken: cancellationToken);
                return;
            }
            var totalCommission = userPairs.Sum(p => p.BuyOrder.Commission + p.SellOrder.Commission);
            var totalProfit = userPairs.Sum(p => p.Profit.GetValueOrDefault());
            var count = userPairs.Count;
            var avgCommission = count > 0 ? totalCommission / count : 0m;
            var commissionToProfitRatio = totalProfit > 0 ? totalCommission / totalProfit * 100m : 0m;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("💰 <b>Сводка по комиссиям</b>\n");
            sb.AppendLine($"Всего комиссий: <b>{totalCommission:F6} USDT</b>");
            sb.AppendLine($"Всего профита: <b>{totalProfit:F6} USDT</b>");
            sb.AppendLine($"Комиссия/Профит: <b>{commissionToProfitRatio:F1}%</b>");
            sb.AppendLine($"Сделок: <b>{count}</b>");
            sb.AppendLine($"Средняя комиссия: <b>{avgCommission:F6} USDT</b>");
            await _botClient.SendMessage(chatId: userId, text: sb.ToString(), cancellationToken: cancellationToken);
        }

        [BotCommand("Дамп ордера", AdminOnly = true)]
        public async Task HandleRestCommand(Message message, CancellationToken cancellationToken)
        {
            var userId = message.Chat.Id;
            var text = message.Text?.Trim();
            var parts = text?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts == null || parts.Length < 2)
            {
                await _botClient.SendMessage(chatId: userId, text: "Используй: /rest {orderId}", cancellationToken: cancellationToken);
                return;
            }
            var orderId = parts[1];
            using var scope = _serviceProvider.CreateScope();
            var userRepository = scope.ServiceProvider.GetRequiredService<KaspaBot.Domain.Interfaces.IUserRepository>();
            var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
            var mexcLogger = loggerFactory.CreateLogger<MexcService>();
            var user = await userRepository.GetByIdAsync(userId);
            if (user == null)
            {
                await _botClient.SendMessage(chatId: userId, text: "Пользователь не найден", cancellationToken: cancellationToken);
                return;
            }
            var mexcService = new MexcService(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, mexcLogger);
            var orderResult = await mexcService.GetOrderAsync("KASUSDT", orderId, cancellationToken);
            var rawJson = System.Text.Json.JsonSerializer.Serialize(orderResult);
            mexcLogger.LogError($"[REST CMD RAW] {rawJson}");
            await _botClient.SendMessage(chatId: userId, text: orderResult.IsSuccess ? "Данные ордера залогированы" : $"Ошибка: {string.Join(", ", orderResult.Errors.Select(e => e.Message))}", cancellationToken: cancellationToken);
        }

        [BotCommand("Проверить статусы ордеров", AdminOnly = true)]
        public async Task HandleCheckOrdersCommand(Message message, CancellationToken cancellationToken)
        {
            var userId = message.Chat.Id;
            _logger.LogInformation($"[CHECK-ORDERS] user={userId} старт проверки ордеров");
            using var scope = _serviceProvider.CreateScope();
            var orderPairRepo = scope.ServiceProvider.GetRequiredService<OrderPairRepository>();
            var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
            var mexcLogger = loggerFactory.CreateLogger<MexcService>();
            var userRepository = scope.ServiceProvider.GetRequiredService<KaspaBot.Domain.Interfaces.IUserRepository>();
            var user = await userRepository.GetByIdAsync(userId);
            if (user == null)
            {
                await _botClient.SendMessage(chatId: userId, text: "Пользователь не найден", cancellationToken: cancellationToken);
                return;
            }
            var mexcService = new MexcService(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, mexcLogger);
            var allPairs = await orderPairRepo.GetAllAsync();
            var userPairs = allPairs.Where(p => p.UserId == userId && !string.IsNullOrEmpty(p.BuyOrder.Id)).ToList();
            int checkedCount = 0, mismatchCount = 0, fixedCount = 0;
            var sb = new System.Text.StringBuilder();
            foreach (var pair in userPairs)
            {
                var buyOrder = pair.BuyOrder;
                var sellOrder = pair.SellOrder;
                if (!string.IsNullOrEmpty(buyOrder.Id))
                {
                    checkedCount++;
                    var buyResult = await mexcService.GetOrderAsync("KASUSDT", buyOrder.Id, cancellationToken);
                    if (buyResult.IsSuccess && buyResult.Value.Status != buyOrder.Status)
                    {
                        mismatchCount++;
                        sb.AppendLine($"BUY: {buyOrder.Id} - {buyOrder.Status} -> {buyResult.Value.Status}");
                        buyOrder.Status = buyResult.Value.Status;
                        buyOrder.QuantityFilled = buyResult.Value.QuantityFilled;
                        buyOrder.Price = buyResult.Value.Price;
                        fixedCount++;
                    }
                }
                if (!string.IsNullOrEmpty(sellOrder.Id))
                {
                    checkedCount++;
                    var sellResult = await mexcService.GetOrderAsync("KASUSDT", sellOrder.Id, cancellationToken);
                    if (sellResult.IsSuccess && sellResult.Value.Status != sellOrder.Status)
                    {
                        mismatchCount++;
                        sb.AppendLine($"SELL: {sellOrder.Id} - {sellOrder.Status} -> {sellResult.Value.Status}");
                        sellOrder.Status = sellResult.Value.Status;
                        sellOrder.QuantityFilled = sellResult.Value.QuantityFilled;
                        sellOrder.Price = sellResult.Value.Price;
                        fixedCount++;
                    }
                }
            }
            foreach (var pair in userPairs)
            {
                await orderPairRepo.UpdateAsync(pair);
            }
            var resultMsg = $"Проверено ордеров: {checkedCount}\nНесоответствий: {mismatchCount}\nИсправлено: {fixedCount}\n\n" + sb.ToString();
            if (resultMsg.Length > 3500)
                resultMsg = resultMsg.Substring(0, 3500) + "\n... (обрезано)";
            await _botClient.SendMessage(chatId: userId, text: resultMsg, cancellationToken: cancellationToken);
        }

        [BotCommand("Отменить ордера", AdminOnly = true)]
        public async Task HandleCancelOrdersCommand(Message message, CancellationToken cancellationToken)
        {
            var userId = message.Chat.Id;
            var text = message.Text?.Trim();
            var parts = text?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var mode = (parts != null && parts.Length > 1) ? parts[1].ToLower() : "all";
            using var scope = _serviceProvider.CreateScope();
            var orderPairRepo = scope.ServiceProvider.GetRequiredService<OrderPairRepository>();
            var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
            var mexcLogger = loggerFactory.CreateLogger<MexcService>();
            var userRepository = scope.ServiceProvider.GetRequiredService<KaspaBot.Domain.Interfaces.IUserRepository>();
            var user = await userRepository.GetByIdAsync(userId);
            if (user == null)
            {
                await _botClient.SendMessage(chatId: userId, text: "Пользователь не найден", cancellationToken: cancellationToken);
                return;
            }
            var mexcService = new MexcService(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, mexcLogger);
            var openOrdersResult = await mexcService.GetOpenOrdersAsync("KASUSDT", cancellationToken);
            if (!openOrdersResult.IsSuccess)
            {
                await _botClient.SendMessage(chatId: userId, text: "Ошибка получения открытых ордеров", cancellationToken: cancellationToken);
                return;
            }
            var allPairs = await orderPairRepo.GetAllAsync();
            var userPairs = allPairs.Where(p => p.UserId == userId).ToList();
            var userOrderIds = userPairs.SelectMany(p => new[] { p.BuyOrder.Id, p.SellOrder.Id }).Where(id => !string.IsNullOrEmpty(id)).ToList();
            var openOrders = openOrdersResult.Value.Where(o => userOrderIds.Contains(o.OrderId)).ToList();
            if (!openOrders.Any())
            {
                await _botClient.SendMessage(chatId: userId, text: "У вас нет открытых ордеров", cancellationToken: cancellationToken);
                return;
            }
            var cancelled = 0;
            var failed = 0;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("🔄 <b>Отмена ордеров</b>\n");
            foreach (var order in openOrders)
            {
                var cancelResult = await mexcService.CancelOrderAsync("KASUSDT", order.OrderId, cancellationToken);
                if (cancelResult.IsSuccess)
                {
                    sb.AppendLine($"✅ Отменён ордер {order.OrderId}");
                    sb.AppendLine($"   {order.Side} {order.Quantity} KAS по {order.Price} USDT");
                    cancelled++;
                }
                else
                {
                    sb.AppendLine($"❌ Ошибка отмены ордера {order.OrderId}");
                    sb.AppendLine($"   {string.Join(", ", cancelResult.Errors.Select(e => e.Message))}");
                    failed++;
                }
                sb.AppendLine();
            }
            var resultMsg = $"📊 <b>Результат:</b>\n✅ Отменено: {cancelled}\n❌ Ошибок: {failed}\n\n" + sb.ToString();
            if (resultMsg.Length > 4000)
                resultMsg = resultMsg.Substring(0, 4000) + "\n... (сообщение обрезано)";
            await _botClient.SendMessage(chatId: userId, text: resultMsg, cancellationToken: cancellationToken);
        }

        [BotCommand("Закрыть все открытые ордера")]
        public async Task HandleCloseAllOrdersCommand(Message message, CancellationToken cancellationToken)
        {
            var userId = message.Chat.Id;
            using var scope = _serviceProvider.CreateScope();
            var userRepository = scope.ServiceProvider.GetRequiredService<KaspaBot.Domain.Interfaces.IUserRepository>();
            var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
            var mexcLogger = loggerFactory.CreateLogger<MexcService>();
            var user = await userRepository.GetByIdAsync(userId);
            if (user == null)
            {
                await _botClient.SendMessage(chatId: userId, text: "❌ Пользователь не найден", cancellationToken: cancellationToken);
                return;
            }
            var mexcService = new MexcService(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, mexcLogger);
            var openOrdersResult = await mexcService.GetOpenOrdersAsync("KASUSDT", cancellationToken);
            if (!openOrdersResult.IsSuccess)
            {
                await _botClient.SendMessage(chatId: userId, text: $"❌ Ошибка получения открытых ордеров: {openOrdersResult.Errors.FirstOrDefault()?.Message}", cancellationToken: cancellationToken);
                return;
            }
            var openOrders = openOrdersResult.Value.ToList();
            if (!openOrders.Any())
            {
                await _botClient.SendMessage(chatId: userId, text: "✅ У вас нет открытых ордеров", cancellationToken: cancellationToken);
                return;
            }
            var cancelled = 0;
            var failed = 0;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("🔄 <b>Закрытие открытых ордеров</b>\n");
            foreach (var order in openOrders)
            {
                var cancelResult = await mexcService.CancelOrderAsync("KASUSDT", order.OrderId, cancellationToken);
                if (cancelResult.IsSuccess)
                {
                    sb.AppendLine($"✅ Отменён ордер {order.OrderId}");
                    sb.AppendLine($"   {order.Side} {order.Quantity} KAS по {order.Price} USDT");
                    cancelled++;
                }
                else
                {
                    sb.AppendLine($"❌ Ошибка отмены ордера {order.OrderId}");
                    sb.AppendLine($"   {string.Join(", ", cancelResult.Errors.Select(e => e.Message))}");
                    failed++;
                }
                sb.AppendLine();
            }
            var resultMsg = $"📊 <b>Результат:</b>\n✅ Отменено: {cancelled}\n❌ Ошибок: {failed}\n\n" + sb.ToString();
            if (resultMsg.Length > 4000)
                resultMsg = resultMsg.Substring(0, 4000) + "\n... (сообщение обрезано)";
            await _botClient.SendMessage(chatId: userId, text: resultMsg, cancellationToken: cancellationToken);
        }

        [BotCommand("Удалить пользователя", AdminOnly = true, UserIdParameter = "userId")]
        public async Task HandleWipeUserCommand(Message message, CancellationToken cancellationToken)
        {
            var userId = message.Chat.Id;
            _logger.LogWarning($"[WIPE-DEBUG] Старт удаления ордеров пользователя {userId}");
            using var scope = _serviceProvider.CreateScope();
            var userRepository = scope.ServiceProvider.GetRequiredService<KaspaBot.Domain.Interfaces.IUserRepository>();
            var orderPairRepo = scope.ServiceProvider.GetRequiredService<OrderPairRepository>();
            await orderPairRepo.DeleteByUserId(userId);
            _logger.LogWarning($"[WIPE-DEBUG] Удаление ордеров пользователя {userId} завершено");
            await userRepository.DeleteAsync(userId);
            _logger.LogWarning($"[WIPE-DEBUG] Пользователь {userId} удалён");
            await _botClient.SendMessage(chatId: userId, text: "Пользователь и все сделки удалены.", cancellationToken: cancellationToken);
        }

        [BotCommand("Ручной запуск восстановления ордеров", AdminOnly = true)]
        public async Task HandleOrderRecoveryCommand(Message message, CancellationToken cancellationToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var recovery = scope.ServiceProvider.GetRequiredService<OrderRecoveryService>();
            await recovery.RunRecoveryForUser(message.Chat.Id, cancellationToken);
            await _botClient.SendMessage(chatId: message.Chat.Id, text: "Восстановление ордеров завершено.", cancellationToken: cancellationToken);
        }

        [BotCommand("Статус WebSocket", AdminOnly = true)]
        public async Task HandleWebSocketStatusCommand(Message message, CancellationToken cancellationToken)
        {
            var userId = message.Chat.Id;
            using var scope = _serviceProvider.CreateScope();
            var userStreamManager = scope.ServiceProvider.GetRequiredService<UserStreamManager>();
            var user = await scope.ServiceProvider.GetRequiredService<KaspaBot.Domain.Interfaces.IUserRepository>().GetByIdAsync(userId);
            if (user == null)
            {
                await _botClient.SendMessage(chatId: userId, text: "❌ Пользователь не найден", cancellationToken: cancellationToken);
                return;
            }
            var listenKey = userStreamManager.GetListenKey(userId);
            var msg = "🔌 <b>Статус WebSocket</b>\n\n";
            msg += $"👤 User ID: {userId}\n";
            msg += $"🔑 Listen Key: {(string.IsNullOrEmpty(listenKey) ? "❌ Отсутствует" : "✅ " + listenKey)}\n";
            msg += $"📡 WebSocket: {(string.IsNullOrEmpty(listenKey) ? "❌ Не подключен" : "✅ Подключен")}\n\n";
            msg += "💡 <b>Диагностика:</b>\n";
            msg += "• Если Listen Key отсутствует - WebSocket не работает\n";
            msg += "• Если Listen Key есть, но события не приходят - проблема с соединением\n";
            msg += "• Проверьте логи на наличие ошибок WebSocket\n";
            await _botClient.SendMessage(chatId: userId, text: msg, cancellationToken: cancellationToken);
        }

        [BotCommand("Сбросить статус отменённого ордера", AdminOnly = true, UserIdParameter = "userId")]
        public async Task HandleResetCanceledOrdersCommand(Message message, CancellationToken cancellationToken)
        {
            var adminUserId = message.Chat.Id;
            var text = message.Text?.Trim() ?? string.Empty;
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string orderId = null;
            if (parts.Length > 1)
            {
                orderId = parts[1];
            }
            if (string.IsNullOrEmpty(orderId))
            {
                await _botClient.SendMessage(chatId: adminUserId, text: "❌ Укажите ID ордера: /reset_canceled ORDER_ID", cancellationToken: cancellationToken);
                return;
            }
            _logger.LogInformation($"[RESET-DEBUG] Админ {adminUserId} сбрасывает статус ордера {orderId}");
            using var scope = _serviceProvider.CreateScope();
            var orderPairRepo = scope.ServiceProvider.GetRequiredService<OrderPairRepository>();
            var allPairs = await orderPairRepo.GetAllAsync();
            var orderPair = allPairs.FirstOrDefault(p => p.BuyOrder.Id == orderId || p.SellOrder.Id == orderId);
            if (orderPair == null)
            {
                await _botClient.SendMessage(chatId: adminUserId, text: $"❌ Ордер {orderId} не найден", cancellationToken: cancellationToken);
                return;
            }
            var order = orderPair.BuyOrder.Id == orderId ? orderPair.BuyOrder : orderPair.SellOrder;
            if (order.Status != OrderStatus.Canceled)
            {
                await _botClient.SendMessage(chatId: adminUserId, text: $"❌ Ордер {orderId} имеет статус {order.Status}, а не Canceled", cancellationToken: cancellationToken);
            }
            else
            {
                order.Status = OrderStatus.New;
                await orderPairRepo.UpdateAsync(orderPair);
                _logger.LogInformation($"[RESET-DEBUG] Статус ордера {orderId} сброшен с Canceled на New");
                await _botClient.SendMessage(chatId: adminUserId, text: $"✅ Статус ордера {orderId} сброшен с Canceled на New", cancellationToken: cancellationToken);
            }
        }

        [BotCommand("Применить изменения статусов", AdminOnly = true)]
        public async Task HandleApplyStatusChangesCommand(Message message, CancellationToken cancellationToken)
        {
            var userId = message.Chat.Id;
            if (userId != 130822044)
            {
                await _botClient.SendMessage(chatId: userId, text: "❌ <b>Доступ запрещен</b>\n\n💡 <i>Эта команда доступна только администратору</i>", cancellationToken: cancellationToken);
                return;
            }

            using var scope = _serviceProvider.CreateScope();
            var orderPairRepo = scope.ServiceProvider.GetRequiredService<OrderPairRepository>();
            var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();

            var allPairs = (await orderPairRepo.GetAllAsync()).Where(p => !p.CompletedAt.HasValue).ToList();
            var appliedChanges = new List<(string orderId, string oldStatus, string newStatus, string reason)>();

            foreach (var pair in allPairs)
            {
                var user = await userRepository.GetByIdAsync(pair.UserId);
                if (user == null) continue;

                var mexcLogger = loggerFactory.CreateLogger<MexcService>();
                var mexcService = new MexcService(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, mexcLogger);

                // Проверяем buy-ордер
                if (!string.IsNullOrEmpty(pair.BuyOrder.Id) && !IsFinal(pair.BuyOrder.Status))
                {
                    var oldStatus = pair.BuyOrder.Status.ToString();
                    var result = await mexcService.GetOrderAsync(pair.BuyOrder.Symbol, pair.BuyOrder.Id, cancellationToken);
                    if (result.IsSuccess)
                    {
                        var newStatus = result.Value.Status.ToString();
                        if (newStatus != oldStatus)
                        {
                            pair.BuyOrder.Status = result.Value.Status;
                            pair.BuyOrder.QuantityFilled = result.Value.QuantityFilled;
                            pair.BuyOrder.QuoteQuantityFilled = result.Value.QuoteQuantityFilled;
                            if (result.Value.OrderType == OrderType.Market && result.Value.Status == OrderStatus.Filled && result.Value.QuantityFilled > 0 && result.Value.QuoteQuantityFilled > 0)
                            {
                                pair.BuyOrder.Price = result.Value.QuoteQuantityFilled / result.Value.QuantityFilled;
                            }
                            else
                            {
                                pair.BuyOrder.Price = result.Value.Price;
                            }
                            pair.BuyOrder.UpdatedAt = DateTime.UtcNow;
                            appliedChanges.Add((pair.BuyOrder.Id, oldStatus, newStatus, "Buy-ордер"));
                        }
                    }
                }

                // Проверяем sell-ордер
                if (!string.IsNullOrEmpty(pair.SellOrder.Id) && !IsFinal(pair.SellOrder.Status))
                {
                    var oldStatus = pair.SellOrder.Status.ToString();
                    var result = await mexcService.GetOrderAsync(pair.SellOrder.Symbol, pair.SellOrder.Id, cancellationToken);
                    if (result.IsSuccess)
                    {
                        var newStatus = result.Value.Status.ToString();
                        if (newStatus != oldStatus)
                        {
                            pair.SellOrder.Status = result.Value.Status;
                            pair.SellOrder.QuantityFilled = result.Value.QuantityFilled;
                            pair.SellOrder.Price = result.Value.Price;
                            pair.SellOrder.UpdatedAt = DateTime.UtcNow;
                            if (result.Value.Status == OrderStatus.Filled)
                            {
                                pair.CompletedAt = DateTime.UtcNow;
                                var sellAmount = result.Value.QuantityFilled * result.Value.Price;
                                var buyAmount = pair.BuyOrder.QuantityFilled * pair.BuyOrder.Price.GetValueOrDefault();
                                pair.Profit = sellAmount - buyAmount - pair.BuyOrder.Commission;
                            }
                            appliedChanges.Add((pair.SellOrder.Id, oldStatus, newStatus, "Sell-ордер"));
                        }
                    }
                }

                await orderPairRepo.UpdateAsync(pair);
            }

            if (appliedChanges.Any())
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("✅ <b>Применены изменения статусов</b>\n");
                sb.AppendLine($"📊 <b>Обновлено ордеров:</b> {appliedChanges.Count}\n");
                sb.AppendLine("📋 <b>Список изменений:</b>");
                foreach (var (orderId, oldStatus, newStatus, reason) in appliedChanges)
                {
                    sb.AppendLine($"• <code>{orderId}</code>: {oldStatus} → {newStatus} ({reason})");
                }
                await _botClient.SendMessage(chatId: userId, text: sb.ToString(), cancellationToken: cancellationToken);
            }
            else
            {
                await _botClient.SendMessage(chatId: userId, text: "ℹ️ <b>Изменений не найдено</b>\n\n💡 <i>Все ордера имеют актуальные статусы</i>", cancellationToken: cancellationToken);
            }
        }

        [BotCommand("Проверить статус ордера", AdminOnly = true)]
        public async Task HandleCheckOrderStatusCommand(Message message, CancellationToken cancellationToken)
        {
            var userId = message.Chat.Id;
            var text = message.Text?.Trim();
            var parts = text?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts == null || parts.Length < 2)
            {
                await _botClient.SendMessage(chatId: userId, text: "Используй: /check_order {orderId}", cancellationToken: cancellationToken);
                return;
            }
            var orderId = parts[1];
            using var scope = _serviceProvider.CreateScope();
            var userRepository = scope.ServiceProvider.GetRequiredService<KaspaBot.Domain.Interfaces.IUserRepository>();
            var orderPairRepo = scope.ServiceProvider.GetRequiredService<OrderPairRepository>();
            var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
            var mexcLogger = loggerFactory.CreateLogger<MexcService>();
            var users = await userRepository.GetAllAsync();
            var pair = (await orderPairRepo.GetAllAsync()).FirstOrDefault(p => p.BuyOrder.Id == orderId || p.SellOrder.Id == orderId);
            if (pair == null)
            {
                await _botClient.SendMessage(chatId: userId, text: $"Ордер {orderId} не найден в базе данных", cancellationToken: cancellationToken);
                return;
            }
            var user = users.FirstOrDefault(u => u.Id == pair.UserId);
            if (user == null)
            {
                await _botClient.SendMessage(chatId: userId, text: $"Пользователь для ордера {orderId} не найден", cancellationToken: cancellationToken);
                return;
            }
            var result = await new MexcService(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, mexcLogger).GetOrderAsync("KASUSDT", orderId, cancellationToken);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"🔍 <b>Проверка ордера {orderId}</b>");
            sb.AppendLine($"👤 Пользователь: {user.Id}");
            sb.AppendLine($"📊 Тип: {(pair.BuyOrder.Id == orderId ? "Buy" : "Sell")}");
            if (result.IsSuccess)
            {
                var order = result.Value;
                sb.AppendLine($"✅ <b>Статус на бирже:</b> {order.Status}");
                sb.AppendLine($"💰 Цена: {order.Price:F6}");
                sb.AppendLine($"📈 Количество: {order.Quantity:F3}");
                sb.AppendLine($"✅ Исполнено: {order.QuantityFilled:F3}");
                sb.AppendLine($"💵 Сумма: {order.QuoteQuantityFilled:F6}");
                var dbOrder = pair.BuyOrder.Id == orderId ? pair.BuyOrder : pair.SellOrder;
                sb.AppendLine("\n📋 <b>В базе данных:</b>");
                sb.AppendLine($"📊 Статус: {dbOrder.Status}");
                sb.AppendLine($"💰 Цена: {dbOrder.Price:F6}");
                sb.AppendLine($"📈 Количество: {dbOrder.Quantity:F3}");
                sb.AppendLine($"✅ Исполнено: {dbOrder.QuantityFilled:F3}");
                sb.AppendLine($"💵 Сумма: {dbOrder.QuoteQuantityFilled:F6}");
            }
            else
            {
                sb.AppendLine($"❌ <b>Ошибка получения данных с биржи:</b>");
                sb.AppendLine(string.Join(", ", result.Errors.Select(e => e.Message)));
            }
            await _botClient.SendMessage(chatId: userId, text: sb.ToString(), cancellationToken: cancellationToken);
        }

        [BotCommand("Показать балансы", AdminOnly = false, UserIdParameter = "userId")]
        public async Task HandleBalanceCommand(Message message, CancellationToken cancellationToken, long targetUserId = 0)
        {
            var adminUserId = message.Chat.Id;
            var userId = targetUserId > 0 ? targetUserId : adminUserId;
            var isAdmin = adminUserId == 130822044;

            if (targetUserId > 0 && !isAdmin)
            {
                await _botClient.SendMessage(chatId: adminUserId, text: "❌ У вас нет прав для просмотра баланса другого пользователя.", cancellationToken: cancellationToken);
                return;
            }

            using var scope = _serviceProvider.CreateScope();
            var user = await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(userId);
            if (user == null)
            {
                var text = targetUserId > 0 ? $"❌ Пользователь {userId} не найден." : "❌ Пользователь не найден.";
                await _botClient.SendMessage(chatId: adminUserId, text: text, cancellationToken: cancellationToken);
                return;
            }

            var mexcLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<MexcService>();
            var mexcService = new MexcService(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, mexcLogger);
            var result = await mexcService.GetAccountInfoAsync(cancellationToken);
            if (!result.IsSuccess)
            {
                var text = targetUserId > 0 ? $"❌ Ошибка получения баланса пользователя {userId}: {result.Errors.FirstOrDefault()?.Message}" : $"❌ Ошибка получения баланса: {result.Errors.FirstOrDefault()?.Message}";
                await _botClient.SendMessage(chatId: adminUserId, text: text, cancellationToken: cancellationToken);
                return;
            }

            var balances = result.Value.Balances.Where(b => b.Total > 0).ToList();
            if (!balances.Any())
            {
                var text = targetUserId > 0 ? $"На счету пользователя {userId} нет средств." : "На вашем счету нет средств.";
                await _botClient.SendMessage(chatId: adminUserId, text: text, cancellationToken: cancellationToken);
                return;
            }

            decimal totalUsdt = 0;
            var rows = new List<(string Asset, decimal Total, decimal Available, decimal Frozen, decimal? UsdtValue)>();

            foreach (var b in balances)
            {
                decimal? usdtValue = null;
                if (b.Asset == "USDT")
                {
                    usdtValue = b.Total;
                    totalUsdt += b.Total;
                }
                else
                {
                    var priceResult = await mexcService.GetSymbolPriceAsync(b.Asset + "USDT", cancellationToken);
                    if (priceResult.IsSuccess)
                    {
                        usdtValue = b.Total * priceResult.Value;
                        totalUsdt += usdtValue.Value;
                    }
                }
                rows.Add((b.Asset, b.Total, b.Available, b.Total - b.Available, usdtValue));
            }

            var title = targetUserId > 0 ? $"💰 <b>Баланс пользователя {userId}</b>" : "💰 <b>Ваш баланс</b>";
            var balanceText = title + "\n\n" + NotificationFormatter.BalanceTable(rows, totalUsdt);
            await _botClient.SendMessage(chatId: adminUserId, text: balanceText, cancellationToken: cancellationToken);
        }

        [BotCommand("Включить/выключить автоторговлю")]
        public async Task HandleAutotradeCommand(Message message, CancellationToken cancellationToken)
        {
            var userId = message.Chat.Id;
            using var scope = _serviceProvider.CreateScope();
            var userRepository = scope.ServiceProvider.GetRequiredService<KaspaBot.Domain.Interfaces.IUserRepository>();
            var user = await userRepository.GetByIdAsync(userId);
            if (user == null)
            {
                await _botClient.SendMessage(chatId: userId, text: "Пользователь не найден", cancellationToken: cancellationToken);
                return;
            }
            user.Settings.IsAutoTradeEnabled = !user.Settings.IsAutoTradeEnabled;
            await userRepository.UpdateAsync(user);
            var status = user.Settings.IsAutoTradeEnabled ? "включена" : "выключена";
            await _botClient.SendMessage(chatId: userId, text: $"Автоторговля {status}", cancellationToken: cancellationToken);
        }

        [BotCommand("Показать список всех команд")]
        public async Task HandleCommandsListCommand(Message message, CancellationToken cancellationToken)
        {
            var userId = message.Chat.Id;
            var isAdmin = userId == 130822044;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("<b>Доступные команды:</b>\n");
            
            var methods = typeof(TradingCommandHandler).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.GetCustomAttribute<BotCommandAttribute>() != null)
                .ToList();
            
            var userCommands = new List<string>();
            var adminCommands = new List<string>();
            
            foreach (var method in methods)
            {
                var attr = method.GetCustomAttribute<BotCommandAttribute>();
                if (attr == null) continue;
                
                var commandName = MethodToCommandName(method);
                var commandText = $"<b>{commandName}</b> — {attr.Description}";
                
                if (attr.AdminOnly)
                {
                    if (isAdmin)
                        adminCommands.Add(commandText);
                }
                else
                {
                    userCommands.Add(commandText);
                }
            }
            
            if (userCommands.Any())
            {
                sb.AppendLine("<b>📋 Основные команды:</b>");
                foreach (var cmd in userCommands)
                {
                    sb.AppendLine($"• {cmd}");
                }
                sb.AppendLine();
            }
            
            if (adminCommands.Any())
            {
                sb.AppendLine("<b>🔧 Админские команды:</b>");
                foreach (var cmd in adminCommands)
                {
                    sb.AppendLine($"• {cmd}");
                }
            }
            
            await _botClient.SendMessage(userId, sb.ToString(), cancellationToken: cancellationToken);
        }

        private static string MethodToCommandName(MethodInfo method)
        {
            var name = method.Name;
            if (name.StartsWith("Handle") && name.EndsWith("Command"))
            {
                var commandName = name.Substring(6, name.Length - 13).ToLowerInvariant();
                return commandName switch
                {
                    "sellamount" => "/sell",
                    "resetcanceledorders" => "/reset_canceled",
                    "wipeuser" => "/wipe_user",
                    "orderrecovery" => "/order_recovery",
                    "checkorders" => "/check_orders",
                    "cancelorders" => "/cancel_orders",
                    "closeallorders" => "/close_all",
                    "websocketstatus" => "/ws_status",
                    "applystatuschanges" => "/apply_status_changes",
                    "commandslist" => "/commands",
                    "autotrade" => "/autotrade",
                    "balance" => "/balance",
                    "fee" => "/fee",
                    "checkorderstatus" => "/check_order",
                    _ => "/" + commandName,
                };
            }
            return "/" + name.ToLowerInvariant();
        }

        private static bool IsFinal(OrderStatus status)
        {
            return status == OrderStatus.Filled || status == OrderStatus.Canceled;
        }
    }
}