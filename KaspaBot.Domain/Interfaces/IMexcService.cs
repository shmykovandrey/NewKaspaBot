using CryptoExchange.Net.CommonObjects;
using FluentResults;
using KaspaBot.Domain.Enums;
using KaspaBot.Domain.Models;
using Mexc.Net.Enums;
using Mexc.Net.Objects.Models.Spot;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace KaspaBot.Domain.Interfaces
{
    public interface IMexcService
    {
        Task<Result<MexcAccountInfo>> GetAccountInfoAsync();
        Task<Result<decimal>> GetSymbolPriceAsync(string symbol);
        Task<Result<OrderId>> PlaceOrderAsync(
            string symbol,
            OrderSide side,
            decimal quantity,
            decimal price,
            OrderType type,
            TimeInForce tif);
        Task<Result<IEnumerable<MexcOrder>>> GetOpenOrdersAsync(string symbol);
        Task<Result<MexcOrder>> GetOrderAsync(string symbol, string orderId);
        Task<Result<bool>> CancelOrderAsync(string symbol, string orderId);
    }
}
