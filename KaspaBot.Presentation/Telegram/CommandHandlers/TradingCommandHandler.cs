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

namespace KaspaBot.Presentation.Telegram.CommandHandlers
{
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
                await _botClient.SendMessage(chatId: userId, text: $"Ошибка покупки: {buyResult.Errors.FirstOrDefault()?.Message}", cancellationToken: cancellationToken);
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
                await _botClient.SendMessage(chatId: userId, text: $"Ошибка получения баланса: {accResult.Errors.FirstOrDefault()?.Message}", cancellationToken: cancellationToken);
                return;
            }
            var kasBalance = accResult.Value.Balances.FirstOrDefault(b => b.Asset == "KAS")?.Available ?? 0m;
            if (kasBalance <= 0)
            {
                await _botClient.SendMessage(chatId: userId, text: "Нет свободных KAS для продажи.", cancellationToken: cancellationToken);
                return;
            }
            // Продаём весь Free KAS
            var sellResult = await mexcService.PlaceOrderAsync("KASUSDT", OrderSide.Sell, OrderType.Market, kasBalance, null, TimeInForce.GoodTillCanceled, cancellationToken);
            if (!sellResult.IsSuccess)
            {
                await _botClient.SendMessage(chatId: userId, text: $"Ошибка продажи: {sellResult.Errors.FirstOrDefault()?.Message}", cancellationToken: cancellationToken);
                return;
            }
            await _botClient.SendMessage(chatId: userId, text: $"Продано {kasBalance:F2} KAS по рынку.", cancellationToken: cancellationToken);
        }

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
                await _botClient.SendMessage(chatId: userId, text: $"Ошибка получения баланса: {accResult.Errors.FirstOrDefault()?.Message}", cancellationToken: cancellationToken);
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
                await _botClient.SendMessage(chatId: userId, text: $"Недостаточно KAS. Баланс: {kasBalance:F2}, запрошено: {quantity:F2}.", cancellationToken: cancellationToken);
                return;
            }
            // Продаём указанное количество
            var sellResult = await mexcService.PlaceOrderAsync("KASUSDT", OrderSide.Sell, OrderType.Market, quantity, null, TimeInForce.GoodTillCanceled, cancellationToken);
            if (!sellResult.IsSuccess)
            {
                await _botClient.SendMessage(chatId: userId, text: $"Ошибка продажи: {sellResult.Errors.FirstOrDefault()?.Message}", cancellationToken: cancellationToken);
                return;
            }
            await _botClient.SendMessage(chatId: userId, text: $"Продано {quantity:F2} KAS по рынку.", cancellationToken: cancellationToken);
        }

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
            var allPairs = await orderPairRepo.GetAllAsync();
            var userPairs = allPairs.Where(p => p.UserId == userId).ToList();
            int totalPairs = userPairs.Count;
            int buyWithoutSell = userPairs.Count(p => !string.IsNullOrEmpty(p.BuyOrder.Id) && (string.IsNullOrEmpty(p.SellOrder.Id) || p.SellOrder.Status == Mexc.Net.Enums.OrderStatus.New));
            var autotradeStatus = user.Settings.IsAutoTradeEnabled ? "🟢 автоторговля ВКЛ" : "🔴 автоторговля ВЫКЛ";
            var msg = $"{autotradeStatus}\n" +
                      $"Комиссия биржи: Maker {makerFee * 100:F3}% / Taker {takerFee * 100:F3}%\n" +
                      $"Всего пар: {totalPairs}\nПокупок без продажи: {buyWithoutSell}\n\n";
            foreach (var pair in userPairs)
            {
                var buy = pair.BuyOrder;
                var sell = pair.SellOrder;
                msg += $"Покупка: {buy.Id} ({buy.Status}, {buy.Price:F6}) <-> Продажа: {sell.Id} ({sell.Status}, {sell.Price:F6})\n";
            }
            await _botClient.SendMessage(chatId: userId, text: msg, cancellationToken: cancellationToken);
        }

        public async Task HandleStatCommand(Message message, CancellationToken cancellationToken)
        {
            var userId = message.Chat.Id;
            using var scope = _serviceProvider.CreateScope();
            var orderPairRepo = scope.ServiceProvider.GetRequiredService<OrderPairRepository>();
            var userRepository = scope.ServiceProvider.GetRequiredService<KaspaBot.Domain.Interfaces.IUserRepository>();
            var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
            var mexcLogger = loggerFactory.CreateLogger<MexcService>();
            var user = await userRepository.GetByIdAsync(userId);
            if (user == null)
            {
                await _botClient.SendMessage(chatId: userId, text: "Пользователь не найден", cancellationToken: cancellationToken);
                return;
            }
            var allPairs = await orderPairRepo.GetAllAsync();
            var sellOrders = allPairs
                .Where(p => p.UserId == userId && !string.IsNullOrEmpty(p.SellOrder.Id) && p.SellOrder.Status != OrderStatus.Filled && p.SellOrder.Quantity > 0)
                .Select(p => p.SellOrder)
                .ToList();
            if (sellOrders.Count == 0)
            {
                await _botClient.SendMessage(chatId: userId, text: "Нет активных ордеров на продажу", cancellationToken: cancellationToken);
                return;
            }
            // Сортировка по возрастанию цены (минимальный первым)
            sellOrders = sellOrders.OrderBy(o => o.Price).ToList();
            var minPrice = sellOrders.First().Price.GetValueOrDefault();
            // Получаем текущую цену (запрос к бирже или к MexcService)
            decimal currentPrice = 0;
            try
            {
                var mexcService = new MexcService(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, mexcLogger);
                var priceResult = await mexcService.GetSymbolPriceAsync(sellOrders.First().Symbol, cancellationToken);
                currentPrice = priceResult.IsSuccess ? priceResult.Value : minPrice;
            }
            catch
            {
                currentPrice = minPrice; // fallback
            }
            var totalQty = sellOrders.Sum(o => o.Quantity);
            var totalSum = sellOrders.Sum(o => o.Quantity * o.Price.GetValueOrDefault());
            var table = "|  #  |  Кол-во  |  Цена   |  Сумма  | Отклонение  |\n|-----|----------|--------|---------|-------------|\n";
            int n = sellOrders.Count;
            int mainCount = Math.Min(10, n);
            for (int i = 0; i < mainCount; i++)
            {
                var o = sellOrders[i];
                var sum = o.Quantity * o.Price.GetValueOrDefault();
                decimal deviation = ((o.Price.GetValueOrDefault() / currentPrice) - 1m) * 100m;
                deviation = Math.Round(deviation, 2);
                if (deviation > 0) deviation = -deviation;
                var idxStr = (i + 1).ToString().PadLeft(3);
                var qtyStr = o.Quantity.ToString("F2", System.Globalization.CultureInfo.InvariantCulture).PadLeft(8);
                var priceStr = o.Price.GetValueOrDefault().ToString("F4", System.Globalization.CultureInfo.InvariantCulture).PadLeft(7);
                var sumStr = sum.ToString("F2", System.Globalization.CultureInfo.InvariantCulture).PadLeft(7);
                var devStr = ($"{deviation,6:F2}%").PadLeft(9);
                table += $"|{idxStr} |{qtyStr} |{priceStr} |{sumStr} |{devStr} |\n";
            }
            if (n > 11) {
                table += "|-----|----------|--------|---------|-------------|\n";
                var o = sellOrders[n - 1];
                var sum = o.Quantity * o.Price.GetValueOrDefault();
                decimal deviation = ((o.Price.GetValueOrDefault() / currentPrice) - 1m) * 100m;
                deviation = Math.Round(deviation, 2);
                if (deviation > 0) deviation = -deviation;
                var idxStr = (n).ToString().PadLeft(3);
                var qtyStr = o.Quantity.ToString("F2", System.Globalization.CultureInfo.InvariantCulture).PadLeft(8);
                var priceStr = o.Price.GetValueOrDefault().ToString("F4", System.Globalization.CultureInfo.InvariantCulture).PadLeft(7);
                var sumStr = sum.ToString("F2", System.Globalization.CultureInfo.InvariantCulture).PadLeft(7);
                var devStr = ($"{deviation,6:F2}%").PadLeft(9);
                table += $"|{idxStr} |{qtyStr} |{priceStr} |{sumStr} |{devStr} |\n";
            }
            var autotradeStatus = user.Settings.IsAutoTradeEnabled ? "🟢 автоторговля ВКЛ" : "🔴 автоторговля ВЫКЛ";
            var msg = $"{autotradeStatus}\n" +
                      $"🚀 Ордера на продажу 🚀\n" +
                      $"📊 Общее количество ордеров: {sellOrders.Count}\n" +
                      $"💰 Общая сумма всех ордеров: {totalSum:F2}\n\n" +
                      table +
                      $"\n💵 Текущая цена: {currentPrice:F4}";
            await _botClient.SendMessage(chatId: userId, text: msg, cancellationToken: cancellationToken);
        }

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
                var orderInfo = await mexcService.GetOrderAsync("KASUSDT", pair.BuyOrder.Id, cancellationToken);
                checkedCount++;
                if (orderInfo.IsSuccess)
                {
                    var o = orderInfo.Value;
                    bool mismatch = false;
                    if (pair.BuyOrder.QuantityFilled != o.QuantityFilled)
                    {
                        sb.AppendLine($"Пара {pair.Id}: QuantityFilled база={pair.BuyOrder.QuantityFilled} биржа={o.QuantityFilled}");
                        pair.BuyOrder.QuantityFilled = o.QuantityFilled;
                        mismatch = true;
                    }
                    if (pair.BuyOrder.QuoteQuantityFilled != o.QuoteQuantityFilled)
                    {
                        sb.AppendLine($"Пара {pair.Id}: QuoteQuantityFilled база={pair.BuyOrder.QuoteQuantityFilled} биржа={o.QuoteQuantityFilled}");
                        pair.BuyOrder.QuoteQuantityFilled = o.QuoteQuantityFilled;
                        mismatch = true;
                    }
                    decimal newPrice = (o.QuantityFilled > 0 && o.QuoteQuantityFilled > 0) ? o.QuoteQuantityFilled / o.QuantityFilled : o.Price;
                    if (pair.BuyOrder.Price != newPrice)
                    {
                        sb.AppendLine($"Пара {pair.Id}: Price база={pair.BuyOrder.Price} биржа={newPrice}");
                        pair.BuyOrder.Price = newPrice;
                        mismatch = true;
                    }
                    if (pair.BuyOrder.Status != o.Status)
                    {
                        sb.AppendLine($"Пара {pair.Id}: Status база={pair.BuyOrder.Status} биржа={o.Status}");
                        pair.BuyOrder.Status = o.Status;
                        mismatch = true;
                    }
                    if (mismatch)
                    {
                        mismatchCount++;
                        await orderPairRepo.UpdateAsync(pair);
                        fixedCount++;
                    }
                }
                else
                {
                    sb.AppendLine($"Пара {pair.Id}: Ошибка запроса к бирже: {string.Join(", ", orderInfo.Errors.Select(e => e.Message))}");
                }
            }
            // --- Восстановление sell-ордеров, не учтённых в базе ---
            var openSellOrdersResult = await mexcService.GetOpenOrdersAsync("KASUSDT", cancellationToken);
            var allSellOrderIds = allPairs.Select(p => p.SellOrder.Id).Where(id => !string.IsNullOrEmpty(id)).ToHashSet();
            int restored = 0, cancelled = 0;
            if (openSellOrdersResult.IsSuccess)
            {
                foreach (var sellOrder in openSellOrdersResult.Value.Where(o => o.Side == OrderSide.Sell))
                {
                    if (!allSellOrderIds.Contains(sellOrder.OrderId))
                    {
                        var timeProp = sellOrder.GetType().GetProperty("time");
                        var timeVal = timeProp?.GetValue(sellOrder) as string;
                        DateTime? orderTime = null;
                        if (!string.IsNullOrEmpty(timeVal)) orderTime = DateTime.Parse(timeVal).ToLocalTime();
                        var match = allPairs.FirstOrDefault(p =>
                            orderTime != null && Math.Abs((orderTime.Value - p.BuyOrder.CreatedAt).TotalMinutes) < 30 &&
                            Math.Abs(sellOrder.Quantity - p.BuyOrder.QuantityFilled) < 0.01m
                        );
                        if (match != null)
                        {
                            if (!string.IsNullOrEmpty(match.SellOrder.Id))
                            {
                                // У пары уже есть другой sell-ордер — отменяем этот лишний
                                var cancelResult = await mexcService.CancelOrderAsync("KASUSDT", sellOrder.OrderId, cancellationToken);
                                sb.AppendLine($"Лишний sell-ордер {sellOrder.OrderId} отменён для пары {match.Id}: {(cancelResult.IsSuccess ? "OK" : string.Join(", ", cancelResult.Errors.Select(e => e.Message)))}");
                                cancelled++;
                            }
                            else
                            {
                                // Привязываем этот sell-ордер к паре
                                match.SellOrder.Id = sellOrder.OrderId;
                                match.SellOrder.Price = sellOrder.Price;
                                match.SellOrder.Quantity = sellOrder.Quantity;
                                match.SellOrder.Status = sellOrder.Status;
                                if (orderTime != null) match.SellOrder.CreatedAt = orderTime.Value;
                                await orderPairRepo.UpdateAsync(match);
                                sb.AppendLine($"Sell-ордер {sellOrder.OrderId} привязан к паре {match.Id}");
                                restored++;
                            }
                        }
                        else
                        {
                            var sum = sellOrder.Quantity * sellOrder.Price;
                            var exactPairs = allPairs
                                .Where(p => Math.Abs(sellOrder.Quantity - p.BuyOrder.QuantityFilled) < 0.0001m)
                                .Select(p => $"Pair {p.Id}: BuyQty={p.BuyOrder.QuantityFilled}, BuyAt={p.BuyOrder.CreatedAt:HH:mm:ss}, BuyId={p.BuyOrder.Id}")
                                .ToList();
                            List<string> nearest;
                            if (exactPairs.Count > 0)
                                nearest = exactPairs;
                            else
                                nearest = allPairs
                                    .Select(p => new {
                                        Pair = p,
                                        QtyDiff = Math.Abs(sellOrder.Quantity - p.BuyOrder.QuantityFilled)
                                    })
                                    .OrderBy(x => x.QtyDiff)
                                    .Take(3)
                                    .Select(x => $"Pair {x.Pair.Id}: BuyQty={x.Pair.BuyOrder.QuantityFilled}, BuyAt={x.Pair.BuyOrder.CreatedAt:HH:mm:ss}, BuyId={x.Pair.BuyOrder.Id}, QtyDiff={x.QtyDiff:F4}")
                                    .ToList();
                            sb.AppendLine($"Sell-ордер {sellOrder.OrderId} не удалось привязать ни к одной паре, сумма: {sum:F4} USDT. Совпадающие пары по количеству:\n{string.Join("\n", nearest)}");
                        }
                    }
                }
            }
            var resultMsg = $"Проверено ордеров: {checkedCount}\nНайдено расхождений: {mismatchCount}\nИсправлено: {fixedCount}\nВосстановлено sell-ордеров: {restored}\nОтменено лишних sell-ордеров: {cancelled}\n" + sb.ToString();
            if (resultMsg.Length > 3500)
                resultMsg = resultMsg.Substring(0, 3500) + "\n... (обрезано)";
            await _botClient.SendMessage(chatId: userId, text: resultMsg, cancellationToken: cancellationToken);
        }

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
            var botOrderIds = allPairs.SelectMany(p => new[] { p.BuyOrder.Id, p.SellOrder.Id }).Where(id => !string.IsNullOrEmpty(id)).ToHashSet();
            int cancelled = 0, failed = 0;
            var sb = new System.Text.StringBuilder();
            foreach (var order in openOrdersResult.Value)
            {
                if (mode == "bot" && !botOrderIds.Contains(order.OrderId))
                    continue;
                var cancelResult = await mexcService.CancelOrderAsync("KASUSDT", order.OrderId, cancellationToken);
                if (cancelResult.IsSuccess)
                {
                    sb.AppendLine($"Отменён ордер {order.OrderId} {order.Side} {order.Quantity} по {order.Price}");
                    cancelled++;
                }
                else
                {
                    sb.AppendLine($"Ошибка отмены ордера {order.OrderId}: {string.Join(", ", cancelResult.Errors.Select(e => e.Message))}");
                    failed++;
                }
            }
            var resultMsg = $"Отменено ордеров: {cancelled}\nОшибок: {failed}\n" + sb.ToString();
            if (resultMsg.Length > 3500)
                resultMsg = resultMsg.Substring(0, 3500) + "\n... (обрезано)";
            await _botClient.SendMessage(chatId: userId, text: resultMsg, cancellationToken: cancellationToken);
        }

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

        public async Task HandleOrderRecoveryCommand(Message message, CancellationToken cancellationToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var recovery = scope.ServiceProvider.GetRequiredService<OrderRecoveryService>();
            await recovery.RunRecoveryForUser(message.Chat.Id, cancellationToken);
            await _botClient.SendMessage(chatId: message.Chat.Id, text: "Восстановление ордеров завершено.", cancellationToken: cancellationToken);
        }
    }
}