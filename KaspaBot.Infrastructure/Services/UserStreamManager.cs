using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using KaspaBot.Domain.Entities;
using KaspaBot.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using KaspaBot.Infrastructure.Repositories;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;

namespace KaspaBot.Infrastructure.Services
{
    public class UserStreamManager
    {
        private readonly IUserRepository _userRepository;
        private readonly ILogger<UserStreamManager> _logger;
        private readonly ConcurrentDictionary<long, string> _listenKeys = new();
        private readonly ConcurrentDictionary<long, IMexcService> _userMexcServices = new();
        private readonly ILoggerFactory _loggerFactory;
        private readonly IServiceProvider _serviceProvider;
        private readonly IOrderRecoveryService _orderRecoveryService;
        public delegate Task OrderSoldHandler(long userId, decimal qty, decimal price, decimal usdt, decimal profit);
        public event OrderSoldHandler? OnOrderSold;
        private readonly OrderPairRepository _orderPairRepo;
        private readonly ConcurrentDictionary<long, CancellationTokenSource> _debounceCtsPerUser = new();
        private readonly object _debounceLock = new();
        private readonly KaspaBot.Domain.Interfaces.IBotMessenger _botMessenger;

        public UserStreamManager(IUserRepository userRepository, ILogger<UserStreamManager> logger, ILoggerFactory loggerFactory, OrderPairRepository orderPairRepo, IServiceProvider serviceProvider, IOrderRecoveryService orderRecoveryService, KaspaBot.Domain.Interfaces.IBotMessenger botMessenger)
        {
            _userRepository = userRepository;
            _logger = logger;
            _loggerFactory = loggerFactory;
            _orderPairRepo = orderPairRepo;
            _serviceProvider = serviceProvider;
            _orderRecoveryService = orderRecoveryService;
            _botMessenger = botMessenger;
        }

        public async Task InitializeAllAsync(CancellationToken cancellationToken = default)
        {
            var users = await _userRepository.GetAllAsync();
            foreach (var user in users)
            {
                await InitializeUserAsync(user, cancellationToken);
            }
        }

        public async Task InitializeUserAsync(User user, CancellationToken cancellationToken = default)
        {
            // Создаём MexcService для пользователя
            var mexcLogger = _loggerFactory.CreateLogger<MexcService>();
            var mexcService = new MexcService(
                user.ApiCredentials.ApiKey,
                user.ApiCredentials.ApiSecret,
                mexcLogger);
            _userMexcServices[user.Id] = mexcService;

            // Получаем listenKey
            var listenKeyResult = await mexcService.GetListenKeyAsync(cancellationToken);
            if (listenKeyResult.IsSuccess)
            {
                _listenKeys[user.Id] = listenKeyResult.Value;
                _logger.LogInformation($"ListenKey for user {user.Id} initialized");
                // Подписка на обновления ордеров
                _ = mexcService._socketClient.SpotApi.SubscribeToOrderUpdatesAsync(
                    listenKeyResult.Value,
                    async (orderUpdate) =>
                    {
                        try
                        {
                            _logger.LogInformation($"[WS DIAG] EVENT: user={user.Id} orderId={orderUpdate.Data.OrderId} side={orderUpdate.Data.Side} status={orderUpdate.Data.Status} type={orderUpdate.Data.OrderType} qty={orderUpdate.Data.Quantity} price={orderUpdate.Data.Price} CumulativeQty={orderUpdate.Data.CumulativeQuantity} CumulativeQuoteQty={orderUpdate.Data.CumulativeQuoteQuantity}");
                            // SELL-ордер исполнен
                            if (orderUpdate.Data.Side == Mexc.Net.Enums.OrderSide.Sell && orderUpdate.Data.Status == Mexc.Net.Enums.OrderStatus.Filled)
                            {
                                var pairs = await _orderPairRepo.GetAllAsync();
                                var pair = pairs.FirstOrDefault(p => p.UserId == user.Id && p.SellOrder.Id == orderUpdate.Data.OrderId.ToString());
                                if (pair == null)
                                {
                                    _logger.LogInformation($"[WS DIAG] SELL исполнен, но ордер {orderUpdate.Data.OrderId} не найден в базе — игнорируем событие");
                                    return;
                                }
                                pair.SellOrder.Status = orderUpdate.Data.Status;
                                pair.SellOrder.QuantityFilled = orderUpdate.Data.Quantity;
                                pair.SellOrder.Price = orderUpdate.Data.Price;
                                pair.SellOrder.UpdatedAt = DateTime.UtcNow;
                                pair.CompletedAt = DateTime.UtcNow;
                                pair.Profit = (orderUpdate.Data.Quantity * orderUpdate.Data.Price) - (pair.BuyOrder.QuantityFilled * pair.BuyOrder.Price.GetValueOrDefault()) - pair.BuyOrder.Commission;
                                await _orderPairRepo.UpdateAsync(pair);
                                var profit = pair.Profit.GetValueOrDefault();
                                if (OnOrderSold != null)
                                    await OnOrderSold(user.Id, orderUpdate.Data.Quantity, orderUpdate.Data.Price, orderUpdate.Data.Quantity * orderUpdate.Data.Price, profit);
                                // Дебаунс автопары
                                lock (_debounceLock)
                                {
                                    if (_debounceCtsPerUser.TryGetValue(user.Id, out var oldCts))
                                    {
                                        oldCts.Cancel();
                                        oldCts.Dispose();
                                    }
                                    var cts = new CancellationTokenSource();
                                    _debounceCtsPerUser[user.Id] = cts;
                                    _ = DebouncedAutoPair(user, cts.Token);
                                }
                            }
                            // BUY-ордер исполнен (особенно маркет)
                            if (orderUpdate.Data.Side == Mexc.Net.Enums.OrderSide.Buy && orderUpdate.Data.Status == Mexc.Net.Enums.OrderStatus.Filled)
                            {
                                var pairs = await _orderPairRepo.GetAllAsync();
                                var pair = pairs.FirstOrDefault(p => p.UserId == user.Id && p.BuyOrder.Id == orderUpdate.Data.OrderId.ToString());
                                if (pair != null)
                                {
                                    decimal? avgPrice = null;
                                    decimal qty = orderUpdate.Data.Quantity;
                                    decimal? quoteQty = null;
                                    // Для маркет-ордеров: если есть cumulative поля — всегда используем их для buyOrder.Price
                                    if (orderUpdate.Data.OrderType == Mexc.Net.Enums.OrderType.Market && orderUpdate.Data.CumulativeQuantity.HasValue && orderUpdate.Data.CumulativeQuoteQuantity.HasValue && orderUpdate.Data.CumulativeQuantity.Value > 0)
                                    {
                                        avgPrice = orderUpdate.Data.CumulativeQuoteQuantity.Value / orderUpdate.Data.CumulativeQuantity.Value;
                                        qty = orderUpdate.Data.CumulativeQuantity.Value;
                                        quoteQty = orderUpdate.Data.CumulativeQuoteQuantity.Value;
                                        _logger.LogError($"[WS DIAG] [BUY WS] avgPrice={avgPrice} (CumulativeQuoteQty / CumulativeQty) — используется как основная цена покупки");
                                    }
                                    // Логируем данные из WebSocket
                                    _logger.LogError($"[WS DIAG] [BUY WS] OrderId={orderUpdate.Data.OrderId} Qty={orderUpdate.Data.Quantity} Price={orderUpdate.Data.Price} CumulativeQty={orderUpdate.Data.CumulativeQuantity} CumulativeQuoteQty={orderUpdate.Data.CumulativeQuoteQuantity}");
                                    // REST-запрос только для диагностики, не влияет на цену
                                    _logger.LogError($"[WS DIAG] [BUY REST] Запрос к REST для OrderId={orderUpdate.Data.OrderId}");
                                    var mexcLogger = _loggerFactory.CreateLogger<MexcService>();
                                    var mexcService = new MexcService(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, mexcLogger);
                                    var orderResult = await mexcService.GetOrderAsync(pair.BuyOrder.Symbol, orderUpdate.Data.OrderId.ToString());
                                    var rawJson = System.Text.Json.JsonSerializer.Serialize(orderResult);
                                    _logger.LogError($"[WS DIAG] [BUY REST RAW] {rawJson}");
                                    if (!orderResult.IsSuccess)
                                    {
                                        _logger.LogError($"[WS DIAG] [BUY REST] GetOrderAsync failed: {string.Join(", ", orderResult.Errors.Select(e => e.Message))}");
                                    }
                                    pair.BuyOrder.Status = orderUpdate.Data.Status;
                                    pair.BuyOrder.QuantityFilled = qty;
                                    pair.BuyOrder.Price = avgPrice;
                                    pair.BuyOrder.QuoteQuantityFilled = quoteQty ?? 0;
                                    pair.BuyOrder.UpdatedAt = DateTime.UtcNow;
                                    await _orderPairRepo.UpdateAsync(pair);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Ошибка в обработчике обновления ордера для user {user.Id}");
                        }
                    }
                );
                // TODO: Поднять WebSocket-подключение и подписаться на события
            }
            else
            {
                _logger.LogError($"Failed to initialize listenKey for user {user.Id}: {listenKeyResult.Errors.FirstOrDefault()?.Message}");
            }
        }

        public async Task ReloadUserAsync(User user, CancellationToken cancellationToken = default)
        {
            // Пересоздать MexcService и listenKey
            var mexcLogger = _loggerFactory.CreateLogger<MexcService>();
            var mexcService = new MexcService(
                user.ApiCredentials.ApiKey,
                user.ApiCredentials.ApiSecret,
                mexcLogger);
            _userMexcServices[user.Id] = mexcService;

            var listenKeyResult = await mexcService.GetListenKeyAsync(cancellationToken);
            if (listenKeyResult.IsSuccess)
            {
                _listenKeys[user.Id] = listenKeyResult.Value;
                _logger.LogInformation($"ListenKey for user {user.Id} reloaded");
                // TODO: Переподключить WebSocket
            }
            else
            {
                _logger.LogError($"Failed to reload listenKey for user {user.Id}: {listenKeyResult.Errors.FirstOrDefault()?.Message}");
            }
        }

        public string? GetListenKey(long userId)
        {
            return _listenKeys.TryGetValue(userId, out var key) ? key : null;
        }

        // TODO: Реализовать переподключение и обновление listenKey при обрыве

        private async Task DebouncedAutoPair(User user, CancellationToken token)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                // После 30 секунд тишины — делаем автопару
                await CreateAutoPairForUser(user);
                await _orderRecoveryService.RunRecoveryForUser(user.Id, CancellationToken.None);
            }
            catch (TaskCanceledException) { /* дебаунс перезапущен */ }
        }

        private async Task CreateAutoPairForUser(User user)
        {
            // Получаем актуальные данные
            var orderAmount = user.Settings.OrderAmount;
            if (orderAmount <= 0) return;
            var mexcLogger = _loggerFactory.CreateLogger<MexcService>();
            var mexcService = new MexcService(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, mexcLogger);
            var buyResult = await mexcService.PlaceOrderAsync("KASUSDT", Mexc.Net.Enums.OrderSide.Buy, Mexc.Net.Enums.OrderType.Market, orderAmount, null, Mexc.Net.Enums.TimeInForce.GoodTillCanceled);
            if (!buyResult.IsSuccess) return;
            var buyOrderId = buyResult.Value;
            var orderInfo = await mexcService.GetOrderAsync("KASUSDT", buyOrderId);
            decimal buyPrice = 0, buyQty = 0, totalCommission = 0;
            if (orderInfo.IsSuccess && orderInfo.Value.QuantityFilled > 0 && orderInfo.Value.QuoteQuantityFilled > 0)
                buyPrice = orderInfo.Value.QuoteQuantityFilled / orderInfo.Value.QuantityFilled;
            else if (orderInfo.IsSuccess)
                buyPrice = orderInfo.Value.Price;
            buyQty = orderInfo.IsSuccess ? orderInfo.Value.QuantityFilled : 0;
            // Комиссия
            var tradesResult = await mexcService.GetOrderTradesAsync("KASUSDT", buyOrderId);
            if (tradesResult.IsSuccess)
                totalCommission = tradesResult.Value.Sum(t => t.Fee);
            // Создаём OrderPair
            var pairId = Guid.NewGuid().ToString();
            var buyOrder = new Order
            {
                Id = buyOrderId,
                Symbol = "KASUSDT",
                Side = Mexc.Net.Enums.OrderSide.Buy,
                Type = Mexc.Net.Enums.OrderType.Market,
                Quantity = orderAmount,
                Status = Mexc.Net.Enums.OrderStatus.Filled,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Price = buyPrice,
                QuantityFilled = buyQty,
                Commission = totalCommission
            };
            var percentProfit = user.Settings.PercentProfit / 100m;
            var sellPrice = buyPrice * (1 + percentProfit);
            // Получить tickSize если нужно (можно добавить при необходимости)
            var sellOrder = new Order
            {
                Id = string.Empty,
                Symbol = "KASUSDT",
                Side = Mexc.Net.Enums.OrderSide.Sell,
                Type = Mexc.Net.Enums.OrderType.Limit,
                Quantity = buyQty,
                Price = sellPrice,
                Status = Mexc.Net.Enums.OrderStatus.New,
                CreatedAt = DateTime.UtcNow
            };
            var orderPair = new OrderPair
            {
                Id = pairId,
                UserId = user.Id,
                BuyOrder = buyOrder,
                SellOrder = sellOrder,
                CreatedAt = DateTime.UtcNow
            };
            await _orderPairRepo.AddAsync(orderPair);
            // Выставляем sell-ордер
            var sellResult = await mexcService.PlaceOrderAsync("KASUSDT", Mexc.Net.Enums.OrderSide.Sell, Mexc.Net.Enums.OrderType.Limit, buyQty, sellPrice, Mexc.Net.Enums.TimeInForce.GoodTillCanceled);
            if (sellResult.IsSuccess)
            {
                orderPair.SellOrder.Id = sellResult.Value;
                orderPair.SellOrder.Status = Mexc.Net.Enums.OrderStatus.New;
                orderPair.SellOrder.CreatedAt = DateTime.UtcNow;
                await _orderPairRepo.UpdateAsync(orderPair);

                // Отправить сообщение пользователю
                var msg = $"КУПЛЕНО\n\n{buyQty:F2} KAS по {buyPrice:F6} USDT\n\nПотрачено\n{(buyQty * buyPrice):F8} USDT\n\nВЫСТАВЛЕНО\n\n{buyQty:F2} KAS по {sellPrice:F6} USDT";
                await _botMessenger.SendMessage(user.Id, msg);
            }
        }
    }
} 