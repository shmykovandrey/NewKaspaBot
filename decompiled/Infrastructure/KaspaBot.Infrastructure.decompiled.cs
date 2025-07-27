using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Objects.Sockets;
using FluentResults;
using KaspaBot.Domain.Entities;
using KaspaBot.Domain.Interfaces;
using KaspaBot.Domain.ValueObjects;
using KaspaBot.Infrastructure.Options;
using KaspaBot.Infrastructure.Persistence;
using KaspaBot.Infrastructure.Repositories;
using KaspaBot.Infrastructure.Services;
using MediatR;
using Mexc.Net.Clients;
using Mexc.Net.Enums;
using Mexc.Net.Interfaces.Clients;
using Mexc.Net.Interfaces.Clients.SpotApi;
using Mexc.Net.Objects.Models.Spot;
using Mexc.Net.Objects.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

[assembly: CompilationRelaxations(8)]
[assembly: RuntimeCompatibility(WrapNonExceptionThrows = true)]
[assembly: Debuggable(DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints)]
[assembly: TargetFramework(".NETCoreApp,Version=v8.0", FrameworkDisplayName = ".NET 8.0")]
[assembly: AssemblyCompany("KaspaBot.Infrastructure")]
[assembly: AssemblyConfiguration("Release")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0+f4aef71110d6b470e9e70939744edbbe17e6a180")]
[assembly: AssemblyProduct("KaspaBot.Infrastructure")]
[assembly: AssemblyTitle("KaspaBot.Infrastructure")]
[assembly: AssemblyVersion("1.0.0.0")]
[module: RefSafetyRules(11)]
namespace KaspaBot.Infrastructure.Services
{
	public class CacheService
	{
		private readonly ConcurrentDictionary<string, (object Value, DateTime Expiry)> _cache = new ConcurrentDictionary<string, (object, DateTime)>();

		private readonly ILogger<CacheService> _logger;

		private readonly Timer _cleanupTimer;

		public CacheService(ILogger<CacheService> logger)
		{
			_logger = logger;
			_cleanupTimer = new Timer(CleanupExpiredItems, null, TimeSpan.FromMinutes(5.0), TimeSpan.FromMinutes(5.0));
		}

		public T? Get<T>(string key) where T : class
		{
			if (_cache.TryGetValue(key, out (object, DateTime) value) && DateTime.UtcNow < value.Item2)
			{
				return (T)value.Item1;
			}
			if (DateTime.UtcNow >= value.Item2)
			{
				_cache.TryRemove(key, out (object, DateTime) _);
			}
			return null;
		}

		public void Set<T>(string key, T value, TimeSpan expiry) where T : class
		{
			_cache[key] = (value, DateTime.UtcNow.Add(expiry));
		}

		public void Remove(string key)
		{
			_cache.TryRemove(key, out (object, DateTime) _);
		}

		public void Clear()
		{
			_cache.Clear();
		}

		private void CleanupExpiredItems(object? state)
		{
			DateTime now = DateTime.UtcNow;
			List<string> list = (from kvp in _cache
				where now >= kvp.Value.Expiry
				select kvp.Key).ToList();
			foreach (string item in list)
			{
				_cache.TryRemove(item, out (object, DateTime) _);
			}
			if (list.Count > 0)
			{
				_logger.LogDebug("Cleaned up {Count} expired cache items", list.Count);
			}
		}

		public void Dispose()
		{
			_cleanupTimer?.Dispose();
		}
	}
	public class EncryptionService
	{
		private const string Prefix = "ENC:";

		public string Encrypt(string plainText)
		{
			byte[] inArray = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), null, DataProtectionScope.CurrentUser);
			return "ENC:" + Convert.ToBase64String(inArray);
		}

		public bool IsEncrypted(string value)
		{
			if (string.IsNullOrEmpty(value))
			{
				return false;
			}
			return value?.StartsWith("ENC:") ?? false;
		}

		public string Decrypt(string cipherText)
		{
			if (!IsEncrypted(cipherText))
			{
				return cipherText;
			}
			try
			{
				byte[] bytes = ProtectedData.Unprotect(Convert.FromBase64String(cipherText.Substring("ENC:".Length)), null, DataProtectionScope.CurrentUser);
				return Encoding.UTF8.GetString(bytes);
			}
			catch (FormatException)
			{
				Console.WriteLine("[EncryptionService] WARNING: Не base64 строка при расшифровке: " + cipherText);
				return cipherText;
			}
			catch (CryptographicException)
			{
				Console.WriteLine("[EncryptionService] WARNING: Не DPAPI строка при расшифровке: " + cipherText);
				return cipherText;
			}
		}
	}
	public class MetricsService
	{
		private readonly ConcurrentDictionary<string, long> _counters = new ConcurrentDictionary<string, long>();

		private readonly ConcurrentDictionary<string, double> _gauges = new ConcurrentDictionary<string, double>();

		private readonly ConcurrentDictionary<string, List<double>> _histograms = new ConcurrentDictionary<string, List<double>>();

		private readonly ILogger<MetricsService> _logger;

		private readonly Timer _reportingTimer;

		public MetricsService(ILogger<MetricsService> logger)
		{
			_logger = logger;
			_reportingTimer = new Timer(ReportMetrics, null, TimeSpan.FromMinutes(5.0), TimeSpan.FromMinutes(5.0));
		}

		public void IncrementCounter(string name, long value = 1L)
		{
			_counters.AddOrUpdate(name, value, (string key, long oldValue) => oldValue + value);
		}

		public void SetGauge(string name, double value)
		{
			_gauges.AddOrUpdate(name, value, (string key, double oldValue) => value);
		}

		public void RecordHistogram(string name, double value)
		{
			_histograms.AddOrUpdate(name, new List<double> { value }, delegate(string key, List<double> oldValue)
			{
				oldValue.Add(value);
				if (oldValue.Count > 100)
				{
					oldValue.RemoveAt(0);
				}
				return oldValue;
			});
		}

		public long GetCounter(string name)
		{
			return _counters.GetValueOrDefault(name, 0L);
		}

		public double GetGauge(string name)
		{
			return _gauges.GetValueOrDefault(name, 0.0);
		}

		public (double Min, double Max, double Avg, double P95) GetHistogram(string name)
		{
			if (!_histograms.TryGetValue(name, out List<double> value) || !value.Any())
			{
				return (Min: 0.0, Max: 0.0, Avg: 0.0, P95: 0.0);
			}
			List<double> list = value.OrderBy((double v) => v).ToList();
			double item = list.First();
			double item2 = list.Last();
			double item3 = list.Average();
			int index = (int)((double)list.Count * 0.95);
			double item4 = list[index];
			return (Min: item, Max: item2, Avg: item3, P95: item4);
		}

		private void ReportMetrics(object? state)
		{
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("=== METRICS REPORT ===");
			foreach (KeyValuePair<string, long> counter in _counters)
			{
				StringBuilder stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder3 = stringBuilder2;
				StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(10, 2, stringBuilder2);
				handler.AppendLiteral("Counter ");
				handler.AppendFormatted(counter.Key);
				handler.AppendLiteral(": ");
				handler.AppendFormatted(counter.Value);
				stringBuilder3.AppendLine(ref handler);
			}
			foreach (KeyValuePair<string, double> gauge in _gauges)
			{
				StringBuilder stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder4 = stringBuilder2;
				StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(8, 2, stringBuilder2);
				handler.AppendLiteral("Gauge ");
				handler.AppendFormatted(gauge.Key);
				handler.AppendLiteral(": ");
				handler.AppendFormatted(gauge.Value, "F2");
				stringBuilder4.AppendLine(ref handler);
			}
			foreach (KeyValuePair<string, List<double>> histogram2 in _histograms)
			{
				(double, double, double, double) histogram = GetHistogram(histogram2.Key);
				StringBuilder stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder5 = stringBuilder2;
				StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(34, 5, stringBuilder2);
				handler.AppendLiteral("Histogram ");
				handler.AppendFormatted(histogram2.Key);
				handler.AppendLiteral(": Min=");
				handler.AppendFormatted(histogram.Item1, "F2");
				handler.AppendLiteral(", Max=");
				handler.AppendFormatted(histogram.Item2, "F2");
				handler.AppendLiteral(", Avg=");
				handler.AppendFormatted(histogram.Item3, "F2");
				handler.AppendLiteral(", P95=");
				handler.AppendFormatted(histogram.Item4, "F2");
				stringBuilder5.AppendLine(ref handler);
			}
			_logger.LogInformation(stringBuilder.ToString());
		}

		public void Reset()
		{
			_counters.Clear();
			_gauges.Clear();
			_histograms.Clear();
		}

		public void Dispose()
		{
			_reportingTimer?.Dispose();
		}
	}
	public class MexcService : IMexcService
	{
		private readonly MexcRestClient _restClient;

		public readonly MexcSocketClient _socketClient;

		private readonly ILogger<MexcService> _logger;

		private readonly KaspaBot.Infrastructure.Options.MexcOptions _options;

		public MexcService(string apiKey, string apiSecret, ILogger<MexcService> logger, IOptions<KaspaBot.Infrastructure.Options.MexcOptions> options)
		{
			_restClient = new MexcRestClient(delegate(MexcRestOptions mexcRestOptions)
			{
				mexcRestOptions.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
				mexcRestOptions.AutoTimestamp = true;
			});
			_socketClient = new MexcSocketClient(delegate(MexcSocketOptions mexcSocketOptions)
			{
				mexcSocketOptions.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
			});
			_logger = logger;
			_options = options.Value;
		}

		public static MexcService Create(string apiKey, string apiSecret, ILogger<MexcService> logger)
		{
			KaspaBot.Infrastructure.Options.MexcOptions options = new KaspaBot.Infrastructure.Options.MexcOptions
			{
				ApiKey = apiKey,
				ApiSecret = apiSecret
			};
			return new MexcService(apiKey, apiSecret, logger, Microsoft.Extensions.Options.Options.Create(options));
		}

		public async Task<Result<MexcAccountInfo>> GetAccountInfoAsync(CancellationToken ct = default(CancellationToken))
		{
			try
			{
				WebCallResult<MexcAccountInfo> webCallResult = await _restClient.SpotApi.Account.GetAccountInfoAsync(ct);
				if (!webCallResult.Success || webCallResult.Data == null)
				{
					_logger.LogError("Failed to get account info: {Error}", webCallResult.Error?.Message);
					return Result.Fail<MexcAccountInfo>("Failed to get account info");
				}
				return Result.Ok(webCallResult.Data);
			}
			catch (Exception exception)
			{
				_logger.LogError(exception, "Error getting account info");
				return Result.Fail<MexcAccountInfo>(new FluentResults.Error("Failed to get account info").CausedBy(exception));
			}
		}

		public async Task<Result<decimal>> GetSymbolPriceAsync(string symbol, CancellationToken ct = default(CancellationToken))
		{
			try
			{
				WebCallResult<MexcTicker> webCallResult = await _restClient.SpotApi.ExchangeData.GetTickerAsync(symbol, ct);
				if (!webCallResult.Success || webCallResult.Data == null)
				{
					_logger.LogError("Failed to get price for {Symbol}: {Error}", symbol, webCallResult.Error?.Message);
					return Result.Fail<decimal>("Failed to get symbol price");
				}
				return Result.Ok(webCallResult.Data.LastPrice);
			}
			catch (Exception exception)
			{
				_logger.LogError(exception, "Error getting price for {Symbol}", symbol);
				return Result.Fail<decimal>(new FluentResults.Error("Failed to get symbol price").CausedBy(exception));
			}
		}

		public async Task<Result<string>> PlaceOrderAsync(string symbol, OrderSide side, OrderType type, decimal amount, decimal? price = null, TimeInForce tif = TimeInForce.GoodTillCanceled, CancellationToken ct = default(CancellationToken))
		{
			CancellationToken ct2;
			if (type == OrderType.Market && side == OrderSide.Buy)
			{
				IMexcRestClientSpotApiTrading trading = _restClient.SpotApi.Trading;
				decimal? quoteQuantity = amount;
				ct2 = ct;
				WebCallResult<MexcOrder> webCallResult = await trading.PlaceOrderAsync(symbol, side, type, null, quoteQuantity, null, null, ct2);
				return webCallResult.Success ? Result.Ok(webCallResult.Data.OrderId.ToString()) : Result.Fail<string>(webCallResult.Error?.Message ?? "Failed to place order");
			}
			IMexcRestClientSpotApiTrading trading2 = _restClient.SpotApi.Trading;
			decimal? quantity = amount;
			ct2 = ct;
			WebCallResult<MexcOrder> webCallResult2 = await trading2.PlaceOrderAsync(symbol, side, type, quantity, null, price, null, ct2);
			return webCallResult2.Success ? Result.Ok(webCallResult2.Data.OrderId.ToString()) : Result.Fail<string>(webCallResult2.Error?.Message ?? "Failed to place order");
		}

		public async Task<Result<IEnumerable<MexcOrder>>> GetOpenOrdersAsync(string symbol, CancellationToken ct = default(CancellationToken))
		{
			try
			{
				WebCallResult<MexcOrder[]> webCallResult = await _restClient.SpotApi.Trading.GetOpenOrdersAsync(symbol, ct);
				if (!webCallResult.Success || webCallResult.Data == null)
				{
					_logger.LogError("Failed to get open orders for {Symbol}: {Error}", symbol, webCallResult.Error?.Message);
					return Result.Fail<IEnumerable<MexcOrder>>("Failed to get open orders");
				}
				return Result.Ok((IEnumerable<MexcOrder>)webCallResult.Data);
			}
			catch (Exception exception)
			{
				_logger.LogError(exception, "Error getting open orders for {Symbol}", symbol);
				return Result.Fail<IEnumerable<MexcOrder>>(new FluentResults.Error("Failed to get open orders").CausedBy(exception));
			}
		}

		public async Task<Result<MexcOrder>> GetOrderAsync(string symbol, string orderId, CancellationToken ct = default(CancellationToken))
		{
			WebCallResult<MexcOrder> webCallResult = await _restClient.SpotApi.Trading.GetOrderAsync(symbol, orderId, ct.ToString());
			return webCallResult.Success ? Result.Ok(webCallResult.Data) : Result.Fail<MexcOrder>(webCallResult.Error?.Message ?? "Failed to get order");
		}

		public async Task<Result<bool>> CancelOrderAsync(string symbol, string orderId, CancellationToken ct = default(CancellationToken))
		{
			try
			{
				if (!long.TryParse(orderId, out var result))
				{
					_logger.LogError("Invalid order ID format: {OrderId}", orderId);
					return Result.Fail<bool>("Invalid order ID format");
				}
				WebCallResult<MexcOrder> webCallResult = await _restClient.SpotApi.Trading.CancelOrderAsync(symbol, result.ToString(), ct.ToString());
				if (!webCallResult.Success)
				{
					_logger.LogError("Failed to cancel order {OrderId} for {Symbol}: {Error}", orderId, symbol, webCallResult.Error?.Message);
					return Result.Fail<bool>("Failed to cancel order");
				}
				return Result.Ok(value: true);
			}
			catch (Exception exception)
			{
				_logger.LogError(exception, "Error canceling order {OrderId} for {Symbol}", orderId, symbol);
				return Result.Fail<bool>(new FluentResults.Error("Failed to cancel order").CausedBy(exception));
			}
		}

		public async Task<Result<string>> GetListenKeyAsync(CancellationToken ct = default(CancellationToken))
		{
			try
			{
				WebCallResult<string> webCallResult = await _restClient.SpotApi.Account.StartUserStreamAsync(ct);
				if (!webCallResult.Success || string.IsNullOrEmpty(webCallResult.Data))
				{
					_logger.LogError("Failed to get listenKey: {Error}", webCallResult.Error?.Message);
					return Result.Fail<string>("Failed to get listenKey");
				}
				return Result.Ok(webCallResult.Data);
			}
			catch (Exception exception)
			{
				_logger.LogError(exception, "Error getting listenKey");
				return Result.Fail<string>(new FluentResults.Error("Failed to get listenKey").CausedBy(exception));
			}
		}

		public async Task<Result<IEnumerable<MexcUserTrade>>> GetOrderTradesAsync(string symbol, string orderId, CancellationToken ct = default(CancellationToken))
		{
			WebCallResult<MexcUserTrade[]> webCallResult = await _restClient.SpotApi.Trading.GetUserTradesAsync(symbol, orderId, null, null, null, ct);
			if (!webCallResult.Success || webCallResult.Data == null)
			{
				return Result.Fail<IEnumerable<MexcUserTrade>>(webCallResult.Error?.Message ?? "Failed to get trades");
			}
			return Result.Ok(webCallResult.Data.AsEnumerable());
		}

		public async Task<Result<IEnumerable<MexcOrder>>> GetOrderHistoryAsync(string symbol, CancellationToken ct = default(CancellationToken))
		{
			_ = 1;
			try
			{
				long value = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
				string text = $"symbol={symbol}&timestamp={value}&limit=1000";
				string text2 = CreateSignature(text);
				string text3 = "https://www.mexc.com/api/v3/allOrders?" + text + "&signature=" + text2;
				using HttpClient httpClient = new HttpClient();
				httpClient.DefaultRequestHeaders.Add("X-MEXC-APIKEY", _options.ApiKey);
				httpClient.Timeout = TimeSpan.FromSeconds(30.0);
				_logger.LogInformation("[AUDIT] Запрашиваем историю ордеров: " + text3);
				HttpResponseMessage response = await httpClient.GetAsync(text3, ct);
				string text4 = await response.Content.ReadAsStringAsync(ct);
				if (!response.IsSuccessStatusCode)
				{
					_logger.LogError("Failed to get order history for {Symbol}: {StatusCode} - {Content}", symbol, response.StatusCode, text4);
					return Result.Fail<IEnumerable<MexcOrder>>($"Failed to get order history: {response.StatusCode} - {text4}");
				}
				_logger.LogInformation($"[AUDIT] Получен ответ истории ордеров: {text4.Length} символов");
				return Result.Ok(JsonSerializer.Deserialize<List<MexcOrder>>(text4, new JsonSerializerOptions
				{
					PropertyNameCaseInsensitive = true
				})?.AsEnumerable() ?? Enumerable.Empty<MexcOrder>());
			}
			catch (Exception exception)
			{
				_logger.LogError(exception, "Error getting order history for {Symbol}", symbol);
				return Result.Ok(Enumerable.Empty<MexcOrder>());
			}
		}

		private string CreateSignature(string queryString)
		{
			using HMACSHA256 hMACSHA = new HMACSHA256(Encoding.UTF8.GetBytes(_options.ApiSecret));
			return Convert.ToHexString(hMACSHA.ComputeHash(Encoding.UTF8.GetBytes(queryString))).ToLower();
		}

		public async Task<decimal> GetTickSizeAsync(string symbol, CancellationToken ct = default(CancellationToken))
		{
			WebCallResult<MexcExchangeInfo> webCallResult = await _restClient.SpotApi.ExchangeData.GetExchangeInfoAsync(new string[1] { symbol }, ct);
			if (webCallResult.Success && webCallResult.Data != null)
			{
				MexcSymbol mexcSymbol = webCallResult.Data.Symbols.FirstOrDefault((MexcSymbol s) => s.Name == symbol);
				if (mexcSymbol != null)
				{
					_logger.LogInformation("[SELL DEBUG] MexcSymbol: " + JsonSerializer.Serialize(mexcSymbol));
				}
			}
			return 0.000001m;
		}

		public async Task<Result<(decimal Maker, decimal Taker)>> GetTradeFeeAsync(string symbol, CancellationToken ct = default(CancellationToken))
		{
			try
			{
				WebCallResult<MexcTradeFee> webCallResult = await _restClient.SpotApi.Account.GetTradeFeeAsync(symbol, ct);
				if (!webCallResult.Success || webCallResult.Data == null)
				{
					_logger.LogError("Failed to get trade fee for {Symbol}: {Error}", symbol, webCallResult.Error?.Message);
					return Result.Fail<(decimal, decimal)>("Failed to get trade fee");
				}
				return Result.Ok((webCallResult.Data.MakerFee, webCallResult.Data.TakerFee));
			}
			catch (Exception exception)
			{
				_logger.LogError(exception, "Error getting trade fee for {Symbol}", symbol);
				return Result.Fail<(decimal, decimal)>(new FluentResults.Error("Failed to get trade fee").CausedBy(exception));
			}
		}
	}
	public static class NotificationFormatter
	{
		public static string Profit(decimal qty, decimal price, decimal usdt, decimal profit)
		{
			return $"<b>✅ ПРОДАНО</b>\n{qty:F2} KAS по {price:F6} USDT\n\n" + "<b>\ud83d\udcb0 Получено</b>\n" + $"{usdt:F8} USDT\n\n" + "<b>\ud83d\udcc8 ПРИБЫЛЬ</b>\n" + $"{profit:F8} USDT";
		}

		public static string AutoBuy(decimal buyQty, decimal buyPrice, decimal sellQty, decimal sellPrice, decimal? lastBuyPrice = null, decimal? currentPrice = null, bool isStartup = false)
		{
			string value2;
			if (lastBuyPrice.HasValue && currentPrice.HasValue)
			{
				decimal? num = lastBuyPrice;
				if ((buyPrice < num.GetValueOrDefault()) & num.HasValue)
				{
					decimal value = 100m * (lastBuyPrice.Value - buyPrice) / lastBuyPrice.Value;
					value2 = $"<b>Цена упала на {value:F2}%: {lastBuyPrice:F6} → {buyPrice:F6} USDT</b>";
					goto IL_00e2;
				}
			}
			value2 = ((!isStartup || lastBuyPrice.HasValue) ? "<b>Автопокупка совершена</b>" : "<b>\ud83d\ude80 Старт автоторговли</b>");
			goto IL_00e2;
			IL_00e2:
			return $"{value2}\n\n✅ <b>КУПЛЕНО</b>\n\ud83d\udcca <b>{buyQty:F2} KAS</b> по <b>{buyPrice:F6} USDT</b>\n\n\ud83d\udcb0 <b>Потрачено:</b> <b>{buyQty * buyPrice:F8} USDT</b>\n\n\ud83d\udcc8 <b>ВЫСТАВЛЕНО</b>\n\ud83d\udcca <b>{sellQty:F2} KAS</b> по <b>{sellPrice:F6} USDT</b>";
		}

		public static string StatTable(IEnumerable<(int Index, decimal Qty, decimal Price, decimal Sum, decimal Deviation)> rows, decimal totalSum, decimal currentPrice, string autotradeStatus, string autoBuyInfo, int totalCount)
		{
			StringBuilder stringBuilder = new StringBuilder();
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder3 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(7, 1, stringBuilder2);
			handler.AppendLiteral("<b>");
			handler.AppendFormatted(autotradeStatus);
			handler.AppendLiteral("</b>");
			stringBuilder3.AppendLine(ref handler);
			stringBuilder.AppendLine("<b>\ud83d\ude80 Ордера на продажу</b>");
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder4 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(36, 1, stringBuilder2);
			handler.AppendLiteral("\ud83d\udcca <b>Общее количество ордеров:</b> ");
			handler.AppendFormatted(totalCount);
			stringBuilder4.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder5 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(36, 1, stringBuilder2);
			handler.AppendLiteral("\ud83d\udcb0 <b>Общая сумма всех ордеров:</b> ");
			handler.AppendFormatted(totalSum, "F2");
			stringBuilder5.AppendLine(ref handler);
			stringBuilder.AppendLine();
			stringBuilder.AppendLine("<pre>");
			stringBuilder.AppendLine(" # | Кол-во |  Цена  | Сумма | Отклон");
			stringBuilder.AppendLine("---|--------|--------|-------|--------");
			foreach (var row in rows)
			{
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder6 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(13, 5, stringBuilder2);
				handler.AppendFormatted(row.Index, 2);
				handler.AppendLiteral(" | ");
				handler.AppendFormatted(row.Qty, 6, "F2");
				handler.AppendLiteral(" | ");
				handler.AppendFormatted(row.Price, 6, "F4");
				handler.AppendLiteral(" | ");
				handler.AppendFormatted(row.Sum, 5, "F2");
				handler.AppendLiteral(" | ");
				handler.AppendFormatted(row.Deviation, 5, "F2");
				handler.AppendLiteral("%");
				stringBuilder6.AppendLine(ref handler);
			}
			stringBuilder.AppendLine("</pre>");
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder7 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(25, 2, stringBuilder2);
			handler.AppendLiteral("\n\ud83d\udcb5 <b>Текущая цена:</b> ");
			handler.AppendFormatted(currentPrice, "F4");
			handler.AppendFormatted(autoBuyInfo);
			stringBuilder7.AppendLine(ref handler);
			return stringBuilder.ToString();
		}

		public static string ProfitTable(IEnumerable<(string Date, decimal Profit, int Count)> rows, decimal weekProfit, int weekCount, decimal allProfit, int allCount)
		{
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("<b>\ud83d\udcc8 Полный профит</b>\n");
			stringBuilder.AppendLine("<pre>");
			string[] source = new string[3] { "Дата", "За неделю", "За всё время" };
			int num = (rows.Any() ? Math.Max(rows.Max<(string, decimal, int)>(((string Date, decimal Profit, int Count) r) => r.Date.Length), source.Max((string h) => h.Length)) : source.Max((string h) => h.Length));
			string format = $"{{0,-{num}}}";
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder3 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(16, 1, stringBuilder2);
			handler.AppendFormatted(string.Format(format, "Дата"));
			handler.AppendLiteral(" | Профит | Сдел");
			stringBuilder3.AppendLine(ref handler);
			stringBuilder.AppendLine(new string('-', num) + "|--------|------");
			foreach (var row in rows)
			{
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder4 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(6, 3, stringBuilder2);
				handler.AppendFormatted(string.Format(format, row.Date));
				handler.AppendLiteral(" | ");
				handler.AppendFormatted(row.Profit, 6, "F2");
				handler.AppendLiteral(" | ");
				handler.AppendFormatted(row.Count, 4);
				stringBuilder4.AppendLine(ref handler);
			}
			stringBuilder.AppendLine(new string('-', num) + "|--------|------");
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder5 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(6, 3, stringBuilder2);
			handler.AppendFormatted(string.Format(format, "За неделю"));
			handler.AppendLiteral(" | ");
			handler.AppendFormatted(weekProfit, 6, "F2");
			handler.AppendLiteral(" | ");
			handler.AppendFormatted(weekCount, 4);
			stringBuilder5.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder6 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(6, 3, stringBuilder2);
			handler.AppendFormatted(string.Format(format, "За всё время"));
			handler.AppendLiteral(" | ");
			handler.AppendFormatted(allProfit, 6, "F2");
			handler.AppendLiteral(" | ");
			handler.AppendFormatted(allCount, 4);
			stringBuilder6.AppendLine(ref handler);
			stringBuilder.AppendLine("</pre>");
			return stringBuilder.ToString();
		}

		public static string BalanceTable(IEnumerable<(string Asset, decimal Total, decimal Available, decimal Frozen, decimal? UsdtValue)> rows, decimal totalUsdt)
		{
			StringBuilder stringBuilder = new StringBuilder();
			StringBuilder stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler;
			foreach (var row in rows)
			{
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder3 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(15, 1, stringBuilder2);
				handler.AppendLiteral("<b>Баланс ");
				handler.AppendFormatted(row.Asset);
				handler.AppendLiteral(":</b>");
				stringBuilder3.AppendLine(ref handler);
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder4 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(42, 3, stringBuilder2);
				handler.AppendLiteral("Total: <b>");
				handler.AppendFormatted(row.Total, "F2");
				handler.AppendLiteral("</b> Free:<b>");
				handler.AppendFormatted(row.Available, "F2");
				handler.AppendLiteral("</b> Locked:<b>");
				handler.AppendFormatted(row.Frozen, "F2");
				handler.AppendLiteral("</b>");
				stringBuilder4.AppendLine(ref handler);
				stringBuilder.AppendLine();
			}
			stringBuilder.AppendLine("================================================");
			stringBuilder.AppendLine("<b>Всего активов USDT по текущей цене KAS=0.096872:</b>");
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder5 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(42, 3, stringBuilder2);
			handler.AppendLiteral("Total: <b>");
			handler.AppendFormatted(totalUsdt, "F2");
			handler.AppendLiteral("</b> Free:<b>");
			handler.AppendFormatted(rows.Where<(string, decimal, decimal, decimal, decimal?)>(((string Asset, decimal Total, decimal Available, decimal Frozen, decimal? UsdtValue) r) => r.Asset == "USDT").Sum<(string, decimal, decimal, decimal, decimal?)>(((string Asset, decimal Total, decimal Available, decimal Frozen, decimal? UsdtValue) r) => r.Available), "F2");
			handler.AppendLiteral("</b> Locked:<b>");
			handler.AppendFormatted(rows.Where<(string, decimal, decimal, decimal, decimal?)>(((string Asset, decimal Total, decimal Available, decimal Frozen, decimal? UsdtValue) r) => r.Asset == "USDT").Sum<(string, decimal, decimal, decimal, decimal?)>(((string Asset, decimal Total, decimal Available, decimal Frozen, decimal? UsdtValue) r) => r.Frozen), "F2");
			handler.AppendLiteral("</b>");
			stringBuilder5.AppendLine(ref handler);
			return stringBuilder.ToString();
		}
	}
	public class OrderAuditEvent
	{
		public long UserId { get; set; }

		public string OrderId { get; set; } = string.Empty;

		public string Symbol { get; set; } = string.Empty;

		public OrderSide Side { get; set; }

		public decimal Qty { get; set; }

		public decimal Price { get; set; }

		public OrderStatus Status { get; set; }
	}
	public class OrderAuditService : BackgroundService
	{
		private readonly IServiceProvider _serviceProvider;

		private readonly ILogger<OrderAuditService> _logger;

		private readonly BlockingCollection<OrderAuditEvent> _queue = new BlockingCollection<OrderAuditEvent>();

		private readonly bool _enabled;

		public OrderAuditService(IServiceProvider serviceProvider, ILogger<OrderAuditService> logger)
		{
			_serviceProvider = serviceProvider;
			_logger = logger;
			_enabled = serviceProvider.GetService<IConfiguration>()?.GetSection("Audit")?.GetValue<bool>("Enabled") == true;
			if (_enabled)
			{
				_logger.LogInformation("[AUDIT] OrderAuditService enabled");
			}
		}

		public void Enqueue(OrderAuditEvent evt)
		{
			if (_enabled)
			{
				_queue.Add(evt);
			}
		}

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			if (!_enabled)
			{
				return;
			}
			while (!stoppingToken.IsCancellationRequested)
			{
				OrderAuditEvent evt = null;
				try
				{
					if (_queue.TryTake(out evt, 1000, stoppingToken) && evt != null)
					{
						Task.Run(() => AuditOrder(evt), stoppingToken);
					}
				}
				catch (OperationCanceledException)
				{
					break;
				}
				await Task.Delay(100, stoppingToken);
			}
		}

		private async Task AuditOrder(OrderAuditEvent evt)
		{
			_ = 4;
			try
			{
				_logger.LogInformation($"[AUDIT] Начинаем аудит orderId={evt.OrderId} user={evt.UserId}");
				using IServiceScope scope = _serviceProvider.CreateScope();
				OrderPairRepository orderRepo = scope.ServiceProvider.GetRequiredService<OrderPairRepository>();
				IUserRepository requiredService = scope.ServiceProvider.GetRequiredService<IUserRepository>();
				ILoggerFactory requiredService2 = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
				ILogger<MexcService> mexcLogger = requiredService2.CreateLogger<MexcService>();
				User user = await requiredService.GetByIdAsync(evt.UserId);
				if (user == null)
				{
					_logger.LogWarning($"[AUDIT] Пользователь {evt.UserId} не найден в базе");
					return;
				}
				KaspaBot.Infrastructure.Options.MexcOptions options = new KaspaBot.Infrastructure.Options.MexcOptions
				{
					ApiKey = user.ApiCredentials.ApiKey,
					ApiSecret = user.ApiCredentials.ApiSecret
				};
				MexcService mexc = new MexcService(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, mexcLogger, Microsoft.Extensions.Options.Options.Create(options));
				_logger.LogInformation("[AUDIT] WS EVENT: " + JsonSerializer.Serialize(evt));
				_logger.LogInformation("[AUDIT] Запрашиваем REST GetOrderAsync для orderId=" + evt.OrderId);
				Result<MexcOrder> restOrder = await mexc.GetOrderAsync(evt.Symbol, evt.OrderId, CancellationToken.None);
				if (!restOrder.IsSuccess)
				{
					_logger.LogWarning("[AUDIT] REST GetOrderAsync failed для orderId=" + evt.OrderId + ": " + string.Join(", ", restOrder.Errors.Select((IError e) => e.Message)));
				}
				else
				{
					_logger.LogInformation("[AUDIT] REST ORDER: " + JsonSerializer.Serialize(restOrder.Value));
				}
				_logger.LogInformation("[AUDIT] Запрашиваем REST GetOrderTradesAsync для orderId=" + evt.OrderId);
				Result<IEnumerable<MexcUserTrade>> restTrades = await mexc.GetOrderTradesAsync(evt.Symbol, evt.OrderId, CancellationToken.None);
				if (!restTrades.IsSuccess)
				{
					_logger.LogWarning("[AUDIT] REST GetOrderTradesAsync failed для orderId=" + evt.OrderId + ": " + string.Join(", ", restTrades.Errors.Select((IError e) => e.Message)));
				}
				else
				{
					_logger.LogInformation("[AUDIT] REST TRADES: " + JsonSerializer.Serialize(restTrades.Value));
				}
				_logger.LogInformation("[AUDIT] Запрашиваем REST GetOrderHistoryAsync для symbol=" + evt.Symbol);
				_logger.LogInformation("[AUDIT] Ищем ордер в базе данных orderId=" + evt.OrderId);
				Order dbOrder = null;
				int retryCount = 3;
				for (int i = 0; i < retryCount; i++)
				{
					dbOrder = await orderRepo.FindOrderByIdAsync(evt.OrderId);
					if (dbOrder != null)
					{
						break;
					}
					await Task.Delay(1000);
				}
				if (dbOrder == null)
				{
					_logger.LogWarning($"[AUDIT-ERR] Ордер {evt.OrderId} не найден в базе данных после {retryCount} попыток");
					return;
				}
				_logger.LogInformation("[AUDIT] DB ORDER: " + JsonSerializer.Serialize(dbOrder));
				if (!restOrder.IsSuccess)
				{
					return;
				}
				MexcOrder value = restOrder.Value;
				if (dbOrder.Status != value.Status)
				{
					_logger.LogWarning($"[AUDIT-ERR] Статус ордера {evt.OrderId} не совпадает: БД={dbOrder.Status} Биржа={value.Status}");
				}
				if (Math.Abs(dbOrder.QuantityFilled - value.QuantityFilled) > 0.0001m)
				{
					_logger.LogWarning($"[AUDIT-ERR] Количество исполнено {evt.OrderId} не совпадает: БД={dbOrder.QuantityFilled} Биржа={value.QuantityFilled}");
				}
				if (!dbOrder.Price.HasValue)
				{
					return;
				}
				decimal? value2;
				if (value.OrderType == OrderType.Market && value.QuantityFilled > 0m && value.QuoteQuantityFilled > 0m)
				{
					value2 = value.QuoteQuantityFilled / value.QuantityFilled;
					if (!restTrades.IsSuccess || !restTrades.Value.Any())
					{
						return;
					}
					List<MexcUserTrade> source = restTrades.Value.ToList();
					decimal num = source.Sum((MexcUserTrade t) => t.QuoteQuantity);
					decimal num2 = source.Sum((MexcUserTrade t) => t.Quantity);
					if (num2 > 0m)
					{
						decimal num3 = num / num2;
						if (Math.Abs(num3 - value2.Value) > 0.0001m)
						{
							_logger.LogError($"[AUDIT-ERR] Расчетная цена MARKET ордера {evt.OrderId} не совпадает: REST={value2:F6} Трейды={num3:F6}");
						}
						if (Math.Abs(dbOrder.Price.Value - num3) > 0.0001m)
						{
							_logger.LogError($"[AUDIT-ERR] Цена MARKET ордера {evt.OrderId} не совпадает: БД={dbOrder.Price:F6} Трейды={num3:F6}");
						}
						else
						{
							_logger.LogInformation($"[AUDIT-OK] Цена MARKET ордера {evt.OrderId} совпадает: БД={dbOrder.Price:F6} Трейды={num3:F6}");
						}
					}
					return;
				}
				value2 = value.Price;
				if (Math.Abs(dbOrder.Price.Value - value2.Value) > 0.0001m)
				{
					_logger.LogWarning($"[AUDIT-ERR] Цена LIMIT ордера {evt.OrderId} не совпадает: БД={dbOrder.Price:F6} Биржа={value2:F6}");
				}
				else
				{
					_logger.LogInformation($"[AUDIT-OK] Цена LIMIT ордера {evt.OrderId} совпадает: БД={dbOrder.Price:F6} Биржа={value2:F6}");
				}
			}
			catch (Exception exception)
			{
				_logger.LogError(exception, "[AUDIT-ERR] Ошибка аудита для orderId=" + evt.OrderId);
			}
		}
	}
	public class PriceStreamService : IPriceStreamService, IDisposable
	{
		private readonly IMexcSocketClient _socketClient;

		private readonly ILogger<PriceStreamService> _logger;

		private UpdateSubscription? _subscription;

		public PriceStreamService(ILogger<PriceStreamService> logger)
		{
			_socketClient = new MexcSocketClient();
			_logger = logger;
		}

		public async Task StartStreamAsync(string symbol, Action<decimal> onPriceUpdate)
		{
			try
			{
				_logger.LogInformation("Subscribing to price updates for {Symbol}...", symbol);
				CallResult<UpdateSubscription> callResult = await _socketClient.SpotApi.SubscribeToMiniTickerUpdatesAsync(symbol, delegate(DataEvent<MexcStreamMiniTick> update)
				{
					onPriceUpdate(update.Data.LastPrice);
				}, CancellationToken.None.ToString());
				if (!callResult.Success)
				{
					_logger.LogError("Failed to subscribe to price updates: {Error}", callResult.Error);
					throw new Exception($"Subscription error: {callResult.Error}");
				}
				_subscription = callResult.Data;
				_logger.LogInformation("Successfully subscribed to price updates for {Symbol}", symbol);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error subscribing to price updates: {Message}", ex.Message);
				throw;
			}
		}

		public void Dispose()
		{
			try
			{
				if (_subscription != null)
				{
					_socketClient.UnsubscribeAsync(_subscription).GetAwaiter().GetResult();
				}
				_socketClient?.Dispose();
			}
			catch (Exception exception)
			{
				_logger.LogError(exception, "Error unsubscribing from price updates");
			}
			finally
			{
				GC.SuppressFinalize(this);
			}
		}
	}
	public class RateLimiter
	{
		private readonly ConcurrentDictionary<string, Queue<DateTime>> _requestHistory = new ConcurrentDictionary<string, Queue<DateTime>>();

		private readonly ILogger<RateLimiter> _logger;

		private readonly Timer _cleanupTimer;

		public RateLimiter(ILogger<RateLimiter> logger)
		{
			_logger = logger;
			_cleanupTimer = new Timer(CleanupOldRequests, null, TimeSpan.FromMinutes(1.0), TimeSpan.FromMinutes(1.0));
		}

		public bool IsAllowed(string key, int maxRequests, TimeSpan window)
		{
			DateTime utcNow = DateTime.UtcNow;
			Queue<DateTime> orAdd = _requestHistory.GetOrAdd(key, (string _) => new Queue<DateTime>());
			lock (orAdd)
			{
				while (orAdd.Count > 0 && utcNow - orAdd.Peek() > window)
				{
					orAdd.Dequeue();
				}
				if (orAdd.Count >= maxRequests)
				{
					_logger.LogWarning("Rate limit exceeded for key: {Key}, requests: {Count}/{Max}", key, orAdd.Count, maxRequests);
					return false;
				}
				orAdd.Enqueue(utcNow);
				return true;
			}
		}

		public async Task<bool> WaitForAllowanceAsync(string key, int maxRequests, TimeSpan window, TimeSpan timeout = default(TimeSpan), CancellationToken cancellationToken = default(CancellationToken))
		{
			if (timeout == default(TimeSpan))
			{
				timeout = TimeSpan.FromSeconds(30.0);
			}
			DateTime startTime = DateTime.UtcNow;
			while (DateTime.UtcNow - startTime < timeout)
			{
				if (IsAllowed(key, maxRequests, window))
				{
					return true;
				}
				await Task.Delay(100, cancellationToken);
			}
			_logger.LogError("Rate limit timeout exceeded for key: {Key}", key);
			return false;
		}

		public int GetCurrentRequests(string key)
		{
			if (_requestHistory.TryGetValue(key, out Queue<DateTime> value))
			{
				lock (value)
				{
					return value.Count;
				}
			}
			return 0;
		}

		private void CleanupOldRequests(object? state)
		{
			DateTime utcNow = DateTime.UtcNow;
			List<string> list = new List<string>();
			Queue<DateTime> value2;
			foreach (KeyValuePair<string, Queue<DateTime>> item in _requestHistory)
			{
				Queue<DateTime> value = item.Value;
				value2 = value;
				bool lockTaken = false;
				try
				{
					Monitor.Enter(value2, ref lockTaken);
					while (value.Count > 0 && utcNow - value.Peek() > TimeSpan.FromHours(1.0))
					{
						value.Dequeue();
					}
					if (value.Count == 0)
					{
						list.Add(item.Key);
					}
				}
				finally
				{
					if (lockTaken)
					{
						Monitor.Exit(value2);
					}
				}
			}
			foreach (string item2 in list)
			{
				_requestHistory.TryRemove(item2, out value2);
			}
		}

		public void Dispose()
		{
			_cleanupTimer?.Dispose();
		}
	}
	public class UserStreamManager
	{
		public delegate Task OrderSoldHandler(long userId, decimal qty, decimal price, decimal usdt, decimal profit);

		public delegate Task StatusChangeNotificationHandler(long userId, string orderId, string oldStatus, string newStatus, string reason);

		public delegate Task DebounceCompletedHandler(long userId);

		private readonly IUserRepository _userRepository;

		private readonly ILogger<UserStreamManager> _logger;

		private readonly ConcurrentDictionary<long, string> _listenKeys = new ConcurrentDictionary<long, string>();

		private readonly ConcurrentDictionary<long, IMexcService> _userMexcServices = new ConcurrentDictionary<long, IMexcService>();

		private readonly ILoggerFactory _loggerFactory;

		private readonly IServiceProvider _serviceProvider;

		private readonly IOrderRecoveryService _orderRecoveryService;

		private readonly OrderPairRepository _orderPairRepo;

		private readonly ConcurrentDictionary<long, CancellationTokenSource> _debounceCtsPerUser = new ConcurrentDictionary<long, CancellationTokenSource>();

		private readonly object _debounceLock = new object();

		private readonly IBotMessenger _botMessenger;

		public event OrderSoldHandler? OnOrderSold;

		public event StatusChangeNotificationHandler? OnStatusChangeNotification;

		public event DebounceCompletedHandler? OnDebounceCompleted;

		public UserStreamManager(IUserRepository userRepository, ILogger<UserStreamManager> logger, ILoggerFactory loggerFactory, OrderPairRepository orderPairRepo, IServiceProvider serviceProvider, IOrderRecoveryService orderRecoveryService, IBotMessenger botMessenger)
		{
			_userRepository = userRepository;
			_logger = logger;
			_loggerFactory = loggerFactory;
			_orderPairRepo = orderPairRepo;
			_serviceProvider = serviceProvider;
			_orderRecoveryService = orderRecoveryService;
			_botMessenger = botMessenger;
		}

		public async Task InitializeAllAsync(CancellationToken cancellationToken = default(CancellationToken))
		{
			foreach (User item in await _userRepository.GetAllAsync())
			{
				await InitializeUserAsync(item, cancellationToken);
			}
		}

		public async Task InitializeUserAsync(User user, CancellationToken cancellationToken = default(CancellationToken))
		{
			ILogger<MexcService> logger = _loggerFactory.CreateLogger<MexcService>();
			KaspaBot.Infrastructure.Options.MexcOptions options = new KaspaBot.Infrastructure.Options.MexcOptions
			{
				ApiKey = user.ApiCredentials.ApiKey,
				ApiSecret = user.ApiCredentials.ApiSecret
			};
			MexcService mexcService = new MexcService(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, logger, Microsoft.Extensions.Options.Options.Create(options));
			_userMexcServices[user.Id] = mexcService;
			Result<string> result = await mexcService.GetListenKeyAsync(cancellationToken);
			if (result.IsSuccess)
			{
				_listenKeys[user.Id] = result.Value;
				_logger.LogInformation($"ListenKey for user {user.Id} initialized");
				mexcService._socketClient.SpotApi.SubscribeToOrderUpdatesAsync(result.Value, async delegate(DataEvent<MexcUserOrderUpdate> orderUpdate)
				{
					try
					{
						_logger.LogInformation($"[WS DIAG] EVENT: user={user.Id} orderId={orderUpdate.Data.OrderId} side={orderUpdate.Data.Side} status={orderUpdate.Data.Status} type={orderUpdate.Data.OrderType} qty={orderUpdate.Data.Quantity} price={orderUpdate.Data.Price} CumulativeQty={orderUpdate.Data.CumulativeQuantity} CumulativeQuoteQty={orderUpdate.Data.CumulativeQuoteQuantity}");
						if (orderUpdate.Data.Side == OrderSide.Sell && orderUpdate.Data.Status == OrderStatus.Filled)
						{
							OrderPair pair = (await _orderPairRepo.GetAllAsync()).FirstOrDefault((OrderPair p) => p.UserId == user.Id && p.SellOrder.Id == orderUpdate.Data.OrderId.ToString());
							if (pair == null)
							{
								_logger.LogInformation("[WS DIAG] SELL исполнен, но ордер " + orderUpdate.Data.OrderId + " не найден в базе — игнорируем событие");
								return;
							}
							pair.SellOrder.Status = orderUpdate.Data.Status;
							pair.SellOrder.QuantityFilled = orderUpdate.Data.Quantity;
							pair.SellOrder.Price = orderUpdate.Data.Price;
							pair.SellOrder.UpdatedAt = DateTime.UtcNow;
							pair.CompletedAt = DateTime.UtcNow;
							pair.Profit = orderUpdate.Data.Quantity * orderUpdate.Data.Price - pair.BuyOrder.QuantityFilled * pair.BuyOrder.Price.GetValueOrDefault() - pair.BuyOrder.Commission;
							await _orderPairRepo.UpdateAsync(pair);
							decimal valueOrDefault = pair.Profit.GetValueOrDefault();
							if (this.OnOrderSold != null)
							{
								await this.OnOrderSold(user.Id, orderUpdate.Data.Quantity, orderUpdate.Data.Price, orderUpdate.Data.Quantity * orderUpdate.Data.Price, valueOrDefault);
							}
							lock (_debounceLock)
							{
								if (_debounceCtsPerUser.TryGetValue(user.Id, out CancellationTokenSource value))
								{
									value.Cancel();
									value.Dispose();
								}
								CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
								_debounceCtsPerUser[user.Id] = cancellationTokenSource;
								DebouncedAutoPair(user, cancellationTokenSource.Token);
							}
						}
						if (orderUpdate.Data.Side == OrderSide.Buy && orderUpdate.Data.Status == OrderStatus.Filled)
						{
							OrderPair pair = (await _orderPairRepo.GetAllAsync()).FirstOrDefault((OrderPair p) => p.UserId == user.Id && p.BuyOrder.Id == orderUpdate.Data.OrderId.ToString());
							if (pair != null)
							{
								decimal? avgPrice = null;
								decimal qty = orderUpdate.Data.Quantity;
								decimal? quoteQty = null;
								if (orderUpdate.Data.OrderType == OrderType.Market && orderUpdate.Data.CumulativeQuantity.HasValue && orderUpdate.Data.CumulativeQuoteQuantity.HasValue && orderUpdate.Data.CumulativeQuantity.Value > 0m)
								{
									avgPrice = orderUpdate.Data.CumulativeQuoteQuantity.Value / orderUpdate.Data.CumulativeQuantity.Value;
									qty = orderUpdate.Data.CumulativeQuantity.Value;
									quoteQty = orderUpdate.Data.CumulativeQuoteQuantity.Value;
									_logger.LogError($"[WS DIAG] [BUY WS] avgPrice={avgPrice} (CumulativeQuoteQty / CumulativeQty) — используется как основная цена покупки");
								}
								_logger.LogError($"[WS DIAG] [BUY WS] OrderId={orderUpdate.Data.OrderId} Qty={orderUpdate.Data.Quantity} Price={orderUpdate.Data.Price} CumulativeQty={orderUpdate.Data.CumulativeQuantity} CumulativeQuoteQty={orderUpdate.Data.CumulativeQuoteQuantity}");
								_logger.LogError("[WS DIAG] [BUY REST] Запрос к REST для OrderId=" + orderUpdate.Data.OrderId);
								ILogger<MexcService> logger2 = _loggerFactory.CreateLogger<MexcService>();
								KaspaBot.Infrastructure.Options.MexcOptions options2 = new KaspaBot.Infrastructure.Options.MexcOptions
								{
									ApiKey = user.ApiCredentials.ApiKey,
									ApiSecret = user.ApiCredentials.ApiSecret
								};
								Result<MexcOrder> result2 = await new MexcService(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, logger2, Microsoft.Extensions.Options.Options.Create(options2)).GetOrderAsync(pair.BuyOrder.Symbol, orderUpdate.Data.OrderId.ToString());
								string text = JsonSerializer.Serialize(result2);
								_logger.LogError("[WS DIAG] [BUY REST RAW] " + text);
								if (!result2.IsSuccess)
								{
									_logger.LogError("[WS DIAG] [BUY REST] GetOrderAsync failed: " + string.Join(", ", result2.Errors.Select((IError e) => e.Message)));
								}
								pair.BuyOrder.Status = orderUpdate.Data.Status;
								pair.BuyOrder.QuantityFilled = qty;
								pair.BuyOrder.Price = avgPrice;
								pair.BuyOrder.QuoteQuantityFilled = quoteQty.GetValueOrDefault();
								pair.BuyOrder.UpdatedAt = DateTime.UtcNow;
								await _orderPairRepo.UpdateAsync(pair);
							}
						}
					}
					catch (Exception exception)
					{
						_logger.LogError(exception, $"Ошибка в обработчике обновления ордера для user {user.Id}");
					}
				});
			}
			else
			{
				_logger.LogError($"Failed to initialize listenKey for user {user.Id}: {result.Errors.FirstOrDefault()?.Message}");
			}
		}

		public async Task ReloadUserAsync(User user, CancellationToken cancellationToken = default(CancellationToken))
		{
			ILogger<MexcService> logger = _loggerFactory.CreateLogger<MexcService>();
			KaspaBot.Infrastructure.Options.MexcOptions options = new KaspaBot.Infrastructure.Options.MexcOptions
			{
				ApiKey = user.ApiCredentials.ApiKey,
				ApiSecret = user.ApiCredentials.ApiSecret
			};
			MexcService mexcService = new MexcService(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, logger, Microsoft.Extensions.Options.Options.Create(options));
			_userMexcServices[user.Id] = mexcService;
			Result<string> result = await mexcService.GetListenKeyAsync(cancellationToken);
			if (result.IsSuccess)
			{
				_listenKeys[user.Id] = result.Value;
				_logger.LogInformation($"ListenKey for user {user.Id} reloaded");
			}
			else
			{
				_logger.LogError($"Failed to reload listenKey for user {user.Id}: {result.Errors.FirstOrDefault()?.Message}");
			}
		}

		public string? GetListenKey(long userId)
		{
			if (!_listenKeys.TryGetValue(userId, out string value))
			{
				return null;
			}
			return value;
		}

		public IServiceProvider GetServiceProvider()
		{
			return _serviceProvider;
		}

		private async Task DebouncedAutoPair(User user, CancellationToken token)
		{
			_ = 2;
			try
			{
				await Task.Delay(TimeSpan.FromSeconds(30.0), token);
				await CreateAutoPairForUser(user);
				await _orderRecoveryService.RunRecoveryForUser(user.Id, CancellationToken.None);
			}
			catch (TaskCanceledException)
			{
			}
		}

		private async Task CreateAutoPairForUser(User user)
		{
			decimal orderAmount = user.Settings.OrderAmount;
			if (orderAmount <= 0m)
			{
				return;
			}
			ILogger<MexcService> logger = _loggerFactory.CreateLogger<MexcService>();
			KaspaBot.Infrastructure.Options.MexcOptions options = new KaspaBot.Infrastructure.Options.MexcOptions
			{
				ApiKey = user.ApiCredentials.ApiKey,
				ApiSecret = user.ApiCredentials.ApiSecret
			};
			MexcService mexcService = new MexcService(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, logger, Microsoft.Extensions.Options.Options.Create(options));
			Result<string> result = await mexcService.PlaceOrderAsync("KASUSDT", OrderSide.Buy, OrderType.Market, orderAmount);
			if (!result.IsSuccess)
			{
				return;
			}
			string buyOrderId = result.Value;
			Result<MexcOrder> result2 = await mexcService.GetOrderAsync("KASUSDT", buyOrderId);
			decimal buyPrice = default(decimal);
			decimal totalCommission = default(decimal);
			if (result2.IsSuccess && result2.Value.QuantityFilled > 0m && result2.Value.QuoteQuantityFilled > 0m)
			{
				buyPrice = result2.Value.QuoteQuantityFilled / result2.Value.QuantityFilled;
			}
			else if (result2.IsSuccess)
			{
				buyPrice = result2.Value.Price;
			}
			decimal buyQty = (result2.IsSuccess ? result2.Value.QuantityFilled : 0m);
			Result<IEnumerable<MexcUserTrade>> result3 = await mexcService.GetOrderTradesAsync("KASUSDT", buyOrderId);
			if (result3.IsSuccess)
			{
				totalCommission = result3.Value.Sum((MexcUserTrade t) => t.Fee);
			}
			string id = Guid.NewGuid().ToString();
			Order buyOrder = new Order
			{
				Id = buyOrderId,
				Symbol = "KASUSDT",
				Side = OrderSide.Buy,
				Type = OrderType.Market,
				Quantity = orderAmount,
				Status = OrderStatus.Filled,
				CreatedAt = DateTime.UtcNow,
				UpdatedAt = DateTime.UtcNow,
				Price = buyPrice,
				QuantityFilled = buyQty,
				Commission = totalCommission
			};
			decimal num = user.Settings.PercentProfit / 100m;
			decimal sellPrice = buyPrice * (1m + num);
			Order sellOrder = new Order
			{
				Id = string.Empty,
				Symbol = "KASUSDT",
				Side = OrderSide.Sell,
				Type = OrderType.Limit,
				Quantity = buyQty,
				Price = sellPrice,
				Status = OrderStatus.New,
				CreatedAt = DateTime.UtcNow
			};
			OrderPair orderPair = new OrderPair
			{
				Id = id,
				UserId = user.Id,
				BuyOrder = buyOrder,
				SellOrder = sellOrder,
				CreatedAt = DateTime.UtcNow
			};
			await _orderPairRepo.AddAsync(orderPair);
			Result<string> result4 = await mexcService.PlaceOrderAsync("KASUSDT", OrderSide.Sell, OrderType.Limit, buyQty, sellPrice);
			if (result4.IsSuccess)
			{
				orderPair.SellOrder.Id = result4.Value;
				orderPair.SellOrder.Status = OrderStatus.New;
				orderPair.SellOrder.CreatedAt = DateTime.UtcNow;
				await _orderPairRepo.UpdateAsync(orderPair);
				string text = $"КУПЛЕНО\n\n{buyQty:F2} KAS по {buyPrice:F6} USDT\n\nПотрачено\n{buyQty * buyPrice:F8} USDT\n\nВЫСТАВЛЕНО\n\n{buyQty:F2} KAS по {sellPrice:F6} USDT";
				await _botMessenger.SendMessage(user.Id, text);
			}
		}
	}
}
namespace KaspaBot.Infrastructure.Repositories
{
	public class OrderPairRepository
	{
		private readonly ApplicationDbContext _context;

		private readonly ILogger<OrderPairRepository> _logger;

		public OrderPairRepository(ApplicationDbContext context, ILogger<OrderPairRepository> logger)
		{
			_context = context;
			_logger = logger;
		}

		public async Task AddAsync(OrderPair pair)
		{
			await _context.OrderPairs.AddAsync(pair);
			await _context.SaveChangesAsync();
		}

		public async Task UpdateAsync(OrderPair pair)
		{
			_context.OrderPairs.Update(pair);
			await _context.SaveChangesAsync();
		}

		public async Task<OrderPair?> GetByIdAsync(string id)
		{
			return await _context.OrderPairs.FindAsync(id);
		}

		public async Task<List<OrderPair>> GetOpenByUserIdAsync(long userId)
		{
			return await _context.OrderPairs.Where((OrderPair p) => p.UserId == userId && p.CompletedAt == null).ToListAsync();
		}

		public async Task<List<OrderPair>> GetAllAsync()
		{
			return await _context.OrderPairs.ToListAsync();
		}

		public async Task DeleteByIdAsync(string id)
		{
			OrderPair orderPair = await _context.OrderPairs.FindAsync(id);
			if (orderPair != null)
			{
				_context.OrderPairs.Remove(orderPair);
				await _context.SaveChangesAsync();
			}
		}

		public async Task DeleteByUserId(long userId)
		{
			List<OrderPair> list = await _context.OrderPairs.Where((OrderPair p) => p.UserId == userId).ToListAsync();
			_logger.LogInformation($"[ORDERPAIR-DELETE] userId={userId} найдено пар: {list.Count}");
			_context.OrderPairs.RemoveRange(list);
			int value = await _context.SaveChangesAsync();
			_logger.LogInformation($"[ORDERPAIR-DELETE] userId={userId} удалено записей: {value}");
		}

		public async Task<Order?> FindOrderByIdAsync(string orderId)
		{
			OrderPair orderPair = await _context.OrderPairs.FirstOrDefaultAsync((OrderPair p) => p.BuyOrder.Id == orderId || p.SellOrder.Id == orderId);
			if (orderPair == null)
			{
				return null;
			}
			if (orderPair.BuyOrder.Id == orderId)
			{
				return orderPair.BuyOrder;
			}
			if (orderPair.SellOrder.Id == orderId)
			{
				return orderPair.SellOrder;
			}
			return null;
		}

		public async Task<(OrderPair, Order)?> FindOrderAndPairByOrderIdAsync(string orderId)
		{
			OrderPair orderPair = await _context.OrderPairs.FirstOrDefaultAsync((OrderPair p) => p.BuyOrder.Id == orderId || p.SellOrder.Id == orderId);
			if (orderPair == null)
			{
				return null;
			}
			if (orderPair.BuyOrder.Id == orderId)
			{
				return (orderPair, orderPair.BuyOrder);
			}
			if (orderPair.SellOrder.Id == orderId)
			{
				return (orderPair, orderPair.SellOrder);
			}
			return null;
		}
	}
	public class UserRepository : IUserRepository
	{
		private readonly ApplicationDbContext _context;

		private readonly EncryptionService _encryptionService;

		public UserRepository(ApplicationDbContext context, EncryptionService encryptionService)
		{
			_context = context;
			_encryptionService = encryptionService;
		}

		public async Task<User?> GetByIdAsync(long userId)
		{
			User user = await _context.Users.FindAsync(userId);
			if (user != null)
			{
				user.ApiCredentials.ApiKey = _encryptionService.Decrypt(user.ApiCredentials.ApiKey);
				user.ApiCredentials.ApiSecret = _encryptionService.Decrypt(user.ApiCredentials.ApiSecret);
			}
			return user;
		}

		public async Task AddAsync(User user)
		{
			if (!_encryptionService.IsEncrypted(user.ApiCredentials.ApiKey))
			{
				user.ApiCredentials.ApiKey = _encryptionService.Encrypt(user.ApiCredentials.ApiKey);
			}
			if (!_encryptionService.IsEncrypted(user.ApiCredentials.ApiSecret))
			{
				user.ApiCredentials.ApiSecret = _encryptionService.Encrypt(user.ApiCredentials.ApiSecret);
			}
			await _context.Users.AddAsync(user);
			await _context.SaveChangesAsync();
			user.ApiCredentials.ApiKey = _encryptionService.Decrypt(user.ApiCredentials.ApiKey);
			user.ApiCredentials.ApiSecret = _encryptionService.Decrypt(user.ApiCredentials.ApiSecret);
		}

		public async Task UpdateAsync(User user)
		{
			if (!_encryptionService.IsEncrypted(user.ApiCredentials.ApiKey))
			{
				user.ApiCredentials.ApiKey = _encryptionService.Encrypt(user.ApiCredentials.ApiKey);
			}
			if (!_encryptionService.IsEncrypted(user.ApiCredentials.ApiSecret))
			{
				user.ApiCredentials.ApiSecret = _encryptionService.Encrypt(user.ApiCredentials.ApiSecret);
			}
			_context.Users.Update(user);
			await _context.SaveChangesAsync();
			user.ApiCredentials.ApiKey = _encryptionService.Decrypt(user.ApiCredentials.ApiKey);
			user.ApiCredentials.ApiSecret = _encryptionService.Decrypt(user.ApiCredentials.ApiSecret);
		}

		public async Task<List<User>> GetAllAsync()
		{
			List<User> list = await _context.Users.ToListAsync();
			foreach (User item in list)
			{
				item.ApiCredentials.ApiKey = _encryptionService.Decrypt(item.ApiCredentials.ApiKey);
				item.ApiCredentials.ApiSecret = _encryptionService.Decrypt(item.ApiCredentials.ApiSecret);
			}
			return list;
		}

		public async Task<bool> ExistsAsync(long userId)
		{
			return await _context.Users.AnyAsync((User u) => u.Id == userId);
		}

		public async Task DeleteAsync(long userId)
		{
			User user = await _context.Users.FindAsync(userId);
			if (user != null)
			{
				_context.Users.Remove(user);
				await _context.SaveChangesAsync();
			}
		}
	}
}
namespace KaspaBot.Infrastructure.Persistence
{
	public class ApplicationDbContext : DbContext
	{
		public DbSet<User> Users { get; set; }

		public DbSet<OrderPair> OrderPairs { get; set; }

		public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
			: base(options)
		{
		}

		protected override void OnModelCreating(ModelBuilder modelBuilder)
		{
			modelBuilder.Entity(delegate(EntityTypeBuilder<User> entity)
			{
				entity.HasKey((User u) => u.Id);
				entity.OwnsOne((User u) => u.Settings, delegate(OwnedNavigationBuilder<User, UserSettings> settings)
				{
					settings.Property((UserSettings s) => s.EnableAutoTrading).HasColumnName("Settings_EnableAutoTrading");
				});
				entity.OwnsOne((User u) => u.ApiCredentials);
			});
			modelBuilder.Entity(delegate(EntityTypeBuilder<OrderPair> entity)
			{
				entity.HasKey((OrderPair p) => p.Id);
				entity.Property((OrderPair p) => p.UserId);
				entity.OwnsOne((OrderPair p) => p.BuyOrder);
				entity.OwnsOne((OrderPair p) => p.SellOrder);
			});
		}
	}
	public class ApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext(string[] args)
		{
			DbContextOptionsBuilder<ApplicationDbContext> dbContextOptionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
			dbContextOptionsBuilder.UseSqlite("Data Source=KaspaBot.db");
			return new ApplicationDbContext(dbContextOptionsBuilder.Options);
		}
	}
}
namespace KaspaBot.Infrastructure.Options
{
	public class MexcOptions
	{
		public const string SectionName = "Mexc";

		public required string ApiKey { get; set; }

		public required string ApiSecret { get; set; }
	}
}
namespace KaspaBot.Infrastructure.Migrations
{
	[DbContext(typeof(ApplicationDbContext))]
	[Migration("20250724165920_InitialCreate")]
	public class InitialCreate : Migration
	{
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.CreateTable("OrderPairs", (ColumnsBuilder table) => new
			{
				Id = table.Column<string>("TEXT"),
				BuyOrder_Id = table.Column<string>("TEXT"),
				BuyOrder_Symbol = table.Column<string>("TEXT"),
				BuyOrder_Side = table.Column<int>("INTEGER"),
				BuyOrder_Type = table.Column<int>("INTEGER"),
				BuyOrder_Quantity = table.Column<decimal>("TEXT"),
				BuyOrder_Price = table.Column<decimal>("TEXT", null, null, rowVersion: false, null, nullable: true),
				BuyOrder_Status = table.Column<int>("INTEGER"),
				BuyOrder_CreatedAt = table.Column<DateTime>("TEXT"),
				BuyOrder_UpdatedAt = table.Column<DateTime>("TEXT", null, null, rowVersion: false, null, nullable: true),
				BuyOrder_QuantityFilled = table.Column<decimal>("TEXT"),
				BuyOrder_QuoteQuantityFilled = table.Column<decimal>("TEXT"),
				BuyOrder_Commission = table.Column<decimal>("TEXT"),
				SellOrder_Id = table.Column<string>("TEXT"),
				SellOrder_Symbol = table.Column<string>("TEXT"),
				SellOrder_Side = table.Column<int>("INTEGER"),
				SellOrder_Type = table.Column<int>("INTEGER"),
				SellOrder_Quantity = table.Column<decimal>("TEXT"),
				SellOrder_Price = table.Column<decimal>("TEXT", null, null, rowVersion: false, null, nullable: true),
				SellOrder_Status = table.Column<int>("INTEGER"),
				SellOrder_CreatedAt = table.Column<DateTime>("TEXT"),
				SellOrder_UpdatedAt = table.Column<DateTime>("TEXT", null, null, rowVersion: false, null, nullable: true),
				SellOrder_QuantityFilled = table.Column<decimal>("TEXT"),
				SellOrder_QuoteQuantityFilled = table.Column<decimal>("TEXT"),
				SellOrder_Commission = table.Column<decimal>("TEXT"),
				CreatedAt = table.Column<DateTime>("TEXT"),
				CompletedAt = table.Column<DateTime>("TEXT", null, null, rowVersion: false, null, nullable: true),
				Profit = table.Column<decimal>("TEXT", null, null, rowVersion: false, null, nullable: true),
				UserId = table.Column<long>("INTEGER")
			}, null, table =>
			{
				table.PrimaryKey("PK_OrderPairs", x => x.Id);
			});
			migrationBuilder.CreateTable("Users", (ColumnsBuilder table) => new
			{
				Id = table.Column<long>("INTEGER").Annotation("Sqlite:Autoincrement", true),
				Username = table.Column<string>("TEXT"),
				RegistrationDate = table.Column<DateTime>("TEXT"),
				Settings_PercentPriceChange = table.Column<decimal>("TEXT"),
				Settings_PercentProfit = table.Column<decimal>("TEXT"),
				Settings_MaxUsdtUsing = table.Column<decimal>("TEXT"),
				Settings_OrderAmount = table.Column<decimal>("TEXT"),
				Settings_EnableAutoTrading = table.Column<bool>("INTEGER"),
				ApiCredentials_ApiKey = table.Column<string>("TEXT"),
				ApiCredentials_ApiSecret = table.Column<string>("TEXT"),
				IsActive = table.Column<bool>("INTEGER")
			}, null, table =>
			{
				table.PrimaryKey("PK_Users", x => x.Id);
			});
		}

		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropTable("OrderPairs");
			migrationBuilder.DropTable("Users");
		}

		protected override void BuildTargetModel(ModelBuilder modelBuilder)
		{
			modelBuilder.HasAnnotation("ProductVersion", "8.0.6");
			modelBuilder.Entity("KaspaBot.Domain.Entities.OrderPair", delegate(EntityTypeBuilder b)
			{
				b.Property<string>("Id").HasColumnType("TEXT");
				b.Property<DateTime?>("CompletedAt").HasColumnType("TEXT");
				b.Property<DateTime>("CreatedAt").HasColumnType("TEXT");
				b.Property<decimal?>("Profit").HasColumnType("TEXT");
				b.Property<long>("UserId").HasColumnType("INTEGER");
				b.HasKey("Id");
				b.ToTable("OrderPairs");
			});
			modelBuilder.Entity("KaspaBot.Domain.Entities.User", delegate(EntityTypeBuilder b)
			{
				b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");
				b.Property<bool>("IsActive").HasColumnType("INTEGER");
				b.Property<DateTime>("RegistrationDate").HasColumnType("TEXT");
				b.Property<string>("Username").IsRequired().HasColumnType("TEXT");
				b.HasKey("Id");
				b.ToTable("Users");
			});
			modelBuilder.Entity("KaspaBot.Domain.Entities.OrderPair", delegate(EntityTypeBuilder b)
			{
				b.OwnsOne("KaspaBot.Domain.Entities.Order", "BuyOrder", delegate(OwnedNavigationBuilder ownedNavigationBuilder)
				{
					ownedNavigationBuilder.Property<string>("OrderPairId").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("Commission").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<DateTime>("CreatedAt").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<string>("Id").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal?>("Price").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("Quantity").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("QuantityFilled").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("QuoteQuantityFilled").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<int>("Side").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<int>("Status").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<string>("Symbol").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.Property<int>("Type").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<DateTime?>("UpdatedAt").HasColumnType("TEXT");
					ownedNavigationBuilder.HasKey("OrderPairId");
					ownedNavigationBuilder.ToTable("OrderPairs");
					ownedNavigationBuilder.WithOwner().HasForeignKey("OrderPairId");
				});
				b.OwnsOne("KaspaBot.Domain.Entities.Order", "SellOrder", delegate(OwnedNavigationBuilder ownedNavigationBuilder)
				{
					ownedNavigationBuilder.Property<string>("OrderPairId").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("Commission").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<DateTime>("CreatedAt").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<string>("Id").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal?>("Price").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("Quantity").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("QuantityFilled").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("QuoteQuantityFilled").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<int>("Side").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<int>("Status").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<string>("Symbol").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.Property<int>("Type").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<DateTime?>("UpdatedAt").HasColumnType("TEXT");
					ownedNavigationBuilder.HasKey("OrderPairId");
					ownedNavigationBuilder.ToTable("OrderPairs");
					ownedNavigationBuilder.WithOwner().HasForeignKey("OrderPairId");
				});
				b.Navigation("BuyOrder").IsRequired();
				b.Navigation("SellOrder").IsRequired();
			});
			modelBuilder.Entity("KaspaBot.Domain.Entities.User", delegate(EntityTypeBuilder b)
			{
				b.OwnsOne("KaspaBot.Domain.ValueObjects.UserApiCredentials", "ApiCredentials", delegate(OwnedNavigationBuilder ownedNavigationBuilder)
				{
					ownedNavigationBuilder.Property<long>("UserId").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<string>("ApiKey").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.Property<string>("ApiSecret").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.HasKey("UserId");
					ownedNavigationBuilder.ToTable("Users");
					ownedNavigationBuilder.WithOwner().HasForeignKey("UserId");
				});
				b.OwnsOne("KaspaBot.Domain.ValueObjects.UserSettings", "Settings", delegate(OwnedNavigationBuilder ownedNavigationBuilder)
				{
					ownedNavigationBuilder.Property<long>("UserId").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<bool>("EnableAutoTrading").HasColumnType("INTEGER").HasColumnName("Settings_EnableAutoTrading");
					ownedNavigationBuilder.Property<decimal>("MaxUsdtUsing").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("OrderAmount").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("PercentPriceChange").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("PercentProfit").HasColumnType("TEXT");
					ownedNavigationBuilder.HasKey("UserId");
					ownedNavigationBuilder.ToTable("Users");
					ownedNavigationBuilder.WithOwner().HasForeignKey("UserId");
				});
				b.Navigation("ApiCredentials").IsRequired();
				b.Navigation("Settings").IsRequired();
			});
		}
	}
	[DbContext(typeof(ApplicationDbContext))]
	[Migration("20250724205406_AddAutoTradeFieldsToUserSettings")]
	public class AddAutoTradeFieldsToUserSettings : Migration
	{
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.AddColumn<bool>("Settings_IsAutoTradeEnabled", "Users", "INTEGER", null, null, rowVersion: false, null, nullable: false, false);
			migrationBuilder.AddColumn<decimal>("Settings_LastDcaBuyPrice", "Users", "TEXT", null, null, rowVersion: false, null, nullable: true);
		}

		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropColumn("Settings_IsAutoTradeEnabled", "Users");
			migrationBuilder.DropColumn("Settings_LastDcaBuyPrice", "Users");
		}

		protected override void BuildTargetModel(ModelBuilder modelBuilder)
		{
			modelBuilder.HasAnnotation("ProductVersion", "8.0.6");
			modelBuilder.Entity("KaspaBot.Domain.Entities.OrderPair", delegate(EntityTypeBuilder b)
			{
				b.Property<string>("Id").HasColumnType("TEXT");
				b.Property<DateTime?>("CompletedAt").HasColumnType("TEXT");
				b.Property<DateTime>("CreatedAt").HasColumnType("TEXT");
				b.Property<decimal?>("Profit").HasColumnType("TEXT");
				b.Property<long>("UserId").HasColumnType("INTEGER");
				b.HasKey("Id");
				b.ToTable("OrderPairs");
			});
			modelBuilder.Entity("KaspaBot.Domain.Entities.User", delegate(EntityTypeBuilder b)
			{
				b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");
				b.Property<bool>("IsActive").HasColumnType("INTEGER");
				b.Property<DateTime>("RegistrationDate").HasColumnType("TEXT");
				b.Property<string>("Username").IsRequired().HasColumnType("TEXT");
				b.HasKey("Id");
				b.ToTable("Users");
			});
			modelBuilder.Entity("KaspaBot.Domain.Entities.OrderPair", delegate(EntityTypeBuilder b)
			{
				b.OwnsOne("KaspaBot.Domain.Entities.Order", "BuyOrder", delegate(OwnedNavigationBuilder ownedNavigationBuilder)
				{
					ownedNavigationBuilder.Property<string>("OrderPairId").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("Commission").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<DateTime>("CreatedAt").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<string>("Id").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal?>("Price").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("Quantity").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("QuantityFilled").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("QuoteQuantityFilled").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<int>("Side").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<int>("Status").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<string>("Symbol").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.Property<int>("Type").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<DateTime?>("UpdatedAt").HasColumnType("TEXT");
					ownedNavigationBuilder.HasKey("OrderPairId");
					ownedNavigationBuilder.ToTable("OrderPairs");
					ownedNavigationBuilder.WithOwner().HasForeignKey("OrderPairId");
				});
				b.OwnsOne("KaspaBot.Domain.Entities.Order", "SellOrder", delegate(OwnedNavigationBuilder ownedNavigationBuilder)
				{
					ownedNavigationBuilder.Property<string>("OrderPairId").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("Commission").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<DateTime>("CreatedAt").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<string>("Id").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal?>("Price").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("Quantity").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("QuantityFilled").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("QuoteQuantityFilled").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<int>("Side").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<int>("Status").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<string>("Symbol").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.Property<int>("Type").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<DateTime?>("UpdatedAt").HasColumnType("TEXT");
					ownedNavigationBuilder.HasKey("OrderPairId");
					ownedNavigationBuilder.ToTable("OrderPairs");
					ownedNavigationBuilder.WithOwner().HasForeignKey("OrderPairId");
				});
				b.Navigation("BuyOrder").IsRequired();
				b.Navigation("SellOrder").IsRequired();
			});
			modelBuilder.Entity("KaspaBot.Domain.Entities.User", delegate(EntityTypeBuilder b)
			{
				b.OwnsOne("KaspaBot.Domain.ValueObjects.UserApiCredentials", "ApiCredentials", delegate(OwnedNavigationBuilder ownedNavigationBuilder)
				{
					ownedNavigationBuilder.Property<long>("UserId").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<string>("ApiKey").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.Property<string>("ApiSecret").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.HasKey("UserId");
					ownedNavigationBuilder.ToTable("Users");
					ownedNavigationBuilder.WithOwner().HasForeignKey("UserId");
				});
				b.OwnsOne("KaspaBot.Domain.ValueObjects.UserSettings", "Settings", delegate(OwnedNavigationBuilder ownedNavigationBuilder)
				{
					ownedNavigationBuilder.Property<long>("UserId").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<bool>("EnableAutoTrading").HasColumnType("INTEGER").HasColumnName("Settings_EnableAutoTrading");
					ownedNavigationBuilder.Property<bool>("IsAutoTradeEnabled").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<decimal?>("LastDcaBuyPrice").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("MaxUsdtUsing").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("OrderAmount").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("PercentPriceChange").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("PercentProfit").HasColumnType("TEXT");
					ownedNavigationBuilder.HasKey("UserId");
					ownedNavigationBuilder.ToTable("Users");
					ownedNavigationBuilder.WithOwner().HasForeignKey("UserId");
				});
				b.Navigation("ApiCredentials").IsRequired();
				b.Navigation("Settings").IsRequired();
			});
		}
	}
	[DbContext(typeof(ApplicationDbContext))]
	[Migration("20250724205846_AddIsAutoTradeEnabledToUserSettings")]
	public class AddIsAutoTradeEnabledToUserSettings : Migration
	{
		protected override void Up(MigrationBuilder migrationBuilder)
		{
		}

		protected override void Down(MigrationBuilder migrationBuilder)
		{
		}

		protected override void BuildTargetModel(ModelBuilder modelBuilder)
		{
			modelBuilder.HasAnnotation("ProductVersion", "8.0.6");
			modelBuilder.Entity("KaspaBot.Domain.Entities.OrderPair", delegate(EntityTypeBuilder b)
			{
				b.Property<string>("Id").HasColumnType("TEXT");
				b.Property<DateTime?>("CompletedAt").HasColumnType("TEXT");
				b.Property<DateTime>("CreatedAt").HasColumnType("TEXT");
				b.Property<decimal?>("Profit").HasColumnType("TEXT");
				b.Property<long>("UserId").HasColumnType("INTEGER");
				b.HasKey("Id");
				b.ToTable("OrderPairs");
			});
			modelBuilder.Entity("KaspaBot.Domain.Entities.User", delegate(EntityTypeBuilder b)
			{
				b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");
				b.Property<bool>("IsActive").HasColumnType("INTEGER");
				b.Property<DateTime>("RegistrationDate").HasColumnType("TEXT");
				b.Property<string>("Username").IsRequired().HasColumnType("TEXT");
				b.HasKey("Id");
				b.ToTable("Users");
			});
			modelBuilder.Entity("KaspaBot.Domain.Entities.OrderPair", delegate(EntityTypeBuilder b)
			{
				b.OwnsOne("KaspaBot.Domain.Entities.Order", "BuyOrder", delegate(OwnedNavigationBuilder ownedNavigationBuilder)
				{
					ownedNavigationBuilder.Property<string>("OrderPairId").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("Commission").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<DateTime>("CreatedAt").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<string>("Id").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal?>("Price").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("Quantity").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("QuantityFilled").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("QuoteQuantityFilled").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<int>("Side").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<int>("Status").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<string>("Symbol").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.Property<int>("Type").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<DateTime?>("UpdatedAt").HasColumnType("TEXT");
					ownedNavigationBuilder.HasKey("OrderPairId");
					ownedNavigationBuilder.ToTable("OrderPairs");
					ownedNavigationBuilder.WithOwner().HasForeignKey("OrderPairId");
				});
				b.OwnsOne("KaspaBot.Domain.Entities.Order", "SellOrder", delegate(OwnedNavigationBuilder ownedNavigationBuilder)
				{
					ownedNavigationBuilder.Property<string>("OrderPairId").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("Commission").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<DateTime>("CreatedAt").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<string>("Id").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal?>("Price").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("Quantity").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("QuantityFilled").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("QuoteQuantityFilled").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<int>("Side").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<int>("Status").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<string>("Symbol").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.Property<int>("Type").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<DateTime?>("UpdatedAt").HasColumnType("TEXT");
					ownedNavigationBuilder.HasKey("OrderPairId");
					ownedNavigationBuilder.ToTable("OrderPairs");
					ownedNavigationBuilder.WithOwner().HasForeignKey("OrderPairId");
				});
				b.Navigation("BuyOrder").IsRequired();
				b.Navigation("SellOrder").IsRequired();
			});
			modelBuilder.Entity("KaspaBot.Domain.Entities.User", delegate(EntityTypeBuilder b)
			{
				b.OwnsOne("KaspaBot.Domain.ValueObjects.UserApiCredentials", "ApiCredentials", delegate(OwnedNavigationBuilder ownedNavigationBuilder)
				{
					ownedNavigationBuilder.Property<long>("UserId").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<string>("ApiKey").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.Property<string>("ApiSecret").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.HasKey("UserId");
					ownedNavigationBuilder.ToTable("Users");
					ownedNavigationBuilder.WithOwner().HasForeignKey("UserId");
				});
				b.OwnsOne("KaspaBot.Domain.ValueObjects.UserSettings", "Settings", delegate(OwnedNavigationBuilder ownedNavigationBuilder)
				{
					ownedNavigationBuilder.Property<long>("UserId").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<bool>("EnableAutoTrading").HasColumnType("INTEGER").HasColumnName("Settings_EnableAutoTrading");
					ownedNavigationBuilder.Property<bool>("IsAutoTradeEnabled").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<decimal?>("LastDcaBuyPrice").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("MaxUsdtUsing").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("OrderAmount").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("PercentPriceChange").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("PercentProfit").HasColumnType("TEXT");
					ownedNavigationBuilder.HasKey("UserId");
					ownedNavigationBuilder.ToTable("Users");
					ownedNavigationBuilder.WithOwner().HasForeignKey("UserId");
				});
				b.Navigation("ApiCredentials").IsRequired();
				b.Navigation("Settings").IsRequired();
			});
		}
	}
	[DbContext(typeof(ApplicationDbContext))]
	[Migration("20250726085610_AddUserSettingsStateFields")]
	public class AddUserSettingsStateFields : Migration
	{
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.AddColumn<int>("Settings_ConsecutiveFailures", "Users", "INTEGER", null, null, rowVersion: false, null, nullable: false, 0);
			migrationBuilder.AddColumn<DateTime>("Settings_DebounceStartTime", "Users", "TEXT", null, null, rowVersion: false, null, nullable: true);
			migrationBuilder.AddColumn<bool>("Settings_IsInDebounce", "Users", "INTEGER", null, null, rowVersion: false, null, nullable: false, false);
			migrationBuilder.AddColumn<DateTime>("Settings_LastBalanceCheck", "Users", "TEXT", null, null, rowVersion: false, null, nullable: true);
			migrationBuilder.AddColumn<decimal>("Settings_LastKnownBalance", "Users", "TEXT", null, null, rowVersion: false, null, nullable: true);
			migrationBuilder.AddColumn<DateTime>("Settings_LastTradeTime", "Users", "TEXT", null, null, rowVersion: false, null, nullable: true);
		}

		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropColumn("Settings_ConsecutiveFailures", "Users");
			migrationBuilder.DropColumn("Settings_DebounceStartTime", "Users");
			migrationBuilder.DropColumn("Settings_IsInDebounce", "Users");
			migrationBuilder.DropColumn("Settings_LastBalanceCheck", "Users");
			migrationBuilder.DropColumn("Settings_LastKnownBalance", "Users");
			migrationBuilder.DropColumn("Settings_LastTradeTime", "Users");
		}

		protected override void BuildTargetModel(ModelBuilder modelBuilder)
		{
			modelBuilder.HasAnnotation("ProductVersion", "8.0.6");
			modelBuilder.Entity("KaspaBot.Domain.Entities.OrderPair", delegate(EntityTypeBuilder b)
			{
				b.Property<string>("Id").HasColumnType("TEXT");
				b.Property<DateTime?>("CompletedAt").HasColumnType("TEXT");
				b.Property<DateTime>("CreatedAt").HasColumnType("TEXT");
				b.Property<decimal?>("Profit").HasColumnType("TEXT");
				b.Property<long>("UserId").HasColumnType("INTEGER");
				b.HasKey("Id");
				b.ToTable("OrderPairs");
			});
			modelBuilder.Entity("KaspaBot.Domain.Entities.User", delegate(EntityTypeBuilder b)
			{
				b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");
				b.Property<bool>("IsActive").HasColumnType("INTEGER");
				b.Property<DateTime>("RegistrationDate").HasColumnType("TEXT");
				b.Property<string>("Username").IsRequired().HasColumnType("TEXT");
				b.HasKey("Id");
				b.ToTable("Users");
			});
			modelBuilder.Entity("KaspaBot.Domain.Entities.OrderPair", delegate(EntityTypeBuilder b)
			{
				b.OwnsOne("KaspaBot.Domain.Entities.Order", "BuyOrder", delegate(OwnedNavigationBuilder ownedNavigationBuilder)
				{
					ownedNavigationBuilder.Property<string>("OrderPairId").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("Commission").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<DateTime>("CreatedAt").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<string>("Id").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal?>("Price").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("Quantity").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("QuantityFilled").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("QuoteQuantityFilled").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<int>("Side").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<int>("Status").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<string>("Symbol").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.Property<int>("Type").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<DateTime?>("UpdatedAt").HasColumnType("TEXT");
					ownedNavigationBuilder.HasKey("OrderPairId");
					ownedNavigationBuilder.ToTable("OrderPairs");
					ownedNavigationBuilder.WithOwner().HasForeignKey("OrderPairId");
				});
				b.OwnsOne("KaspaBot.Domain.Entities.Order", "SellOrder", delegate(OwnedNavigationBuilder ownedNavigationBuilder)
				{
					ownedNavigationBuilder.Property<string>("OrderPairId").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("Commission").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<DateTime>("CreatedAt").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<string>("Id").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal?>("Price").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("Quantity").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("QuantityFilled").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("QuoteQuantityFilled").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<int>("Side").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<int>("Status").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<string>("Symbol").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.Property<int>("Type").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<DateTime?>("UpdatedAt").HasColumnType("TEXT");
					ownedNavigationBuilder.HasKey("OrderPairId");
					ownedNavigationBuilder.ToTable("OrderPairs");
					ownedNavigationBuilder.WithOwner().HasForeignKey("OrderPairId");
				});
				b.Navigation("BuyOrder").IsRequired();
				b.Navigation("SellOrder").IsRequired();
			});
			modelBuilder.Entity("KaspaBot.Domain.Entities.User", delegate(EntityTypeBuilder b)
			{
				b.OwnsOne("KaspaBot.Domain.ValueObjects.UserApiCredentials", "ApiCredentials", delegate(OwnedNavigationBuilder ownedNavigationBuilder)
				{
					ownedNavigationBuilder.Property<long>("UserId").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<string>("ApiKey").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.Property<string>("ApiSecret").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.HasKey("UserId");
					ownedNavigationBuilder.ToTable("Users");
					ownedNavigationBuilder.WithOwner().HasForeignKey("UserId");
				});
				b.OwnsOne("KaspaBot.Domain.ValueObjects.UserSettings", "Settings", delegate(OwnedNavigationBuilder ownedNavigationBuilder)
				{
					ownedNavigationBuilder.Property<long>("UserId").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<int>("ConsecutiveFailures").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<DateTime?>("DebounceStartTime").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<bool>("EnableAutoTrading").HasColumnType("INTEGER").HasColumnName("Settings_EnableAutoTrading");
					ownedNavigationBuilder.Property<bool>("IsAutoTradeEnabled").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<bool>("IsInDebounce").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<DateTime?>("LastBalanceCheck").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal?>("LastDcaBuyPrice").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal?>("LastKnownBalance").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<DateTime?>("LastTradeTime").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("MaxUsdtUsing").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("OrderAmount").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("PercentPriceChange").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("PercentProfit").HasColumnType("TEXT");
					ownedNavigationBuilder.HasKey("UserId");
					ownedNavigationBuilder.ToTable("Users");
					ownedNavigationBuilder.WithOwner().HasForeignKey("UserId");
				});
				b.Navigation("ApiCredentials").IsRequired();
				b.Navigation("Settings").IsRequired();
			});
		}
	}
	[DbContext(typeof(ApplicationDbContext))]
	[Migration("20250726090539_AddOrderAmountModeAndDynamicCoef")]
	public class AddOrderAmountModeAndDynamicCoef : Migration
	{
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.AddColumn<decimal>("Settings_DynamicOrderCoef", "Users", "TEXT", null, null, rowVersion: false, null, nullable: false, 0m);
			migrationBuilder.AddColumn<int>("Settings_OrderAmountMode", "Users", "INTEGER", null, null, rowVersion: false, null, nullable: false, 0);
		}

		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropColumn("Settings_DynamicOrderCoef", "Users");
			migrationBuilder.DropColumn("Settings_OrderAmountMode", "Users");
		}

		protected override void BuildTargetModel(ModelBuilder modelBuilder)
		{
			modelBuilder.HasAnnotation("ProductVersion", "8.0.6");
			modelBuilder.Entity("KaspaBot.Domain.Entities.OrderPair", delegate(EntityTypeBuilder b)
			{
				b.Property<string>("Id").HasColumnType("TEXT");
				b.Property<DateTime?>("CompletedAt").HasColumnType("TEXT");
				b.Property<DateTime>("CreatedAt").HasColumnType("TEXT");
				b.Property<decimal?>("Profit").HasColumnType("TEXT");
				b.Property<long>("UserId").HasColumnType("INTEGER");
				b.HasKey("Id");
				b.ToTable("OrderPairs");
			});
			modelBuilder.Entity("KaspaBot.Domain.Entities.User", delegate(EntityTypeBuilder b)
			{
				b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");
				b.Property<bool>("IsActive").HasColumnType("INTEGER");
				b.Property<DateTime>("RegistrationDate").HasColumnType("TEXT");
				b.Property<string>("Username").IsRequired().HasColumnType("TEXT");
				b.HasKey("Id");
				b.ToTable("Users");
			});
			modelBuilder.Entity("KaspaBot.Domain.Entities.OrderPair", delegate(EntityTypeBuilder b)
			{
				b.OwnsOne("KaspaBot.Domain.Entities.Order", "BuyOrder", delegate(OwnedNavigationBuilder ownedNavigationBuilder)
				{
					ownedNavigationBuilder.Property<string>("OrderPairId").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("Commission").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<DateTime>("CreatedAt").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<string>("Id").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal?>("Price").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("Quantity").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("QuantityFilled").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("QuoteQuantityFilled").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<int>("Side").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<int>("Status").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<string>("Symbol").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.Property<int>("Type").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<DateTime?>("UpdatedAt").HasColumnType("TEXT");
					ownedNavigationBuilder.HasKey("OrderPairId");
					ownedNavigationBuilder.ToTable("OrderPairs");
					ownedNavigationBuilder.WithOwner().HasForeignKey("OrderPairId");
				});
				b.OwnsOne("KaspaBot.Domain.Entities.Order", "SellOrder", delegate(OwnedNavigationBuilder ownedNavigationBuilder)
				{
					ownedNavigationBuilder.Property<string>("OrderPairId").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("Commission").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<DateTime>("CreatedAt").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<string>("Id").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal?>("Price").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("Quantity").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("QuantityFilled").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("QuoteQuantityFilled").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<int>("Side").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<int>("Status").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<string>("Symbol").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.Property<int>("Type").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<DateTime?>("UpdatedAt").HasColumnType("TEXT");
					ownedNavigationBuilder.HasKey("OrderPairId");
					ownedNavigationBuilder.ToTable("OrderPairs");
					ownedNavigationBuilder.WithOwner().HasForeignKey("OrderPairId");
				});
				b.Navigation("BuyOrder").IsRequired();
				b.Navigation("SellOrder").IsRequired();
			});
			modelBuilder.Entity("KaspaBot.Domain.Entities.User", delegate(EntityTypeBuilder b)
			{
				b.OwnsOne("KaspaBot.Domain.ValueObjects.UserApiCredentials", "ApiCredentials", delegate(OwnedNavigationBuilder ownedNavigationBuilder)
				{
					ownedNavigationBuilder.Property<long>("UserId").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<string>("ApiKey").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.Property<string>("ApiSecret").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.HasKey("UserId");
					ownedNavigationBuilder.ToTable("Users");
					ownedNavigationBuilder.WithOwner().HasForeignKey("UserId");
				});
				b.OwnsOne("KaspaBot.Domain.ValueObjects.UserSettings", "Settings", delegate(OwnedNavigationBuilder ownedNavigationBuilder)
				{
					ownedNavigationBuilder.Property<long>("UserId").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<int>("ConsecutiveFailures").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<DateTime?>("DebounceStartTime").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("DynamicOrderCoef").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<bool>("EnableAutoTrading").HasColumnType("INTEGER").HasColumnName("Settings_EnableAutoTrading");
					ownedNavigationBuilder.Property<bool>("IsAutoTradeEnabled").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<bool>("IsInDebounce").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<DateTime?>("LastBalanceCheck").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal?>("LastDcaBuyPrice").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal?>("LastKnownBalance").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<DateTime?>("LastTradeTime").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("MaxUsdtUsing").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("OrderAmount").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<int>("OrderAmountMode").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<decimal>("PercentPriceChange").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("PercentProfit").HasColumnType("TEXT");
					ownedNavigationBuilder.HasKey("UserId");
					ownedNavigationBuilder.ToTable("Users");
					ownedNavigationBuilder.WithOwner().HasForeignKey("UserId");
				});
				b.Navigation("ApiCredentials").IsRequired();
				b.Navigation("Settings").IsRequired();
			});
		}
	}
	[DbContext(typeof(ApplicationDbContext))]
	internal class ApplicationDbContextModelSnapshot : ModelSnapshot
	{
		protected override void BuildModel(ModelBuilder modelBuilder)
		{
			modelBuilder.HasAnnotation("ProductVersion", "8.0.6");
			modelBuilder.Entity("KaspaBot.Domain.Entities.OrderPair", delegate(EntityTypeBuilder b)
			{
				b.Property<string>("Id").HasColumnType("TEXT");
				b.Property<DateTime?>("CompletedAt").HasColumnType("TEXT");
				b.Property<DateTime>("CreatedAt").HasColumnType("TEXT");
				b.Property<decimal?>("Profit").HasColumnType("TEXT");
				b.Property<long>("UserId").HasColumnType("INTEGER");
				b.HasKey("Id");
				b.ToTable("OrderPairs");
			});
			modelBuilder.Entity("KaspaBot.Domain.Entities.User", delegate(EntityTypeBuilder b)
			{
				b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");
				b.Property<bool>("IsActive").HasColumnType("INTEGER");
				b.Property<DateTime>("RegistrationDate").HasColumnType("TEXT");
				b.Property<string>("Username").IsRequired().HasColumnType("TEXT");
				b.HasKey("Id");
				b.ToTable("Users");
			});
			modelBuilder.Entity("KaspaBot.Domain.Entities.OrderPair", delegate(EntityTypeBuilder b)
			{
				b.OwnsOne("KaspaBot.Domain.Entities.Order", "BuyOrder", delegate(OwnedNavigationBuilder ownedNavigationBuilder)
				{
					ownedNavigationBuilder.Property<string>("OrderPairId").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("Commission").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<DateTime>("CreatedAt").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<string>("Id").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal?>("Price").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("Quantity").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("QuantityFilled").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("QuoteQuantityFilled").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<int>("Side").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<int>("Status").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<string>("Symbol").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.Property<int>("Type").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<DateTime?>("UpdatedAt").HasColumnType("TEXT");
					ownedNavigationBuilder.HasKey("OrderPairId");
					ownedNavigationBuilder.ToTable("OrderPairs");
					ownedNavigationBuilder.WithOwner().HasForeignKey("OrderPairId");
				});
				b.OwnsOne("KaspaBot.Domain.Entities.Order", "SellOrder", delegate(OwnedNavigationBuilder ownedNavigationBuilder)
				{
					ownedNavigationBuilder.Property<string>("OrderPairId").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("Commission").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<DateTime>("CreatedAt").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<string>("Id").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal?>("Price").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("Quantity").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("QuantityFilled").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("QuoteQuantityFilled").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<int>("Side").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<int>("Status").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<string>("Symbol").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.Property<int>("Type").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<DateTime?>("UpdatedAt").HasColumnType("TEXT");
					ownedNavigationBuilder.HasKey("OrderPairId");
					ownedNavigationBuilder.ToTable("OrderPairs");
					ownedNavigationBuilder.WithOwner().HasForeignKey("OrderPairId");
				});
				b.Navigation("BuyOrder").IsRequired();
				b.Navigation("SellOrder").IsRequired();
			});
			modelBuilder.Entity("KaspaBot.Domain.Entities.User", delegate(EntityTypeBuilder b)
			{
				b.OwnsOne("KaspaBot.Domain.ValueObjects.UserApiCredentials", "ApiCredentials", delegate(OwnedNavigationBuilder ownedNavigationBuilder)
				{
					ownedNavigationBuilder.Property<long>("UserId").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<string>("ApiKey").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.Property<string>("ApiSecret").IsRequired().HasColumnType("TEXT");
					ownedNavigationBuilder.HasKey("UserId");
					ownedNavigationBuilder.ToTable("Users");
					ownedNavigationBuilder.WithOwner().HasForeignKey("UserId");
				});
				b.OwnsOne("KaspaBot.Domain.ValueObjects.UserSettings", "Settings", delegate(OwnedNavigationBuilder ownedNavigationBuilder)
				{
					ownedNavigationBuilder.Property<long>("UserId").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<int>("ConsecutiveFailures").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<DateTime?>("DebounceStartTime").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("DynamicOrderCoef").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<bool>("EnableAutoTrading").HasColumnType("INTEGER").HasColumnName("Settings_EnableAutoTrading");
					ownedNavigationBuilder.Property<bool>("IsAutoTradeEnabled").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<bool>("IsInDebounce").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<DateTime?>("LastBalanceCheck").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal?>("LastDcaBuyPrice").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal?>("LastKnownBalance").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<DateTime?>("LastTradeTime").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("MaxUsdtUsing").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("OrderAmount").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<int>("OrderAmountMode").HasColumnType("INTEGER");
					ownedNavigationBuilder.Property<decimal>("PercentPriceChange").HasColumnType("TEXT");
					ownedNavigationBuilder.Property<decimal>("PercentProfit").HasColumnType("TEXT");
					ownedNavigationBuilder.HasKey("UserId");
					ownedNavigationBuilder.ToTable("Users");
					ownedNavigationBuilder.WithOwner().HasForeignKey("UserId");
				});
				b.Navigation("ApiCredentials").IsRequired();
				b.Navigation("Settings").IsRequired();
			});
		}
	}
}
namespace KaspaBot.Infrastructure.Extensions
{
	public static class InfrastructureExtensions
	{
		public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
		{
			services.Configure<KaspaBot.Infrastructure.Options.MexcOptions>(configuration.GetSection("Mexc"));
			services.AddSingleton((Func<IServiceProvider, IMexcRestClient>)delegate
			{
				KaspaBot.Infrastructure.Options.MexcOptions mexcOptions = configuration.GetSection("Mexc").Get<KaspaBot.Infrastructure.Options.MexcOptions>();
				if (mexcOptions == null || string.IsNullOrEmpty(mexcOptions.ApiKey) || string.IsNullOrEmpty(mexcOptions.ApiSecret))
				{
					throw new InvalidOperationException("Mexc API credentials are not configured properly");
				}
				MexcRestClient mexcRestClient = new MexcRestClient();
				mexcRestClient.SetApiCredentials(new ApiCredentials(mexcOptions.ApiKey, mexcOptions.ApiSecret));
				return mexcRestClient;
			});
			services.AddScoped((Func<IServiceProvider, IMexcService>)delegate(IServiceProvider provider)
			{
				IOptions<KaspaBot.Infrastructure.Options.MexcOptions> requiredService = provider.GetRequiredService<IOptions<KaspaBot.Infrastructure.Options.MexcOptions>>();
				ILogger<MexcService> requiredService2 = provider.GetRequiredService<ILogger<MexcService>>();
				return new MexcService(requiredService.Value.ApiKey, requiredService.Value.ApiSecret, requiredService2, requiredService);
			});
			services.AddScoped<IPriceStreamService, PriceStreamService>();
			services.AddScoped<IUserRepository, UserRepository>();
			services.AddScoped<OrderPairRepository>();
			services.AddSingleton<EncryptionService>();
			services.AddSingleton<CacheService>();
			services.AddSingleton<MetricsService>();
			services.AddSingleton<RateLimiter>();
			services.AddSingleton(delegate(IServiceProvider provider)
			{
				IUserRepository requiredService = provider.GetRequiredService<IUserRepository>();
				ILogger<UserStreamManager> requiredService2 = provider.GetRequiredService<ILogger<UserStreamManager>>();
				ILoggerFactory requiredService3 = provider.GetRequiredService<ILoggerFactory>();
				OrderPairRepository requiredService4 = provider.GetRequiredService<OrderPairRepository>();
				IOrderRecoveryService requiredService5 = provider.GetRequiredService<IOrderRecoveryService>();
				IBotMessenger requiredService6 = provider.GetRequiredService<IBotMessenger>();
				return new UserStreamManager(requiredService, requiredService2, requiredService3, requiredService4, provider, requiredService5, requiredService6);
			});
			services.AddMediatR(typeof(InfrastructureExtensions).Assembly);
			services.AddDbContext<ApplicationDbContext>(delegate(DbContextOptionsBuilder options)
			{
				options.UseSqlite(configuration.GetConnectionString("DefaultConnection"));
			});
			services.AddSingleton<OrderAuditService>();
			if (configuration.GetSection("Audit").GetValue<bool>("Enabled"))
			{
				services.AddHostedService((IServiceProvider provider) => provider.GetRequiredService<OrderAuditService>());
			}
			return services;
		}
	}
}
