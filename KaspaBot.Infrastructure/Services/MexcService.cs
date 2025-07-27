//MexcService.cs
using CryptoExchange.Net.Authentication;
using FluentResults;
using KaspaBot.Domain.Interfaces;
using Mexc.Net.Clients;
using Mexc.Net.Enums;
using Mexc.Net.Objects;
using Mexc.Net.Objects.Models.Spot;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace KaspaBot.Infrastructure.Services
{
    public class MexcService : IMexcService
    {
        private readonly MexcRestClient _restClient;
        public readonly MexcSocketClient _socketClient;
        private readonly ILogger<MexcService> _logger;

        public MexcService(
            string apiKey,
            string apiSecret,
            ILogger<MexcService> logger)
        {
            _restClient = new MexcRestClient(options =>
            {
                options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
                options.AutoTimestamp = true;
            });

            _socketClient = new MexcSocketClient(options =>
            {
                options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
            });

            _logger = logger;
        }

        public async Task<Result<MexcAccountInfo>> GetAccountInfoAsync(CancellationToken ct = default)
        {
            try
            {
                var result = await _restClient.SpotApi.Account.GetAccountInfoAsync(ct);
                if (!result.Success || result.Data == null)
                {
                    _logger.LogError("Failed to get account info: {Error}", result.Error?.Message);
                    return Result.Fail<MexcAccountInfo>("Failed to get account info");
                }
                return Result.Ok(result.Data);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting account info");
                return Result.Fail<MexcAccountInfo>(new Error("Failed to get account info").CausedBy(ex));
            }
        }

        public async Task<Result<decimal>> GetSymbolPriceAsync(string symbol, CancellationToken ct = default)
        {
            try
            {
                var result = await _restClient.SpotApi.ExchangeData.GetTickerAsync(symbol, ct);
                if (!result.Success || result.Data == null)
                {
                    _logger.LogError("Failed to get price for {Symbol}: {Error}", symbol, result.Error?.Message);
                    return Result.Fail<decimal>("Failed to get symbol price");
                }
                return Result.Ok(result.Data.LastPrice);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting price for {Symbol}", symbol);
                return Result.Fail<decimal>(new Error("Failed to get symbol price").CausedBy(ex));
            }
        }

        public async Task<Result<string>> PlaceOrderAsync(
            string symbol,
            Mexc.Net.Enums.OrderSide side,
            Mexc.Net.Enums.OrderType type,
            decimal amount,
            decimal? price = null,
            Mexc.Net.Enums.TimeInForce tif = Mexc.Net.Enums.TimeInForce.GoodTillCanceled,
            CancellationToken ct = default)
        {
            // Маркет-бай через сумму (quoteOrderQty)
            if (type == Mexc.Net.Enums.OrderType.Market && side == Mexc.Net.Enums.OrderSide.Buy)
            {
                var result = await _restClient.SpotApi.Trading.PlaceOrderAsync(
                    symbol: symbol,
                    side: side,
                    type: type,
                    quantity: null,
                    quoteQuantity: amount,
                    ct: ct
                );
                return result.Success
                    ? Result.Ok(result.Data.OrderId.ToString())
                    : Result.Fail<string>(result.Error?.Message ?? "Failed to place order");
            }
            // Для LIMIT и MARKET SELL — только рабочая перегрузка
            var normalResult = await _restClient.SpotApi.Trading.PlaceOrderAsync(
                symbol: symbol,
                side: side,
                type: type,
                quantity: amount,
                price: price,
                ct: ct
            );
            return normalResult.Success
                ? Result.Ok(normalResult.Data.OrderId.ToString())
                : Result.Fail<string>(normalResult.Error?.Message ?? "Failed to place order");
        }

        public async Task<Result<IEnumerable<MexcOrder>>> GetOpenOrdersAsync(string symbol, CancellationToken ct = default)
        {
            try
            {
                var result = await _restClient.SpotApi.Trading.GetOpenOrdersAsync(symbol, ct);
                if (!result.Success || result.Data == null)
                {
                    _logger.LogError("Failed to get open orders for {Symbol}: {Error}", symbol, result.Error?.Message);
                    return Result.Fail<IEnumerable<MexcOrder>>("Failed to get open orders");
                }
                return Result.Ok((IEnumerable<MexcOrder>)result.Data);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting open orders for {Symbol}", symbol);
                return Result.Fail<IEnumerable<MexcOrder>>(new Error("Failed to get open orders").CausedBy(ex));
            }
        }

        public async Task<Result<Mexc.Net.Objects.Models.Spot.MexcOrder>> GetOrderAsync(string symbol, string orderId, CancellationToken ct = default)
        {
            var result = await _restClient.SpotApi.Trading.GetOrderAsync(symbol, orderId, ct.ToString());
            return result.Success
                ? Result.Ok(result.Data)
                : Result.Fail<Mexc.Net.Objects.Models.Spot.MexcOrder>(result.Error?.Message ?? "Failed to get order");
        }

        public async Task<Result<bool>> CancelOrderAsync(string symbol, string orderId, CancellationToken ct = default)
        {
            try
            {
                if (!long.TryParse(orderId, out var id))
                {
                    _logger.LogError("Invalid order ID format: {OrderId}", orderId);
                    return Result.Fail<bool>("Invalid order ID format");
                }

                var result = await _restClient.SpotApi.Trading.CancelOrderAsync(symbol, id.ToString(), ct.ToString());
                if (!result.Success)
                {
                    _logger.LogError("Failed to cancel order {OrderId} for {Symbol}: {Error}", orderId, symbol, result.Error?.Message);
                    return Result.Fail<bool>("Failed to cancel order");
                }

                return Result.Ok(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error canceling order {OrderId} for {Symbol}", orderId, symbol);
                return Result.Fail<bool>(new Error("Failed to cancel order").CausedBy(ex));
            }
        }

        public async Task<Result<string>> GetListenKeyAsync(CancellationToken ct = default)
        {
            try
            {
                var result = await _restClient.SpotApi.Account.StartUserStreamAsync(ct);
                if (!result.Success || string.IsNullOrEmpty(result.Data))
                {
                    _logger.LogError("Failed to get listenKey: {Error}", result.Error?.Message);
                    return Result.Fail<string>("Failed to get listenKey");
                }
                return Result.Ok(result.Data);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting listenKey");
                return Result.Fail<string>(new Error("Failed to get listenKey").CausedBy(ex));
            }
        }

        public async Task<Result<IEnumerable<Mexc.Net.Objects.Models.Spot.MexcUserTrade>>> GetOrderTradesAsync(string symbol, string orderId, CancellationToken ct = default)
        {
            var tradesResult = await _restClient.SpotApi.Trading.GetUserTradesAsync(symbol, orderId: orderId, ct: ct);
            if (!tradesResult.Success || tradesResult.Data == null)
                return Result.Fail<IEnumerable<Mexc.Net.Objects.Models.Spot.MexcUserTrade>>(tradesResult.Error?.Message ?? "Failed to get trades");
            return Result.Ok(tradesResult.Data.AsEnumerable());
        }

        public async Task<Result<IEnumerable<Mexc.Net.Objects.Models.Spot.MexcOrder>>> GetOrderHistoryAsync(string symbol, CancellationToken ct = default)
        {
            try
            {
                var result = await _restClient.SpotApi.Trading.GetOrdersAsync(symbol, limit: 1000, ct: ct);
                if (!result.Success || result.Data == null)
                {
                    _logger.LogError("Failed to get order history for {Symbol}: {Error}", symbol, result.Error?.Message);
                    return Result.Fail<IEnumerable<Mexc.Net.Objects.Models.Spot.MexcOrder>>(result.Error?.Message ?? "Failed to get order history");
                }
                return Result.Ok(result.Data.AsEnumerable());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting order history for {Symbol}", symbol);
                return Result.Fail<IEnumerable<Mexc.Net.Objects.Models.Spot.MexcOrder>>(new Error("Failed to get order history").CausedBy(ex));
            }
        }

        public async Task<decimal> GetTickSizeAsync(string symbol, CancellationToken ct = default)
        {
            var result = await _restClient.SpotApi.ExchangeData.GetExchangeInfoAsync(new[] { symbol }, ct);
            if (result.Success && result.Data != null)
            {
                var symbolInfo = result.Data.Symbols.FirstOrDefault(s => s.Name == symbol);
                if (symbolInfo != null)
                {
                    _logger.LogError($"[SELL DEBUG] MexcSymbol: {System.Text.Json.JsonSerializer.Serialize(symbolInfo)}");
                }
            }
            return 0.000001m; // fallback
        }

        public async Task<Result<(decimal Maker, decimal Taker)>> GetTradeFeeAsync(string symbol, CancellationToken ct = default)
        {
            try
            {
                // Mexc.Net >= 1.2.5: SpotApi.Account.GetTradeFeeAsync
                var result = await _restClient.SpotApi.Account.GetTradeFeeAsync(symbol, ct);
                if (!result.Success || result.Data == null)
                {
                    _logger.LogError("Failed to get trade fee for {Symbol}: {Error}", symbol, result.Error?.Message);
                    return Result.Fail<(decimal, decimal)>("Failed to get trade fee");
                }
                return Result.Ok((result.Data.MakerFee, result.Data.TakerFee));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting trade fee for {Symbol}", symbol);
                return Result.Fail<(decimal, decimal)>(new Error("Failed to get trade fee").CausedBy(ex));
            }
        }
    }
}