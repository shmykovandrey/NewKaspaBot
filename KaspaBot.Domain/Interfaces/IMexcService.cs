using FluentResults;
using Mexc.Net.Enums;
using Mexc.Net.Objects.Models.Spot;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KaspaBot.Domain.Interfaces
{
    public interface IMexcService
    {
        Task<Result<MexcAccountInfo>> GetAccountInfoAsync(CancellationToken ct = default);
        Task<Result<decimal>> GetSymbolPriceAsync(string symbol, CancellationToken ct = default);
        Task<Result<string>> PlaceOrderAsync(
            string symbol,
            OrderSide side,
            OrderType type,
            decimal quantity,
            decimal? price = null,
            TimeInForce tif = TimeInForce.GoodTillCanceled,
            CancellationToken ct = default);
        Task<Result<IEnumerable<MexcOrder>>> GetOpenOrdersAsync(string symbol, CancellationToken ct = default);
        Task<Result<MexcOrder>> GetOrderAsync(string symbol, string orderId, CancellationToken ct = default);
        Task<Result<bool>> CancelOrderAsync(string symbol, string orderId, CancellationToken ct = default);
        Task<Result<string>> GetListenKeyAsync(CancellationToken ct = default);
        Task<Result<(decimal Maker, decimal Taker)>> GetTradeFeeAsync(string symbol, CancellationToken ct = default);
    }
}