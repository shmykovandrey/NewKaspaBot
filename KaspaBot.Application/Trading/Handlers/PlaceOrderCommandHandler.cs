using FluentResults;
using KaspaBot.Application.Trading.Commands;
using KaspaBot.Application.Trading.Dtos;
using KaspaBot.Domain.Entities;
using KaspaBot.Domain.Enums;
using KaspaBot.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace KaspaBot.Application.Trading.Handlers;

public class PlaceOrderCommandHandler : IRequestHandler<PlaceOrderCommand, Result<OrderDto>>
{
    private readonly IMexcService _mexcService;
    private readonly IUserRepository _userRepository;
    private readonly ILogger<PlaceOrderCommandHandler> _logger;

    public PlaceOrderCommandHandler(
        IMexcService mexcService,
        IUserRepository userRepository,
        ILogger<PlaceOrderCommandHandler> logger)
    {
        _mexcService = mexcService;
        _userRepository = userRepository;
        _logger = logger;
    }

    public async Task<Result<OrderDto>> Handle(PlaceOrderCommand request, CancellationToken ct)
    {
        try
        {
            var user = await _userRepository.GetByIdAsync(request.UserId);
            if (user == null)
                return Result.Fail("User not found");

            var orderResult = await _mexcService.PlaceOrderAsync(
                request.Symbol,
                request.Side,
                request.Type,
                request.Side == OrderSide.Buy ? request.Amount : request.Amount,
                request.Price,
                ct);

            if (orderResult.IsFailed)
                return orderResult.ToResult<OrderDto>(); // Изменено здесь

            return Result.Ok(new OrderDto(orderResult.Value));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error placing order");
            return Result.Fail(new Error("Failed to place order").CausedBy(ex));
        }
    }
}