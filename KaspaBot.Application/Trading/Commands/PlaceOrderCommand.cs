using FluentResults;
using KaspaBot.Application.Trading.Dtos;
using MediatR;
using Mexc.Net.Enums;

namespace KaspaBot.Application.Trading.Commands
{
    public record PlaceOrderCommand(
        long UserId,
        string Symbol,
        OrderSide Side,
        OrderType Type,
        decimal Amount,
        decimal? Price = null,
        TimeInForce TimeInForce = TimeInForce.GoodTillCanceled)
        : IRequest<Result<OrderDto>>;
}