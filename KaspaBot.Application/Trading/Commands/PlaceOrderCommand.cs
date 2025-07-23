using FluentResults;
using KaspaBot.Application.Trading.Dtos;
using KaspaBot.Domain.Enums;
using MediatR;

namespace KaspaBot.Application.Trading.Commands;

public record PlaceOrderCommand(
    long UserId,
    string Symbol,
    OrderSide Side,
    OrderType Type,
    decimal Amount,
    decimal? Price = null) : IRequest<Result<OrderDto>>;