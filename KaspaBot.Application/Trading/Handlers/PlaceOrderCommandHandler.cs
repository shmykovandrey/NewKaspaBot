using FluentResults;
using KaspaBot.Application.Trading.Commands;
using KaspaBot.Application.Trading.Dtos;
using KaspaBot.Domain.Entities;
using KaspaBot.Domain.Interfaces;
using MediatR;
using Mexc.Net.Enums;
using Microsoft.Extensions.Logging;

namespace KaspaBot.Application.Trading.Handlers
{
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

                // Для рыночных ордеров цена не требуется
                decimal price = request.Type == OrderType.Market ? 0 : request.Price ?? 0;

                var orderResult = await _mexcService.PlaceOrderAsync(
                    symbol: request.Symbol,
                    side: request.Side,
                    type: request.Type,  // Теперь передаем OrderType напрямую
                    quantity: request.Amount,
                    price: price,
                    tif: request.TimeInForce,
                    ct: ct);

                if (orderResult.IsFailed)
                    return orderResult.ToResult<OrderDto>();

                var order = new Order
                {
                    Id = orderResult.Value,
                    Symbol = request.Symbol,
                    Side = request.Side,
                    Type = request.Type,
                    Quantity = request.Amount,
                    Price = request.Price,
                    Status = OrderStatus.New,
                    CreatedAt = DateTime.UtcNow
                };

                return Result.Ok(new OrderDto(order));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error placing order");
                return Result.Fail(new Error("Failed to place order").CausedBy(ex));
            }
        }
    }
}