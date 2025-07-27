using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using FluentResults;
using KaspaBot.Domain.Entities;
using KaspaBot.Domain.ValueObjects;
using Mexc.Net.Enums;
using Mexc.Net.Objects.Models.Spot;

[assembly: CompilationRelaxations(8)]
[assembly: RuntimeCompatibility(WrapNonExceptionThrows = true)]
[assembly: Debuggable(DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints)]
[assembly: TargetFramework(".NETCoreApp,Version=v8.0", FrameworkDisplayName = ".NET 8.0")]
[assembly: AssemblyCompany("KaspaBot.Domain")]
[assembly: AssemblyConfiguration("Release")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0+f4aef71110d6b470e9e70939744edbbe17e6a180")]
[assembly: AssemblyProduct("KaspaBot.Domain")]
[assembly: AssemblyTitle("KaspaBot.Domain")]
[assembly: AssemblyVersion("1.0.0.0")]
[module: RefSafetyRules(11)]
namespace KaspaBot.Domain.ValueObjects
{
	public class UserApiCredentials
	{
		public string ApiKey { get; set; } = string.Empty;

		public string ApiSecret { get; set; } = string.Empty;
	}
	public enum OrderAmountMode
	{
		Fixed,
		Dynamic
	}
	public class UserSettings
	{
		private decimal _orderAmount = 1m;

		private decimal _dynamicOrderCoef = 40m;

		public OrderAmountMode OrderAmountMode { get; set; }

		public decimal DynamicOrderCoef
		{
			get
			{
				if (!(_dynamicOrderCoef < 1m))
				{
					return _dynamicOrderCoef;
				}
				return 40m;
			}
			set
			{
				_dynamicOrderCoef = ((value < 1m) ? 40m : value);
			}
		}

		public decimal OrderAmount
		{
			get
			{
				return _orderAmount;
			}
			set
			{
				_orderAmount = ValidateOrderAmount(value);
			}
		}

		public decimal PercentPriceChange { get; set; } = 0.5m;

		public decimal PercentProfit { get; set; } = 0.5m;

		public decimal MaxUsdtUsing { get; set; } = 50m;

		public bool EnableAutoTrading { get; set; }

		public bool IsAutoTradeEnabled { get; set; }

		public decimal? LastDcaBuyPrice { get; set; }

		public DateTime? LastTradeTime { get; set; }

		public int ConsecutiveFailures { get; set; }

		public DateTime? LastBalanceCheck { get; set; }

		public decimal? LastKnownBalance { get; set; }

		public bool IsInDebounce { get; set; }

		public DateTime? DebounceStartTime { get; set; }

		public decimal GetOrderAmount(decimal freeUsdt)
		{
			decimal num = ((DynamicOrderCoef < 1m) ? 1m : DynamicOrderCoef);
			if (OrderAmountMode == OrderAmountMode.Fixed)
			{
				return OrderAmount;
			}
			decimal num2 = freeUsdt / num;
			if (!(num2 < 1m))
			{
				return Math.Floor(num2 * 100m) / 100m;
			}
			return 1m;
		}

		private static decimal ValidatePercent(decimal value, string propertyName)
		{
			if (value <= 0m || value > 100m)
			{
				throw new ArgumentOutOfRangeException(propertyName, $"Значение должно быть от 0.01 до 100, получено: {value}");
			}
			return value;
		}

		private static decimal ValidatePositive(decimal value, string propertyName)
		{
			if (value <= 0m)
			{
				throw new ArgumentOutOfRangeException(propertyName, $"Значение должно быть положительным, получено: {value}");
			}
			return value;
		}

		private static decimal ValidateOrderAmount(decimal value)
		{
			if (value < 1m)
			{
				throw new ArgumentOutOfRangeException("OrderAmount", $"Минимальная сумма ордера: 1 USDT, получено: {value}");
			}
			if (value > 10000m)
			{
				throw new ArgumentOutOfRangeException("OrderAmount", $"Максимальная сумма ордера: 10000 USDT, получено: {value}");
			}
			return value;
		}

		public bool IsValid()
		{
			try
			{
				ValidatePercent(PercentPriceChange, "PercentPriceChange");
				ValidatePercent(PercentProfit, "PercentProfit");
				ValidatePositive(MaxUsdtUsing, "MaxUsdtUsing");
				ValidateOrderAmount(OrderAmount);
				if (DynamicOrderCoef < 1m)
				{
					throw new ArgumentOutOfRangeException("DynamicOrderCoef");
				}
				return true;
			}
			catch
			{
				return false;
			}
		}
	}
}
namespace KaspaBot.Domain.Interfaces
{
	public interface IBotMessenger
	{
		Task SendMessage(long chatId, string text);
	}
	public interface IMexcService
	{
		Task<Result<MexcAccountInfo>> GetAccountInfoAsync(CancellationToken ct = default(CancellationToken));

		Task<Result<decimal>> GetSymbolPriceAsync(string symbol, CancellationToken ct = default(CancellationToken));

		Task<Result<string>> PlaceOrderAsync(string symbol, OrderSide side, OrderType type, decimal quantity, decimal? price = null, TimeInForce tif = TimeInForce.GoodTillCanceled, CancellationToken ct = default(CancellationToken));

		Task<Result<IEnumerable<MexcOrder>>> GetOpenOrdersAsync(string symbol, CancellationToken ct = default(CancellationToken));

		Task<Result<MexcOrder>> GetOrderAsync(string symbol, string orderId, CancellationToken ct = default(CancellationToken));

		Task<Result<bool>> CancelOrderAsync(string symbol, string orderId, CancellationToken ct = default(CancellationToken));

		Task<Result<string>> GetListenKeyAsync(CancellationToken ct = default(CancellationToken));

		Task<Result<(decimal Maker, decimal Taker)>> GetTradeFeeAsync(string symbol, CancellationToken ct = default(CancellationToken));
	}
	public interface IOrderRecoveryService
	{
		Task RunRecoveryForUser(long userId, CancellationToken cancellationToken);
	}
	public interface IPriceStreamService : IDisposable
	{
		Task StartStreamAsync(string symbol, Action<decimal> onPriceUpdate);
	}
	public interface IUserRepository
	{
		Task<User?> GetByIdAsync(long userId);

		Task AddAsync(User user);

		Task UpdateAsync(User user);

		Task<List<User>> GetAllAsync();

		Task<bool> ExistsAsync(long userId);

		Task DeleteAsync(long userId);
	}
}
namespace KaspaBot.Domain.Entities
{
	public class Order
	{
		public required string Id { get; set; }

		public required string Symbol { get; set; }

		public OrderSide Side { get; set; }

		public OrderType Type { get; set; }

		public decimal Quantity { get; set; }

		public decimal? Price { get; set; }

		public OrderStatus Status { get; set; }

		public DateTime CreatedAt { get; set; }

		public DateTime? UpdatedAt { get; set; }

		public decimal QuantityFilled { get; set; }

		public decimal QuoteQuantityFilled { get; set; }

		public decimal Commission { get; set; }
	}
	public class OrderPair
	{
		public required string Id { get; set; }

		public required Order BuyOrder { get; set; }

		public required Order SellOrder { get; set; }

		public DateTime CreatedAt { get; set; }

		public DateTime? CompletedAt { get; set; }

		public decimal? Profit { get; set; }

		public long UserId { get; set; }
	}
	public class User
	{
		public long Id { get; set; }

		public string Username { get; set; }

		public DateTime RegistrationDate { get; set; }

		public UserSettings Settings { get; set; }

		public UserApiCredentials ApiCredentials { get; set; }

		public bool IsActive { get; set; }

		public User(long id, string username)
		{
			Id = id;
			Username = username;
			RegistrationDate = DateTime.UtcNow;
			Settings = new UserSettings();
			ApiCredentials = new UserApiCredentials();
			IsActive = true;
		}
	}
}
