using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using FluentResults;
using KaspaBot.Application.Trading.Commands;
using KaspaBot.Application.Trading.Dtos;
using KaspaBot.Application.Users.Commands;
using KaspaBot.Application.Users.Dtos;
using KaspaBot.Domain.Entities;
using KaspaBot.Domain.Interfaces;
using MediatR;
using Mexc.Net.Enums;
using Mexc.Net.Objects.Models.Spot;
using Microsoft.Extensions.Logging;

[assembly: CompilationRelaxations(8)]
[assembly: RuntimeCompatibility(WrapNonExceptionThrows = true)]
[assembly: Debuggable(DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints)]
[assembly: TargetFramework(".NETCoreApp,Version=v8.0", FrameworkDisplayName = ".NET 8.0")]
[assembly: AssemblyCompany("KaspaBot.Application")]
[assembly: AssemblyConfiguration("Release")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0+f4aef71110d6b470e9e70939744edbbe17e6a180")]
[assembly: AssemblyProduct("KaspaBot.Application")]
[assembly: AssemblyTitle("KaspaBot.Application")]
[assembly: AssemblyVersion("1.0.0.0")]
[module: RefSafetyRules(11)]
namespace KaspaBot.Application.Users.Handlers
{
	public class CreateUserCommandHandler : IRequestHandler<CreateUserCommand, UserDto>
	{
		private readonly IUserRepository _userRepository;

		public CreateUserCommandHandler(IUserRepository userRepository)
		{
			_userRepository = userRepository;
		}

		public async Task<UserDto> Handle(CreateUserCommand request, CancellationToken cancellationToken)
		{
			User user = new User(request.UserId, request.Username);
			await _userRepository.AddAsync(user);
			return new UserDto(user);
		}
	}
}
namespace KaspaBot.Application.Users.Dtos
{
	public class UserDto
	{
		public long Id { get; set; }

		public string Username { get; set; }

		public UserDto(User user)
		{
			Id = user.Id;
			Username = user.Username;
		}
	}
}
namespace KaspaBot.Application.Users.Commands
{
	public record CreateUserCommand(long UserId, string Username) : IRequest<UserDto>, IBaseRequest;
}
namespace KaspaBot.Application.Trading.Handlers
{
	public class PlaceOrderCommandHandler : IRequestHandler<PlaceOrderCommand, Result<OrderDto>>
	{
		private readonly IMexcService _mexcService;

		private readonly IUserRepository _userRepository;

		private readonly ILogger<PlaceOrderCommandHandler> _logger;

		public PlaceOrderCommandHandler(IMexcService mexcService, IUserRepository userRepository, ILogger<PlaceOrderCommandHandler> logger)
		{
			_mexcService = mexcService;
			_userRepository = userRepository;
			_logger = logger;
		}

		public async Task<Result<OrderDto>> Handle(PlaceOrderCommand request, CancellationToken ct)
		{
			_ = 2;
			try
			{
				User user = await _userRepository.GetByIdAsync(request.UserId);
				if (user == null)
				{
					return Result.Fail("User not found");
				}
				Result<MexcAccountInfo> result = await _mexcService.GetAccountInfoAsync(ct);
				decimal freeUsdt = ((!result.IsSuccess) ? 0m : (result.Value.Balances.FirstOrDefault((MexcAccountBalance b) => b.Asset == "USDT")?.Available ?? 0m));
				decimal orderAmount = user.Settings.GetOrderAmount(freeUsdt);
				decimal value = ((request.Type == OrderType.Market) ? 0m : request.Price.GetValueOrDefault());
				Result<string> result2 = await _mexcService.PlaceOrderAsync(request.Symbol, request.Side, request.Type, orderAmount, value, request.TimeInForce, ct);
				if (result2.IsFailed)
				{
					return result2.ToResult<OrderDto>();
				}
				return Result.Ok(new OrderDto(new Order
				{
					Id = result2.Value,
					Symbol = request.Symbol,
					Side = request.Side,
					Type = request.Type,
					Quantity = orderAmount,
					Price = request.Price,
					Status = OrderStatus.New,
					CreatedAt = DateTime.UtcNow
				}));
			}
			catch (Exception exception)
			{
				_logger.LogError(exception, "Error placing order");
				return Result.Fail(new Error("Failed to place order").CausedBy(exception));
			}
		}
	}
}
namespace KaspaBot.Application.Trading.Dtos
{
	public class OrderDto
	{
		public string Id { get; set; } = string.Empty;

		public string Symbol { get; set; } = string.Empty;

		public OrderSide Side { get; set; }

		public OrderType Type { get; set; }

		public decimal Quantity { get; set; }

		public decimal? Price { get; set; }

		public OrderStatus Status { get; set; }

		public DateTime CreatedAt { get; set; }

		public OrderDto(Order order)
		{
			Id = order.Id;
			Symbol = order.Symbol;
			Side = order.Side;
			Type = order.Type;
			Quantity = order.Quantity;
			Price = order.Price;
			Status = order.Status;
			CreatedAt = order.CreatedAt;
		}
	}
}
namespace KaspaBot.Application.Trading.Commands
{
	public record PlaceOrderCommand(long UserId, string Symbol, OrderSide Side, OrderType Type, decimal Amount, decimal? Price = null, TimeInForce TimeInForce = TimeInForce.GoodTillCanceled) : IRequest<Result<OrderDto>>, IBaseRequest;
}
