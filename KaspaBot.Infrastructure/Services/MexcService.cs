using FluentResults;
using KaspaBot.Domain.Enums;
using KaspaBot.Domain.Interfaces;
using KaspaBot.Domain.Models;
using Mexc.Net.Clients;
using Mexc.Net.Enums;
using Mexc.Net.Objects;
using Mexc.Net.Objects.Models.Spot;
using Mexc.Net.Objects.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KaspaBot.Infrastructure.Services
{
    public class MexcService : IMexcService
    {
        private readonly MexcClient _client;
        private readonly ILogger<MexcService> _logger;

        public MexcService(IOptions<MexcClientOptions> options, ILogger<MexcService> logger)
        {
            _client = new MexcClient(options.Value);
            _logger = logger;
        }

        public async Task<Result<MexcAccountInfo>> GetAccountInfoAsync()
        {
            var result = await _client.SpotApi.Account.GetAccountInfoAsync();
            if (!result.Success || result.Data == null)
            {
                _logger.LogError("Failed to get account info: {Error}", result.Error?.Message);
                return Result.Fail<MexcAccountInfo>("Failed to get account info");
            }

            return Result.Ok(result.Data);
        }

        public async Task<Result<decimal>> GetSymbolPriceAsync(string symbol)
        {
            var result = await _client.SpotApi.ExchangeData.GetPriceAsync(symbol);
            if (!result.Success || result.Data == null)
            {
                _logger.LogError("Failed to get price for symbol {Symbol}: {Error}", symbol, result.Error?.Message);
                return Result.Fail<decimal>("Failed to get symbol price");
            }

            return Result.Ok(result.Data.Price);
        }

        public async Task<Result<OrderId>> PlaceOrderAsync(
            string symbol,
            OrderSide side,
            decimal quantity,
            decimal price,
            OrderType type,
            TimeInForce tif)
        {
            var result = await _client.SpotApi.Trading.PlaceOrderAsync(
                symbol: symbol,
                side: side,
                type: type,
                quantity: quantity,
                price: price,
                timeInForce: tif
            );

            if (!result.Success || result.Data == null)
            {
                _logger.LogError("Failed to place order: {Error}", result.Error?.Message);
                return Result.Fail<OrderId>("Failed to place order");
            }

            return Result.Ok(new OrderId { Id = result.Data.OrderId.ToString() });
        }

        public async Task<Result<IEnumerable<MexcOrder>>> GetOpenOrdersAsync(string symbol)
        {
            var result = await _client.SpotApi.Trading.GetOpenOrdersAsync(symbol);
            if (!result.Success || result.Data == null)
            {
                _logger.LogError("Failed to get open orders for symbol {Symbol}: {Error}", symbol, result.Error?.Message);
                return Result.Fail<IEnumerable<MexcOrder>>("Failed to get open orders");
            }

            return Result.Ok(result.Data);
        }

        public async Task<Result<MexcOrder>> GetOrderAsync(string symbol, string orderId)
        {
            if (!long.TryParse(orderId, out long id))
            {
                return Result.Fail<MexcOrder>("Invalid order ID format");
            }

            var result = await _client.SpotApi.Trading.GetOrderAsync(symbol, id);
            if (!result.Success || result.Data == null)
            {
                _logger.LogError("Failed to get order {OrderId} for {Symbol}: {Error}", orderId, symbol, result.Error?.Message);
                return Result.Fail<MexcOrder>("Failed to get order");
            }

            return Result.Ok(result.Data);
        }

        public async Task<Result<bool>> CancelOrderAsync(string symbol, string orderId)
        {
            if (!long.TryParse(orderId, out long id))
            {
                return Result.Fail<bool>("Invalid order ID format");
            }

            var result = await _client.SpotApi.Trading.CancelOrderAsync(symbol, id);
            if (!result.Success)
            {
                _logger.LogError("Failed to cancel order {OrderId} for {Symbol}: {Error}", orderId, symbol, result.Error?.Message);
                return Result.Fail<bool>("Failed to cancel order");
            }

            return Result.Ok(true);
        }
    }
}
