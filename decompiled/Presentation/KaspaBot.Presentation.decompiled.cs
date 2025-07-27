using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentResults;
using KaspaBot.Domain.Entities;
using KaspaBot.Domain.Interfaces;
using KaspaBot.Domain.ValueObjects;
using KaspaBot.Infrastructure.Extensions;
using KaspaBot.Infrastructure.Repositories;
using KaspaBot.Infrastructure.Services;
using KaspaBot.Presentation;
using KaspaBot.Presentation.Telegram;
using KaspaBot.Presentation.Telegram.CommandHandlers;
using MediatR;
using Mexc.Net.Enums;
using Mexc.Net.Objects.Models.Spot;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

[assembly: CompilationRelaxations(8)]
[assembly: RuntimeCompatibility(WrapNonExceptionThrows = true)]
[assembly: Debuggable(DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints)]
[assembly: TargetFramework(".NETCoreApp,Version=v8.0", FrameworkDisplayName = ".NET 8.0")]
[assembly: AssemblyCompany("KaspaBot.Presentation")]
[assembly: AssemblyConfiguration("Release")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0+f4aef71110d6b470e9e70939744edbbe17e6a180")]
[assembly: AssemblyProduct("KaspaBot.Presentation")]
[assembly: AssemblyTitle("KaspaBot.Presentation")]
[assembly: AssemblyVersion("1.0.0.0")]
[module: RefSafetyRules(11)]
public class AdminShutdownNotifier : IHostedService
{
	private readonly ITelegramBotClient _botClient;

	private readonly IConfiguration _configuration;

	private readonly ILogger<AdminShutdownNotifier> _logger;

	private readonly IHostApplicationLifetime _appLifetime;

	private CancellationTokenRegistration? _stoppingRegistration;

	public AdminShutdownNotifier(ITelegramBotClient botClient, IConfiguration configuration, ILogger<AdminShutdownNotifier> logger, IHostApplicationLifetime appLifetime)
	{
		_botClient = botClient;
		_configuration = configuration;
		_logger = logger;
		_appLifetime = appLifetime;
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		_stoppingRegistration = _appLifetime.ApplicationStopping.Register(OnStopping);
		return Task.CompletedTask;
	}

	private async void OnStopping()
	{
		try
		{
			if (long.TryParse(_configuration["Telegram:AdminChatId"], out var result))
			{
				await _botClient.SendMessage(result, "✅ Приложение остановлено.");
			}
		}
		catch (Exception exception)
		{
			_logger.LogError(exception, "Ошибка при отправке финального сообщения админу");
		}
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		_stoppingRegistration?.Dispose();
		return Task.CompletedTask;
	}
}
public class AdminStartupNotifier : IHostedService
{
	private readonly IUserRepository _userRepository;

	private readonly ITelegramBotClient _botClient;

	private readonly IConfiguration _configuration;

	private readonly ILogger<AdminStartupNotifier> _logger;

	private bool _notified;

	private readonly UserStreamManager _userStreamManager;

	public AdminStartupNotifier(IUserRepository userRepository, ITelegramBotClient botClient, IConfiguration configuration, ILogger<AdminStartupNotifier> logger, UserStreamManager userStreamManager)
	{
		_userRepository = userRepository;
		_botClient = botClient;
		_configuration = configuration;
		_logger = logger;
		_userStreamManager = userStreamManager;
	}

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		if (_notified)
		{
			return;
		}
		_notified = true;
		try
		{
			if (long.TryParse(_configuration["Telegram:AdminChatId"], out var result))
			{
				ChatId chatId = new ChatId(result);
				List<KaspaBot.Domain.Entities.User> list = await _userRepository.GetAllAsync();
				string text = $"✅ Приложение успешно запущено! Количество пользователей в базе: {list.Count}";
				await _botClient.SendMessage(chatId, text, ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken);
			}
			await _userStreamManager.InitializeAllAsync(cancellationToken);
			_logger.LogInformation("[STARTUP] Начинаем проверку ордеров при старте приложения...");
			IOrderRecoveryService orderRecoveryService = _userStreamManager.GetServiceProvider().GetRequiredService<IOrderRecoveryService>();
			foreach (KaspaBot.Domain.Entities.User user in await _userRepository.GetAllAsync())
			{
				try
				{
					await orderRecoveryService.RunRecoveryForUser(user.Id, cancellationToken);
					_logger.LogInformation($"[STARTUP] Проверка ордеров для пользователя {user.Id} завершена");
				}
				catch (Exception exception)
				{
					_logger.LogError(exception, $"[STARTUP] Ошибка при проверке ордеров для пользователя {user.Id}");
				}
			}
			_logger.LogInformation("[STARTUP] Проверка ордеров при старте приложения завершена");
		}
		catch (Exception exception2)
		{
			_logger.LogError(exception2, "Ошибка при отправке уведомления админу о старте приложения");
		}
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		return Task.CompletedTask;
	}
}
[CompilerGenerated]
internal class Program
{
	private static async Task <Main>$(string[] args)
	{
		IHost host = Host.CreateDefaultBuilder(args).ConfigureServices(delegate(HostBuilderContext context, IServiceCollection services)
		{
			IConfiguration configuration2 = context.Configuration;
			services.AddInfrastructure(configuration2);
			if (configuration2["Mexc:ApiKey"] == null)
			{
				throw new ArgumentNullException("Mexc:ApiKey is not configured");
			}
			if (configuration2["Mexc:ApiSecret"] == null)
			{
				throw new ArgumentNullException("Mexc:ApiSecret is not configured");
			}
			string token = configuration2["Telegram:Token"] ?? throw new ArgumentNullException("Telegram:Token is not configured");
			services.AddSingleton((ITelegramBotClient)new TelegramBotClient(token));
			services.AddScoped<TradingCommandHandler>();
			services.AddScoped<IUpdateHandler, TelegramUpdateHandler>((IServiceProvider provider) => new TelegramUpdateHandler(provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<ILogger<TelegramUpdateHandler>>(), provider.GetRequiredService<IHostApplicationLifetime>(), provider.GetRequiredService<ILoggerFactory>()));
			services.AddHostedService<TelegramPollingService>();
			services.AddHostedService<AdminStartupNotifier>();
			services.AddHostedService<AdminShutdownNotifier>();
			services.AddSingleton<OrderRecoveryService>();
			services.AddSingleton((Func<IServiceProvider, IOrderRecoveryService>)((IServiceProvider sp) => sp.GetRequiredService<OrderRecoveryService>()));
			services.AddHostedService<DcaBuyService>();
			services.AddSingleton<IBotMessenger, BotMessenger>();
		}).UseSerilog(delegate(HostBuilderContext context, LoggerConfiguration config)
		{
			config.ReadFrom.Configuration(context.Configuration);
		})
			.Build();
		IServiceScope scope = host.Services.CreateScope();
		try
		{
			UserStreamManager requiredService = scope.ServiceProvider.GetRequiredService<UserStreamManager>();
			ITelegramBotClient botClient = scope.ServiceProvider.GetRequiredService<ITelegramBotClient>();
			IConfiguration configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
			requiredService.OnOrderSold += async delegate(long userId, decimal qty, decimal price, decimal usdt, decimal profit)
			{
				string text = NotificationFormatter.Profit(qty, price, usdt, profit);
				await botClient.SendMessage(userId, text, ParseMode.Html);
			};
			requiredService.OnStatusChangeNotification += async delegate(long userId, string orderId, string oldStatus, string newStatus, string reason)
			{
				if (long.TryParse(configuration["Telegram:AdminChatId"], out var result))
				{
					string text = $"\ud83d\udd04 <b>Изменение статуса ордера</b>\n\n\ud83d\udc64 <b>Пользователь:</b> <code>{userId}</code>\n\ud83c\udd94 <b>Ордер:</b> <code>{orderId}</code>\n\ud83d\udcca <b>Статус:</b> <code>{oldStatus}</code> → <code>{newStatus}</code>\n\ud83d\udca1 <b>Причина:</b> {reason}\n\n⏰ <b>Время:</b> {DateTime.UtcNow:HH:mm:ss} UTC";
					ITelegramBotClient botClient2 = botClient;
					ChatId chatId = result;
					CancellationToken none = CancellationToken.None;
					await botClient2.SendMessage(chatId, text, ParseMode.Html, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, none);
				}
			};
			requiredService.OnDebounceCompleted += async delegate(long userId)
			{
				scope.ServiceProvider.GetRequiredService<ILogger<Program>>().LogInformation($"[PROGRAM] Получено событие завершения дебаунса для user={userId}");
			};
		}
		finally
		{
			if (scope != null)
			{
				scope.Dispose();
			}
		}
		await host.RunAsync();
	}
}
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class BotCommandAttribute : Attribute
{
	public string Description { get; }

	public bool AdminOnly { get; set; }

	public string? UserIdParameter { get; set; }

	public BotCommandAttribute(string description)
	{
		Description = description;
	}
}
public static class TelegramBotExtensions
{
	public static IServiceCollection AddTelegramBot(this IServiceCollection services, IConfiguration configuration)
	{
		string token = configuration["Telegram:Token"] ?? throw new ArgumentNullException("Telegram token is not configured");
		services.AddSingleton((ITelegramBotClient)new TelegramBotClient(token));
		services.AddSingleton<IUpdateHandler, TelegramUpdateHandler>();
		services.AddHostedService<TelegramPollingService>();
		return services;
	}
}
public class TelegramPollingService : BackgroundService
{
	private readonly ITelegramBotClient _botClient;

	private readonly IUpdateHandler _updateHandler;

	private readonly ILogger<TelegramPollingService> _logger;

	public TelegramPollingService(ITelegramBotClient botClient, IUpdateHandler updateHandler, ILogger<TelegramPollingService> logger)
	{
		_botClient = botClient;
		_updateHandler = updateHandler;
		_logger = logger;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		ReceiverOptions receiverOptions = new ReceiverOptions
		{
			AllowedUpdates = Array.Empty<UpdateType>()
		};
		_logger.LogInformation("Starting telegram bot polling...");
		await _botClient.ReceiveAsync(_updateHandler, receiverOptions, stoppingToken);
		_logger.LogInformation("Telegram bot polling is running...");
	}
}
public class TelegramUpdateHandler : IUpdateHandler
{
	private readonly IServiceScopeFactory _scopeFactory;

	private readonly ILogger<TelegramUpdateHandler> _logger;

	private readonly IHostApplicationLifetime _appLifetime;

	private readonly ILoggerFactory _loggerFactory;

	private static readonly DateTime AppStartTime = DateTime.UtcNow;

	private static readonly ConcurrentDictionary<long, int> RegistrationStates = new ConcurrentDictionary<long, int>();

	private static readonly ConcurrentDictionary<long, string> TempApiKeys = new ConcurrentDictionary<long, string>();

	private static readonly ConcurrentDictionary<long, string> ConfigStates = new ConcurrentDictionary<long, string>();

	public TelegramUpdateHandler(IServiceScopeFactory scopeFactory, ILogger<TelegramUpdateHandler> logger, IHostApplicationLifetime appLifetime, ILoggerFactory loggerFactory)
	{
		_scopeFactory = scopeFactory;
		_logger = logger;
		_appLifetime = appLifetime;
		_loggerFactory = loggerFactory;
	}

	public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
	{
		using IServiceScope scope = _scopeFactory.CreateScope();
		TradingCommandHandler tradingCommandHandler = scope.ServiceProvider.GetRequiredService<TradingCommandHandler>();
		IUserRepository userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
		try
		{
			if (update.Type == UpdateType.CallbackQuery)
			{
				CallbackQuery callback = update.CallbackQuery;
				long userId = callback.From.Id;
				string data = callback.Data;
				_logger.LogInformation($"Получен CallbackQuery от {userId}: {data}");
				if (!(await userRepository.ExistsAsync(userId)))
				{
					string id = callback.Id;
					CancellationToken cancellationToken2 = cancellationToken;
					await botClient.AnswerCallbackQuery(id, "❌ Вы не зарегистрированы. Отправьте /start", showAlert: false, null, null, cancellationToken2);
					return;
				}
				KaspaBot.Domain.Entities.User user = await userRepository.GetByIdAsync(userId);
				if (user == null)
				{
					string id2 = callback.Id;
					CancellationToken cancellationToken2 = cancellationToken;
					await botClient.AnswerCallbackQuery(id2, "❌ Пользователь не найден", showAlert: false, null, null, cancellationToken2);
					return;
				}
				string value = data;
				switch (value)
				{
				case "config_OrderAmount":
				{
					ChatId chatId = userId;
					CancellationToken cancellationToken2 = cancellationToken;
					await botClient.SendMessage(chatId, "\ud83d\udcb0 <b>Введите новую сумму ордера (USDT):</b>\n\n\ud83d\udca1 <i>Минимум: 1 USDT</i>", ParseMode.Html, null, new ReplyKeyboardRemove(), null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
					ConfigStates[userId] = "OrderAmount";
					string id5 = callback.Id;
					cancellationToken2 = cancellationToken;
					await botClient.AnswerCallbackQuery(id5, null, showAlert: false, null, null, cancellationToken2);
					break;
				}
				case "config_MaxUsdtUsing":
				{
					ChatId chatId3 = userId;
					CancellationToken cancellationToken2 = cancellationToken;
					await botClient.SendMessage(chatId3, "\ud83d\udc8e <b>Введите новую максимальную сумму (USDT):</b>\n\n\ud83d\udca1 <i>Максимальная сумма для торговли</i>", ParseMode.Html, null, new ReplyKeyboardRemove(), null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
					ConfigStates[userId] = "MaxUsdtUsing";
					string id7 = callback.Id;
					cancellationToken2 = cancellationToken;
					await botClient.AnswerCallbackQuery(id7, null, showAlert: false, null, null, cancellationToken2);
					break;
				}
				case "config_PercentPriceChange":
				{
					ChatId chatId2 = userId;
					CancellationToken cancellationToken2 = cancellationToken;
					await botClient.SendMessage(chatId2, "\ud83d\udcc9 <b>Введите новый процент падения:</b>\n\n\ud83d\udca1 <i>Например: 0.5 (0.5%)</i>", ParseMode.Html, null, new ReplyKeyboardRemove(), null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
					ConfigStates[userId] = "PercentPriceChange";
					string id6 = callback.Id;
					cancellationToken2 = cancellationToken;
					await botClient.AnswerCallbackQuery(id6, null, showAlert: false, null, null, cancellationToken2);
					break;
				}
				case "config_PercentProfit":
				{
					ChatId chatId4 = userId;
					CancellationToken cancellationToken2 = cancellationToken;
					await botClient.SendMessage(chatId4, "\ud83d\udcc8 <b>Введите новый процент прибыли:</b>\n\n\ud83d\udca1 <i>Например: 0.5 (0.5%)</i>", ParseMode.Html, null, new ReplyKeyboardRemove(), null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
					ConfigStates[userId] = "PercentProfit";
					string id9 = callback.Id;
					cancellationToken2 = cancellationToken;
					await botClient.AnswerCallbackQuery(id9, null, showAlert: false, null, null, cancellationToken2);
					break;
				}
				case "config_ApiKeys":
				{
					ChatId chatId5 = userId;
					CancellationToken cancellationToken2 = cancellationToken;
					await botClient.SendMessage(chatId5, "\ud83d\udd11 <b>Введите новый API Key:</b>\n\n\ud83d\udca1 <i>Публичный ключ от MEXC</i>", ParseMode.Html, null, new ReplyKeyboardRemove(), null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
					ConfigStates[userId] = "ApiKey";
					string id10 = callback.Id;
					cancellationToken2 = cancellationToken;
					await botClient.AnswerCallbackQuery(id10, null, showAlert: false, null, null, cancellationToken2);
					break;
				}
				case "config_OrderAmountMode":
				{
					InlineKeyboardMarkup replyMarkup = new InlineKeyboardMarkup(new InlineKeyboardButton[2][]
					{
						new InlineKeyboardButton[2]
						{
							InlineKeyboardButton.WithCallbackData("Фиксированный", "set_OrderAmountMode_Fixed"),
							InlineKeyboardButton.WithCallbackData("Динамический", "set_OrderAmountMode_Dynamic")
						},
						new InlineKeyboardButton[1] { InlineKeyboardButton.WithCallbackData("⬅\ufe0f Назад", "config_back") }
					});
					string text2 = ((user.Settings.OrderAmountMode == OrderAmountMode.Fixed) ? "Фиксированный" : "Динамический");
					ChatId chatId8 = userId;
					string text3 = "\ud83d\udd22 <b>Настройки ордера</b>\n\n<b>Режим:</b> <code>" + text2 + "</code>\n\n<b>Выберите режим:</b>";
					CancellationToken cancellationToken2 = cancellationToken;
					await botClient.SendMessage(chatId8, text3, ParseMode.Html, null, replyMarkup, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
					ConfigStates[userId] = "OrderAmountMode";
					string id14 = callback.Id;
					cancellationToken2 = cancellationToken;
					await botClient.AnswerCallbackQuery(id14, null, showAlert: false, null, null, cancellationToken2);
					break;
				}
				case "set_OrderAmountMode_Fixed":
				{
					user.Settings.OrderAmountMode = OrderAmountMode.Fixed;
					await userRepository.UpdateAsync(user);
					ChatId chatId7 = userId;
					CancellationToken cancellationToken2 = cancellationToken;
					await botClient.SendMessage(chatId7, "\ud83d\udcb0 <b>Введите сумму ордера (USDT):</b>\n\n<code>Минимум: 1 USDT</code>", ParseMode.Html, null, new ReplyKeyboardRemove(), null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
					ConfigStates[userId] = "OrderAmount";
					string id13 = callback.Id;
					cancellationToken2 = cancellationToken;
					await botClient.AnswerCallbackQuery(id13, null, showAlert: false, null, null, cancellationToken2);
					break;
				}
				case "set_OrderAmountMode_Dynamic":
				{
					_logger.LogInformation($"[CONFIG] set_OrderAmountMode_Dynamic: user={userId}");
					user.Settings.OrderAmountMode = OrderAmountMode.Dynamic;
					await userRepository.UpdateAsync(user);
					ChatId chatId6 = userId;
					CancellationToken cancellationToken2 = cancellationToken;
					await botClient.SendMessage(chatId6, "⚙\ufe0f <b>Введите коэффициент для динамического режима:</b>\n\n<code>Например: 40</code>\n\n<i>Отправьте число в чат. Для отмены — напишите 'Отмена'.</i>", ParseMode.Html, null, new ReplyKeyboardRemove(), null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
					ConfigStates[userId] = "DynamicOrderCoef";
					string id12 = callback.Id;
					cancellationToken2 = cancellationToken;
					await botClient.AnswerCallbackQuery(id12, null, showAlert: false, null, null, cancellationToken2);
					_logger.LogInformation($"[CONFIG] Ожидание ввода коэффициента: user={userId}");
					break;
				}
				case "config_back":
				{
					await ShowInlineConfigMenu(botClient, user, cancellationToken);
					string id11 = callback.Id;
					CancellationToken cancellationToken2 = cancellationToken;
					await botClient.AnswerCallbackQuery(id11, null, showAlert: false, null, null, cancellationToken2);
					break;
				}
				case "config_close":
				{
					if (callback.Message != null)
					{
						await botClient.DeleteMessage(userId, callback.Message.MessageId, cancellationToken);
					}
					ConfigStates.TryRemove(userId, out value);
					string id8 = callback.Id;
					CancellationToken cancellationToken2 = cancellationToken;
					await botClient.AnswerCallbackQuery(id8, "❌ Меню закрыто", showAlert: false, null, null, cancellationToken2);
					break;
				}
				case "config_toggle_autotrade":
				{
					user.Settings.IsAutoTradeEnabled = !user.Settings.IsAutoTradeEnabled;
					await userRepository.UpdateAsync(user);
					await ShowInlineConfigMenu(botClient, user, cancellationToken);
					string id4 = callback.Id;
					string text = (user.Settings.IsAutoTradeEnabled ? "Автоторговля включена" : "Автоторговля отключена");
					CancellationToken cancellationToken2 = cancellationToken;
					await botClient.AnswerCallbackQuery(id4, text, showAlert: false, null, null, cancellationToken2);
					break;
				}
				default:
				{
					_logger.LogWarning($"[CONFIG] Неизвестная команда callback: {callback.Data} для user={userId}");
					string id3 = callback.Id;
					CancellationToken cancellationToken2 = cancellationToken;
					await botClient.AnswerCallbackQuery(id3, "❌ Неизвестная команда", showAlert: false, null, null, cancellationToken2);
					break;
				}
				}
			}
			else
			{
				if (update.Type != UpdateType.Message || update.Message?.Text == null)
				{
					return;
				}
				long userId = update.Message.Chat.Id;
				string data = update.Message.Text.Trim();
				if (data.Equals("/SoftExit", StringComparison.OrdinalIgnoreCase) && userId == 130822044)
				{
					if (update.Message.Date.ToUniversalTime() < AppStartTime.AddSeconds(-10.0))
					{
						_logger.LogInformation($"Пропущено устаревшее /SoftExit от {update.Message.Chat.Id}");
					}
					else
					{
						ChatId chatId9 = userId;
						CancellationToken cancellationToken2 = cancellationToken;
						await botClient.SendMessage(chatId9, "⏳ Приложение завершает работу, дождитесь завершения всех операций...", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
						_appLifetime.StopApplication();
					}
					return;
				}
				string value;
				if (!(await userRepository.ExistsAsync(userId)))
				{
					if (RegistrationStates.TryGetValue(userId, out var value2))
					{
						switch (value2)
						{
						case 1:
						{
							TempApiKeys[userId] = data;
							RegistrationStates[userId] = 2;
							ChatId chatId16 = userId;
							CancellationToken cancellationToken2 = cancellationToken;
							await botClient.SendMessage(chatId16, "Пожалуйста, отправьте ваш API Secret (секретный ключ).", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
							return;
						}
						case 2:
						{
							string apiKey = TempApiKeys[userId];
							string apiSecret = data;
							ILogger<MexcService> logger = _loggerFactory.CreateLogger<MexcService>();
							Result<MexcAccountInfo> result = await MexcService.Create(apiKey, apiSecret, logger).GetAccountInfoAsync(cancellationToken);
							CancellationToken cancellationToken2;
							if (!result.IsSuccess)
							{
								ChatId chatId11 = userId;
								string text4 = "Ошибка: ключи невалидны: " + result.Errors.FirstOrDefault()?.Message;
								cancellationToken2 = cancellationToken;
								await botClient.SendMessage(chatId11, text4, ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
								RegistrationStates[userId] = 1;
								ChatId chatId12 = userId;
								cancellationToken2 = cancellationToken;
								await botClient.SendMessage(chatId12, "Пожалуйста, отправьте ваш API Key (публичный ключ) заново.", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
								return;
							}
							KaspaBot.Domain.Entities.User user2 = new KaspaBot.Domain.Entities.User(userId, update.Message.From?.Username ?? $"user{userId}")
							{
								ApiCredentials = new UserApiCredentials
								{
									ApiKey = apiKey,
									ApiSecret = apiSecret
								},
								Settings = new UserSettings
								{
									OrderAmount = 1m,
									MaxUsdtUsing = 200m,
									PercentPriceChange = 0.5m,
									PercentProfit = 0.5m
								},
								IsActive = true
							};
							await userRepository.AddAsync(user2);
							RegistrationStates.TryRemove(userId, out var _);
							TempApiKeys.TryRemove(userId, out value);
							RegistrationStates[userId] = 3;
							ChatId chatId13 = userId;
							cancellationToken2 = cancellationToken;
							await botClient.SendMessage(chatId13, "✅ Регистрация завершена! Ваши ключи сохранены и проверены.", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
							ChatId chatId14 = userId;
							cancellationToken2 = cancellationToken;
							await botClient.SendMessage(chatId14, "Автоторговля по умолчанию выключена. Для включения используйте команду /autotrade", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
							ChatId chatId15 = userId;
							cancellationToken2 = cancellationToken;
							await botClient.SendMessage(chatId15, "Ваши дефолтные настройки:\nСумма ордера: 1 USDT\nМаксимальная сумма: 200 USDT\n% падения: 0.5%\n% прибыли: 0.5%\n\nДоступные команды:\n/config — изменить настройки\n/buy — купить KASUSDT (пример)\n", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
							return;
						}
						case 3:
						{
							ChatId chatId10 = userId;
							CancellationToken cancellationToken2 = cancellationToken;
							await botClient.SendMessage(chatId10, "Вы уже завершили регистрацию. Можете пользоваться ботом.", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
							return;
						}
						}
					}
					if (data.Equals("/start", StringComparison.OrdinalIgnoreCase))
					{
						RegistrationStates[userId] = 1;
						ChatId chatId17 = userId;
						CancellationToken cancellationToken2 = cancellationToken;
						await botClient.SendMessage(chatId17, "Добро пожаловать! Для работы с ботом необходимо пройти регистрацию.\nПожалуйста, отправьте ваш API Key (публичный ключ).", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
					}
					else
					{
						ChatId chatId18 = userId;
						CancellationToken cancellationToken2 = cancellationToken;
						await botClient.SendMessage(chatId18, "❗\ufe0fВы не зарегистрированы в системе. Для регистрации отправьте /start в этот чат.", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
					}
					return;
				}
				if (data.Equals("/config", StringComparison.OrdinalIgnoreCase))
				{
					KaspaBot.Domain.Entities.User user = await userRepository.GetByIdAsync(userId);
					CancellationToken cancellationToken2;
					if (user == null)
					{
						ChatId chatId19 = userId;
						cancellationToken2 = cancellationToken;
						await botClient.SendMessage(chatId19, "❌ Пользователь не найден.", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
						return;
					}
					decimal usdtBalance = default(decimal);
					try
					{
						Result<MexcAccountInfo> result2 = await scope.ServiceProvider.GetRequiredService<IMexcService>().GetAccountInfoAsync(CancellationToken.None);
						if (result2.IsSuccess)
						{
							usdtBalance = result2.Value.Balances.FirstOrDefault((MexcAccountBalance b) => b.Asset == "USDT")?.Available ?? 0m;
						}
					}
					catch
					{
					}
					string text5 = ((user.Settings.OrderAmountMode == OrderAmountMode.Fixed) ? $"\ud83d\udcb0 <b>Сумма ордера:</b> <code>{user.Settings.OrderAmount:F2} USDT</code>" : $"⚙\ufe0f <b>Коэффициент:</b> <code>{user.Settings.DynamicOrderCoef:F2}</code>\n\ud83d\udcb0 <b>Текущий размер:</b> <code>{user.Settings.GetOrderAmount(usdtBalance):F2} USDT</code>");
					string value4 = (user.Settings.IsAutoTradeEnabled ? "\ud83d\udfe2 автоторговля ВКЛ" : "\ud83d\udd34 автоторговля ВЫКЛ");
					string text6 = $"⚙\ufe0f <b>Настройки бота</b>\n\n{value4}\n\ud83d\udd22 <b>Настройки ордера:</b> <code>{((user.Settings.OrderAmountMode == OrderAmountMode.Fixed) ? "Фиксированный" : "Динамический")}</code>\n" + text5 + "\n" + $"\ud83d\udc8e <b>Макс. сумма:</b> <code>{user.Settings.MaxUsdtUsing:F2} USDT</code>\n" + $"\ud83d\udcc9 <b>% падения:</b> <code>{user.Settings.PercentPriceChange:F1}%</code>\n" + $"\ud83d\udcc8 <b>% прибыли:</b> <code>{user.Settings.PercentProfit:F1}%</code>\n" + "\ud83d\udd11 <b>API Key:</b> <code>" + user.ApiCredentials.ApiKey.Substring(0, Math.Min(8, user.ApiCredentials.ApiKey.Length)) + "...</code>\n\n\ud83d\udca1 <i>Выберите параметр для изменения:</i>";
					InlineKeyboardMarkup replyMarkup2 = new InlineKeyboardMarkup(new InlineKeyboardButton[6][]
					{
						new InlineKeyboardButton[1] { InlineKeyboardButton.WithCallbackData(user.Settings.IsAutoTradeEnabled ? "\ud83d\uded1 Отключить автоторговлю" : "▶\ufe0f Включить автоторговлю", "config_toggle_autotrade") },
						new InlineKeyboardButton[1] { InlineKeyboardButton.WithCallbackData("\ud83d\udd22 Настройки ордера", "config_OrderAmountMode") },
						new InlineKeyboardButton[1] { InlineKeyboardButton.WithCallbackData("\ud83d\udc8e Макс. сумма", "config_MaxUsdtUsing") },
						new InlineKeyboardButton[2]
						{
							InlineKeyboardButton.WithCallbackData("\ud83d\udcc9 % падения", "config_PercentPriceChange"),
							InlineKeyboardButton.WithCallbackData("\ud83d\udcc8 % прибыли", "config_PercentProfit")
						},
						new InlineKeyboardButton[1] { InlineKeyboardButton.WithCallbackData("\ud83d\udd11 API ключи", "config_ApiKeys") },
						new InlineKeyboardButton[1] { InlineKeyboardButton.WithCallbackData("❌ Закрыть", "config_close") }
					});
					ChatId chatId20 = userId;
					cancellationToken2 = cancellationToken;
					await botClient.SendMessage(chatId20, text6, ParseMode.Html, null, replyMarkup2, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
					ConfigStates[userId] = "menu";
					return;
				}
				if (ConfigStates.TryGetValue(userId, out string configStep))
				{
					KaspaBot.Domain.Entities.User user = await userRepository.GetByIdAsync(userId);
					if (user == null)
					{
						ChatId chatId21 = userId;
						CancellationToken cancellationToken2 = cancellationToken;
						await botClient.SendMessage(chatId21, "Пользователь не найден.", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
						ConfigStates.TryRemove(userId, out value);
						return;
					}
					if (data == "Отмена")
					{
						ChatId chatId22 = userId;
						CancellationToken cancellationToken2 = cancellationToken;
						await botClient.SendMessage(chatId22, "❌ <b>Изменение настроек отменено</b>", ParseMode.Html, null, new ReplyKeyboardRemove(), null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
						ConfigStates.TryRemove(userId, out value);
						return;
					}
					decimal usdtBalance;
					switch (configStep)
					{
					case "menu":
						switch (data)
						{
						case "\ud83d\udd22 Настройки ордера":
						{
							string text7 = ((user.Settings.OrderAmountMode == OrderAmountMode.Fixed) ? "Фиксированный" : "Динамический");
							await botClient.SendMessage(userId, "\ud83d\udd22 <b>Настройки ордера</b>\n\n<b>Режим:</b> <code>" + text7 + "</code>\n\n<b>Выберите режим:</b>", ParseMode.Html, null, cancellationToken: cancellationToken, replyMarkup: new InlineKeyboardMarkup(new InlineKeyboardButton[2][]
							{
								new InlineKeyboardButton[2]
								{
									InlineKeyboardButton.WithCallbackData("Фиксированный", "set_OrderAmountMode_Fixed"),
									InlineKeyboardButton.WithCallbackData("Динамический", "set_OrderAmountMode_Dynamic")
								},
								new InlineKeyboardButton[1] { InlineKeyboardButton.WithCallbackData("⬅\ufe0f Назад", "config_back") }
							}));
							ConfigStates[userId] = "OrderAmountMode";
							break;
						}
						case "\ud83d\udc8e Макс. сумма":
						{
							ChatId chatId28 = userId;
							CancellationToken cancellationToken2 = cancellationToken;
							await botClient.SendMessage(chatId28, "\ud83d\udc8e <b>Введите новую максимальную сумму (USDT):</b>\n\n\ud83d\udca1 <i>Максимальная сумма для торговли</i>", ParseMode.Html, null, new ReplyKeyboardRemove(), null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
							ConfigStates[userId] = "MaxUsdtUsing";
							break;
						}
						case "\ud83d\udcc9 % падения":
						{
							ChatId chatId27 = userId;
							CancellationToken cancellationToken2 = cancellationToken;
							await botClient.SendMessage(chatId27, "\ud83d\udcc9 <b>Введите новый процент падения:</b>\n\n\ud83d\udca1 <i>Например: 0.5 (0.5%)</i>", ParseMode.Html, null, new ReplyKeyboardRemove(), null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
							ConfigStates[userId] = "PercentPriceChange";
							break;
						}
						case "\ud83d\udcc8 % прибыли":
						{
							ChatId chatId26 = userId;
							CancellationToken cancellationToken2 = cancellationToken;
							await botClient.SendMessage(chatId26, "\ud83d\udcc8 <b>Введите новый процент прибыли:</b>\n\n\ud83d\udca1 <i>Например: 0.5 (0.5%)</i>", ParseMode.Html, null, new ReplyKeyboardRemove(), null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
							ConfigStates[userId] = "PercentProfit";
							break;
						}
						case "\ud83d\udd11 API ключи":
						{
							ChatId chatId25 = userId;
							CancellationToken cancellationToken2 = cancellationToken;
							await botClient.SendMessage(chatId25, "\ud83d\udd11 <b>Введите новый API Key:</b>\n\n\ud83d\udca1 <i>Публичный ключ от Mexc</i>", ParseMode.Html, null, new ReplyKeyboardRemove(), null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
							ConfigStates[userId] = "ApiKey";
							break;
						}
						case "❌ Отмена":
						{
							ChatId chatId24 = userId;
							CancellationToken cancellationToken2 = cancellationToken;
							await botClient.SendMessage(chatId24, "❌ <b>Изменение настроек отменено</b>", ParseMode.Html, null, new ReplyKeyboardRemove(), null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
							ConfigStates.TryRemove(userId, out value);
							break;
						}
						default:
						{
							ChatId chatId23 = userId;
							CancellationToken cancellationToken2 = cancellationToken;
							await botClient.SendMessage(chatId23, "❌ <b>Пожалуйста, выберите действие из меню</b>", ParseMode.Html, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
							break;
						}
						}
						return;
					case "OrderAmount":
						if (decimal.TryParse(data.Replace(",", "."), out usdtBalance) && usdtBalance >= 1m)
						{
							user.Settings.OrderAmount = usdtBalance;
							await userRepository.UpdateAsync(user);
							ChatId chatId32 = userId;
							string text9 = $"✅ <b>Сумма ордера обновлена:</b> <code>{usdtBalance:F2} USDT</code>";
							CancellationToken cancellationToken2 = cancellationToken;
							await botClient.SendMessage(chatId32, text9, ParseMode.Html, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
							await ShowInlineConfigMenu(botClient, user, cancellationToken);
						}
						else
						{
							ChatId chatId33 = userId;
							CancellationToken cancellationToken2 = cancellationToken;
							await botClient.SendMessage(chatId33, "❌ <b>Ошибка!</b> Минимальная сумма ордера — 1 USDT", ParseMode.Html, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
						}
						return;
					case "MaxUsdtUsing":
						if (decimal.TryParse(data.Replace(",", "."), out usdtBalance) && usdtBalance > 0m)
						{
							user.Settings.MaxUsdtUsing = usdtBalance;
							await userRepository.UpdateAsync(user);
							ChatId chatId39 = userId;
							string text12 = $"✅ <b>Максимальная сумма обновлена:</b> <code>{usdtBalance:F2} USDT</code>";
							CancellationToken cancellationToken2 = cancellationToken;
							await botClient.SendMessage(chatId39, text12, ParseMode.Html, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
							await ShowInlineConfigMenu(botClient, user, cancellationToken);
						}
						else
						{
							ChatId chatId40 = userId;
							CancellationToken cancellationToken2 = cancellationToken;
							await botClient.SendMessage(chatId40, "❌ <b>Ошибка!</b> Введите число больше 0.", ParseMode.Html, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
						}
						return;
					case "PercentPriceChange":
						if (decimal.TryParse(data.Replace(",", "."), out usdtBalance) && usdtBalance > 0m)
						{
							user.Settings.PercentPriceChange = usdtBalance;
							await userRepository.UpdateAsync(user);
							ChatId chatId37 = userId;
							string text11 = $"✅ <b>% падения обновлён:</b> <code>{usdtBalance:F1}%</code>";
							CancellationToken cancellationToken2 = cancellationToken;
							await botClient.SendMessage(chatId37, text11, ParseMode.Html, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
							await ShowInlineConfigMenu(botClient, user, cancellationToken);
						}
						else
						{
							ChatId chatId38 = userId;
							CancellationToken cancellationToken2 = cancellationToken;
							await botClient.SendMessage(chatId38, "❌ <b>Ошибка!</b> Введите число больше 0.", ParseMode.Html, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
						}
						return;
					case "PercentProfit":
						if (decimal.TryParse(data.Replace(",", "."), out usdtBalance) && usdtBalance > 0m)
						{
							user.Settings.PercentProfit = usdtBalance;
							await userRepository.UpdateAsync(user);
							ChatId chatId30 = userId;
							string text8 = $"✅ <b>% прибыли обновлён:</b> <code>{usdtBalance:F1}%</code>";
							CancellationToken cancellationToken2 = cancellationToken;
							await botClient.SendMessage(chatId30, text8, ParseMode.Html, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
							await ShowInlineConfigMenu(botClient, user, cancellationToken);
						}
						else
						{
							ChatId chatId31 = userId;
							CancellationToken cancellationToken2 = cancellationToken;
							await botClient.SendMessage(chatId31, "❌ <b>Ошибка!</b> Введите число больше 0.", ParseMode.Html, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
						}
						return;
					case "ApiKey":
					{
						TempApiKeys[userId] = data;
						ChatId chatId29 = userId;
						CancellationToken cancellationToken2 = cancellationToken;
						await botClient.SendMessage(chatId29, "Теперь введите новый API Secret:", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
						ConfigStates[userId] = "ApiSecretUpdate";
						return;
					}
					case "ApiSecretUpdate":
					{
						string value5;
						string apiSecret = (TempApiKeys.TryGetValue(userId, out value5) ? value5 : null);
						string apiKey = data;
						if (string.IsNullOrEmpty(apiSecret))
						{
							ChatId chatId34 = userId;
							CancellationToken cancellationToken2 = cancellationToken;
							await botClient.SendMessage(chatId34, "Сначала введите новый API Key.", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
							ConfigStates[userId] = "ApiKey";
							return;
						}
						ILogger<MexcService> logger2 = _loggerFactory.CreateLogger<MexcService>();
						Result<MexcAccountInfo> result3 = await MexcService.Create(apiSecret, apiKey, logger2).GetAccountInfoAsync(cancellationToken);
						if (!result3.IsSuccess)
						{
							ChatId chatId35 = userId;
							string text10 = "Ошибка: ключи невалидны: " + result3.Errors.FirstOrDefault()?.Message;
							CancellationToken cancellationToken2 = cancellationToken;
							await botClient.SendMessage(chatId35, text10, ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
							ConfigStates[userId] = "ApiKey";
						}
						else
						{
							user.ApiCredentials.ApiKey = apiSecret;
							user.ApiCredentials.ApiSecret = apiKey;
							await userRepository.UpdateAsync(user);
							await scope.ServiceProvider.GetRequiredService<UserStreamManager>().ReloadUserAsync(user, cancellationToken);
							ChatId chatId36 = userId;
							CancellationToken cancellationToken2 = cancellationToken;
							await botClient.SendMessage(chatId36, "✅ <b>API ключи успешно обновлены и переподключены!</b>", ParseMode.Html, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
							TempApiKeys.TryRemove(userId, out value);
							await ShowInlineConfigMenu(botClient, user, cancellationToken);
						}
						return;
					}
					case "DynamicOrderCoef":
						_logger.LogInformation($"[CONFIG] Ввод коэффициента: '{data}' для user={userId}");
						if (decimal.TryParse(data, out usdtBalance) && usdtBalance >= 1m && usdtBalance <= 1000m)
						{
							user.Settings.DynamicOrderCoef = usdtBalance;
							await userRepository.UpdateAsync(user);
							await ShowInlineConfigMenu(botClient, user, cancellationToken);
							_logger.LogInformation($"[CONFIG] Коэффициент обновлён: {usdtBalance} для user={userId}");
						}
						else
						{
							await botClient.SendMessage(userId, "Некорректный коэффициент. Введите число от 1 до 1000 или напишите 'Отмена'.");
							_logger.LogWarning($"[CONFIG] Некорректный ввод коэффициента: '{data}' для user={userId}");
						}
						return;
					}
				}
				await tradingCommandHandler.HandleUpdateAsync(update.Message, cancellationToken);
			}
		}
		catch (Exception exception)
		{
			_logger.LogError(exception, "Error processing update");
		}
	}

	public async Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
	{
		_logger.LogError(exception, "Telegram polling error");
		await Task.CompletedTask;
	}

	public async Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, HandleErrorSource errorSource, CancellationToken cancellationToken)
	{
		_logger.LogError(exception, $"Telegram error from {errorSource}");
		await Task.CompletedTask;
	}

	private async Task ShowInlineConfigMenu(ITelegramBotClient botClient, KaspaBot.Domain.Entities.User user, CancellationToken cancellationToken)
	{
		decimal usdtBalance = default(decimal);
		try
		{
			Result<MexcAccountInfo> result = await _scopeFactory.CreateScope().ServiceProvider.GetRequiredService<IMexcService>().GetAccountInfoAsync(CancellationToken.None);
			if (result.IsSuccess)
			{
				usdtBalance = result.Value.Balances.FirstOrDefault((MexcAccountBalance b) => b.Asset == "USDT")?.Available ?? 0m;
			}
		}
		catch
		{
		}
		string text = ((user.Settings.OrderAmountMode == OrderAmountMode.Fixed) ? $"\ud83d\udcb0 <b>Сумма ордера:</b> <code>{user.Settings.OrderAmount:F2} USDT</code>" : $"⚙\ufe0f <b>Коэффициент:</b> <code>{user.Settings.DynamicOrderCoef:F2}</code>\n\ud83d\udcb0 <b>Текущий размер:</b> <code>{user.Settings.GetOrderAmount(usdtBalance):F2} USDT</code>");
		string value = (user.Settings.IsAutoTradeEnabled ? "\ud83d\udfe2 автоторговля ВКЛ" : "\ud83d\udd34 автоторговля ВЫКЛ");
		string text2 = $"⚙\ufe0f <b>Настройки бота</b>\n\n{value}\n\ud83d\udd22 <b>Настройки ордера:</b> <code>{((user.Settings.OrderAmountMode == OrderAmountMode.Fixed) ? "Фиксированный" : "Динамический")}</code>\n" + text + "\n" + $"\ud83d\udc8e <b>Макс. сумма:</b> <code>{user.Settings.MaxUsdtUsing:F2} USDT</code>\n" + $"\ud83d\udcc9 <b>% падения:</b> <code>{user.Settings.PercentPriceChange:F1}%</code>\n" + $"\ud83d\udcc8 <b>% прибыли:</b> <code>{user.Settings.PercentProfit:F1}%</code>\n" + "\ud83d\udd11 <b>API Key:</b> <code>" + user.ApiCredentials.ApiKey.Substring(0, Math.Min(8, user.ApiCredentials.ApiKey.Length)) + "...</code>\n\n\ud83d\udca1 <i>Выберите параметр для изменения:</i>";
		InlineKeyboardMarkup replyMarkup = new InlineKeyboardMarkup(new InlineKeyboardButton[6][]
		{
			new InlineKeyboardButton[1] { InlineKeyboardButton.WithCallbackData(user.Settings.IsAutoTradeEnabled ? "\ud83d\uded1 Отключить автоторговлю" : "▶\ufe0f Включить автоторговлю", "config_toggle_autotrade") },
			new InlineKeyboardButton[1] { InlineKeyboardButton.WithCallbackData("\ud83d\udd22 Настройки ордера", "config_OrderAmountMode") },
			new InlineKeyboardButton[1] { InlineKeyboardButton.WithCallbackData("\ud83d\udc8e Макс. сумма", "config_MaxUsdtUsing") },
			new InlineKeyboardButton[2]
			{
				InlineKeyboardButton.WithCallbackData("\ud83d\udcc9 % падения", "config_PercentPriceChange"),
				InlineKeyboardButton.WithCallbackData("\ud83d\udcc8 % прибыли", "config_PercentProfit")
			},
			new InlineKeyboardButton[1] { InlineKeyboardButton.WithCallbackData("\ud83d\udd11 API ключи", "config_ApiKeys") },
			new InlineKeyboardButton[1] { InlineKeyboardButton.WithCallbackData("❌ Закрыть", "config_close") }
		});
		await botClient.SendMessage(user.Id, text2, ParseMode.Html, null, replyMarkup, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken);
		ConfigStates[user.Id] = "menu";
	}
}
namespace KaspaBot.Presentation
{
	public class DcaBuyService : BackgroundService
	{
		private readonly IServiceProvider _serviceProvider;

		private readonly ILogger<DcaBuyService> _logger;

		private readonly HashSet<long> _lowBalanceWarnedUsers = new HashSet<long>();

		private readonly HashSet<long> _activeUsers = new HashSet<long>();

		private readonly ConcurrentDictionary<long, SemaphoreSlim> _userLocks = new ConcurrentDictionary<long, SemaphoreSlim>();

		private UserStreamManager? _userStreamManager;

		public DcaBuyService(IServiceProvider serviceProvider, ILogger<DcaBuyService> logger)
		{
			_serviceProvider = serviceProvider;
			_logger = logger;
		}

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			_logger.LogInformation("[DCA-DEBUG] Сервис автоторговли стартует");
			await Task.Delay(5000, stoppingToken);
			await Task.Delay(2000, stoppingToken);
			using IServiceScope scope = _serviceProvider.CreateScope();
			_userStreamManager = scope.ServiceProvider.GetRequiredService<UserStreamManager>();
			_userStreamManager.OnDebounceCompleted += OnDebounceCompleted;
			_logger.LogInformation("[DCA-DEBUG] Подписка на события UserStreamManager выполнена");
			ILoggerFactory loggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>();
			while (!stoppingToken.IsCancellationRequested)
			{
				try
				{
					using IServiceScope scope2 = _serviceProvider.CreateScope();
					foreach (KaspaBot.Domain.Entities.User user in (await scope2.ServiceProvider.GetRequiredService<IUserRepository>().GetAllAsync()).Where((KaspaBot.Domain.Entities.User u) => u.Settings.IsAutoTradeEnabled && !_activeUsers.Contains(u.Id)).ToList())
					{
						_activeUsers.Add(user.Id);
						Task.Run(() => RunForUser(user, loggerFactory, stoppingToken), stoppingToken);
						_logger.LogInformation($"[DCA-DEBUG] Автоторговля запущена для user={user.Id}");
					}
				}
				catch (Exception value)
				{
					_logger.LogError($"[DCA-DEBUG] Ошибка в основном цикле автоторговли: {value}");
				}
				await Task.Delay(10000, stoppingToken);
			}
		}

		private async Task OnDebounceCompleted(long userId)
		{
			_logger.LogInformation($"[DCA-DEBUG] Получено событие завершения дебаунса для user={userId}");
			using IServiceScope scope = _serviceProvider.CreateScope();
			KaspaBot.Domain.Entities.User user = await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(userId);
			if (user != null && user.Settings.IsAutoTradeEnabled)
			{
				await CreateAutoPairAfterDebounce(user, scope.ServiceProvider);
			}
		}

		private async Task CreateAutoPairAfterDebounce(KaspaBot.Domain.Entities.User user, IServiceProvider serviceProvider)
		{
			SemaphoreSlim userLock = GetUserLock(user.Id);
			if (!(await userLock.WaitAsync(TimeSpan.FromSeconds(5.0))))
			{
				_logger.LogWarning($"[DCA-DEBUG] user={user.Id} Не удалось получить блокировку для покупки после дебаунса");
				return;
			}
			try
			{
				_logger.LogInformation($"[DCA-DEBUG] user={user.Id} Создаем пару после завершения дебаунса");
				await CreateBuySellPair(user, serviceProvider, "после дебаунса");
			}
			finally
			{
				userLock.Release();
			}
		}

		private SemaphoreSlim GetUserLock(long userId)
		{
			return _userLocks.GetOrAdd(userId, (long _) => new SemaphoreSlim(1, 1));
		}

		private async Task CreateBuySellPair(KaspaBot.Domain.Entities.User user, IServiceProvider serviceProvider, string reason)
		{
			ILogger<MexcService> logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<MexcService>();
			MexcService mexcService = MexcService.Create(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, logger);
			OrderPairRepository orderPairRepo = serviceProvider.GetRequiredService<OrderPairRepository>();
			IUserRepository userRepository = serviceProvider.GetRequiredService<IUserRepository>();
			decimal? buyPrice = null;
			Result<decimal> result = await mexcService.GetSymbolPriceAsync("KASUSDT", CancellationToken.None);
			if (result.IsSuccess)
			{
				buyPrice = result.Value;
			}
			Result<MexcAccountInfo> result2 = await mexcService.GetAccountInfoAsync(CancellationToken.None);
			decimal freeUsdt = ((!result2.IsSuccess) ? 0m : (result2.Value.Balances.FirstOrDefault((MexcAccountBalance b) => b.Asset == "USDT")?.Available ?? 0m));
			decimal orderAmount = user.Settings.GetOrderAmount(freeUsdt);
			Order buyOrder = new Order
			{
				Id = string.Empty,
				Symbol = "KASUSDT",
				Side = OrderSide.Buy,
				Type = OrderType.Market,
				Quantity = orderAmount,
				Status = OrderStatus.New,
				CreatedAt = DateTime.UtcNow
			};
			OrderPair orderPair = new OrderPair
			{
				Id = Guid.NewGuid().ToString(),
				UserId = user.Id,
				BuyOrder = buyOrder,
				SellOrder = new Order
				{
					Id = string.Empty,
					Symbol = "KASUSDT",
					Side = OrderSide.Sell,
					Type = OrderType.Limit,
					Quantity = 0m,
					Price = default(decimal),
					Status = OrderStatus.New,
					CreatedAt = DateTime.UtcNow,
					QuantityFilled = 0m,
					QuoteQuantityFilled = 0m,
					Commission = 0m
				},
				CreatedAt = DateTime.UtcNow
			};
			await orderPairRepo.AddAsync(orderPair);
			Result<string> result3 = await mexcService.PlaceOrderAsync("KASUSDT", OrderSide.Buy, OrderType.Market, orderAmount, null, TimeInForce.GoodTillCanceled, CancellationToken.None);
			if (result3.IsSuccess)
			{
				buyOrder.Id = result3.Value;
				buyOrder.Status = OrderStatus.Filled;
				buyOrder.UpdatedAt = DateTime.UtcNow;
				Result<MexcOrder> result4 = await mexcService.GetOrderAsync("KASUSDT", buyOrder.Id, CancellationToken.None);
				_logger.LogInformation($"[BUY-DEBUG] orderId={buyOrder.Id} status={result4.Value.Status} qtyFilled={result4.Value.QuantityFilled} quoteQtyFilled={result4.Value.QuoteQuantityFilled} price={result4.Value.Price} orderAmount={orderAmount} reason={reason}");
				if (result4.IsSuccess)
				{
					buyOrder.QuantityFilled = result4.Value.QuantityFilled;
					buyOrder.QuoteQuantityFilled = result4.Value.QuoteQuantityFilled;
					if (result4.Value.QuantityFilled > 0m && result4.Value.QuoteQuantityFilled > 0m)
					{
						buyOrder.Price = result4.Value.QuoteQuantityFilled / result4.Value.QuantityFilled;
					}
					else
					{
						buyOrder.Price = buyPrice;
					}
				}
				else
				{
					buyOrder.Price = buyPrice;
				}
				orderPair.BuyOrder = buyOrder;
				await orderPairRepo.UpdateAsync(orderPair);
				decimal num = user.Settings.PercentProfit / 100m;
				decimal sellPrice = buyOrder.Price.GetValueOrDefault() * (1m + num);
				decimal num2 = 0.001m;
				decimal num3 = Math.Ceiling(1m / sellPrice / num2) * num2;
				decimal sellQty = buyOrder.QuantityFilled;
				sellQty = ((!(sellQty * sellPrice < 1m)) ? (Math.Floor(sellQty / num2) * num2) : num3);
				Result<string> sellResult = await mexcService.PlaceOrderAsync("KASUSDT", OrderSide.Sell, OrderType.Limit, sellQty, sellPrice, TimeInForce.GoodTillCanceled, CancellationToken.None);
				if (sellResult.IsSuccess)
				{
					orderPair.SellOrder.Id = sellResult.Value;
					orderPair.SellOrder.Price = sellPrice;
					orderPair.SellOrder.Quantity = sellQty;
					orderPair.SellOrder.Status = OrderStatus.New;
					orderPair.SellOrder.CreatedAt = DateTime.UtcNow;
					await orderPairRepo.UpdateAsync(orderPair);
					_logger.LogInformation($"[DCA-DEBUG] user={user.Id} Sell-ордер выставлен: {sellResult.Value} qty={sellQty} price={sellPrice} reason={reason}");
				}
				else
				{
					_logger.LogError($"[DCA-DEBUG] user={user.Id} Ошибка выставления sell-ордера: {string.Join(", ", sellResult.Errors.Select((IError e) => e.Message))}");
				}
				if (buyOrder.Price.HasValue && buyOrder.Price.Value > 0m)
				{
					user.Settings.LastDcaBuyPrice = buyOrder.Price.Value;
					await userRepository.UpdateAsync(user);
					_logger.LogInformation($"[DCA-DEBUG] user={user.Id} LastDcaBuyPrice обновлен: {buyOrder.Price.Value}");
				}
				else
				{
					_logger.LogWarning($"[DCA-DEBUG] user={user.Id} Не удалось обновить LastDcaBuyPrice: buyOrder.Price = {buyOrder.Price}");
				}
				await serviceProvider.GetRequiredService<ITelegramBotClient>().SendMessage(text: NotificationFormatter.AutoBuy(isStartup: reason == "старт автоторговли" || (!user.Settings.LastDcaBuyPrice.HasValue && reason == "включение автоторговли"), buyQty: buyOrder.QuantityFilled, buyPrice: buyOrder.Price.GetValueOrDefault(), sellQty: sellQty, sellPrice: sellPrice, lastBuyPrice: user.Settings.LastDcaBuyPrice), chatId: user.Id, parseMode: ParseMode.Html);
			}
			else
			{
				_logger.LogError($"[DCA-DEBUG] user={user.Id} Ошибка создания пары ({reason}): {string.Join(", ", result3.Errors.Select((IError e) => e.Message))}");
			}
		}

		private async Task RunForUser(KaspaBot.Domain.Entities.User user, ILoggerFactory loggerFactory, CancellationToken stoppingToken)
		{
			ILogger<MexcService> logger = loggerFactory.CreateLogger<MexcService>();
			MexcService mexcService = MexcService.Create(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, logger);
			_logger.LogInformation($"[DCA-DEBUG] user={user.Id} стартует поток автоторговли");
			while (!stoppingToken.IsCancellationRequested)
			{
				try
				{
					using IServiceScope scope = _serviceProvider.CreateScope();
					OrderPairRepository orderPairRepo = scope.ServiceProvider.GetRequiredService<OrderPairRepository>();
					KaspaBot.Domain.Entities.User user2 = await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(user.Id);
					if (user2 == null)
					{
						_logger.LogWarning($"[DCA-DEBUG] user={user.Id} удалён из базы, поток автоторговли завершён");
						_activeUsers.Remove(user.Id);
						break;
					}
					user = user2;
					if (!user.Settings.IsAutoTradeEnabled)
					{
						_logger.LogInformation($"[DCA-DEBUG] user={user.Id} автоторговля выключена");
						await Task.Delay(2000, stoppingToken);
						continue;
					}
					Result<MexcAccountInfo> result = await mexcService.GetAccountInfoAsync(stoppingToken);
					if (!result.IsSuccess)
					{
						_logger.LogWarning($"[DCA-DEBUG] user={user.Id} Не удалось получить баланс, автоторговля пропущена");
						await Task.Delay(2000, stoppingToken);
						continue;
					}
					decimal usdtBalance = result.Value.Balances.FirstOrDefault((MexcAccountBalance b) => b.Asset == "USDT")?.Available ?? 0m;
					decimal orderAmount = user.Settings.GetOrderAmount(usdtBalance);
					if (usdtBalance < orderAmount)
					{
						if (!_lowBalanceWarnedUsers.Contains(user.Id))
						{
							await scope.ServiceProvider.GetRequiredService<ITelegramBotClient>().SendMessage(user.Id, $"Недостаточно USDT для автоторговли. Баланс: {usdtBalance:F2}, требуется: {orderAmount:F2}. Пополните баланс для продолжения DCA.");
							_lowBalanceWarnedUsers.Add(user.Id);
						}
						_logger.LogWarning($"[DCA-DEBUG] user={user.Id} Недостаточно USDT для автоторговли: {usdtBalance} < {orderAmount} (режим: {user.Settings.OrderAmountMode}, коэффициент: {user.Settings.DynamicOrderCoef})");
						await Task.Delay(2000, stoppingToken);
						continue;
					}
					_lowBalanceWarnedUsers.Remove(user.Id);
					decimal? num = (from p in await orderPairRepo.GetAllAsync()
						where p.UserId == user.Id && p.BuyOrder.Status == OrderStatus.Filled
						orderby p.BuyOrder.UpdatedAt ?? p.BuyOrder.CreatedAt descending
						select p).FirstOrDefault()?.BuyOrder.Price ?? user.Settings.LastDcaBuyPrice;
					if (!num.HasValue)
					{
						goto IL_0891;
					}
					decimal? num2 = num;
					if ((num2.GetValueOrDefault() <= default(decimal)) & num2.HasValue)
					{
						goto IL_0891;
					}
					if (!(await mexcService.GetSymbolPriceAsync("KASUSDT", stoppingToken)).IsSuccess)
					{
						goto IL_106a;
					}
					decimal percentChange = user.Settings.PercentPriceChange / 100m;
					SemaphoreSlim userLock = GetUserLock(user.Id);
					if (!(await userLock.WaitAsync(TimeSpan.FromSeconds(5.0), stoppingToken)))
					{
						_logger.LogWarning($"[DCA-DEBUG] user={user.Id} Не удалось получить блокировку для автопокупки, пропускаем итерацию");
						await Task.Delay(2000, stoppingToken);
						continue;
					}
					try
					{
						decimal? lastBuyPrice2 = (from p in await orderPairRepo.GetAllAsync()
							where p.UserId == user.Id && p.BuyOrder.Status == OrderStatus.Filled
							orderby p.BuyOrder.UpdatedAt ?? p.BuyOrder.CreatedAt descending
							select p).FirstOrDefault()?.BuyOrder.Price ?? user.Settings.LastDcaBuyPrice;
						Result<decimal> result2 = await mexcService.GetSymbolPriceAsync("KASUSDT", stoppingToken);
						if (result2.IsSuccess)
						{
							decimal value = result2.Value;
							num2 = lastBuyPrice2 * (decimal?)(1m - percentChange);
							if ((value <= num2.GetValueOrDefault()) & num2.HasValue)
							{
								_logger.LogInformation($"[DCA-DEBUG] user={user.Id} (atomic) Условие автопокупки подтверждено: currentPrice={result2.Value} <= {lastBuyPrice2} * (1 - {percentChange})");
								await CreateBuySellPair(user, scope.ServiceProvider, "падение цены");
							}
						}
					}
					finally
					{
						userLock.Release();
					}
					goto IL_106a;
					IL_0891:
					userLock = GetUserLock(user.Id);
					if (!(await userLock.WaitAsync(TimeSpan.FromSeconds(5.0), stoppingToken)))
					{
						_logger.LogWarning($"[DCA-DEBUG] user={user.Id} Не удалось получить блокировку, пропускаем итерацию");
						await Task.Delay(2000, stoppingToken);
						continue;
					}
					try
					{
						_logger.LogInformation($"[DCA-DEBUG] user={user.Id} Нет lastBuyPrice, создаем первую пару");
						await CreateBuySellPair(user, scope.ServiceProvider, "первая покупка");
					}
					finally
					{
						userLock.Release();
					}
					goto IL_106a;
					IL_106a:
					await Task.Delay(2000, stoppingToken);
				}
				catch (Exception value2)
				{
					_logger.LogError($"[DCA-DEBUG] user={user.Id} Ошибка в автоторговле: {value2}");
					await Task.Delay(5000, stoppingToken);
				}
			}
		}
	}
	public class OrderRecoveryService : IOrderRecoveryService
	{
		private readonly IServiceProvider _serviceProvider;

		private readonly ILogger<OrderRecoveryService> _logger;

		private readonly ITelegramBotClient _botClient;

		public OrderRecoveryService(IServiceProvider serviceProvider, ILogger<OrderRecoveryService> logger, ITelegramBotClient botClient)
		{
			_serviceProvider = serviceProvider;
			_logger = logger;
			_botClient = botClient;
		}

		public async Task RunRecoveryForUser(long userId, CancellationToken stoppingToken)
		{
			_logger.LogWarning($"[ORDER-RECOVERY-DBG] RunRecoveryForUser called for userId={userId}");
			using IServiceScope scope = _serviceProvider.CreateScope();
			OrderPairRepository orderPairRepo = scope.ServiceProvider.GetRequiredService<OrderPairRepository>();
			IUserRepository userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
			ILoggerFactory loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
			List<OrderPair> allPairs = (await orderPairRepo.GetAllAsync()).Where((OrderPair p) => p.UserId == userId).ToList();
			_logger.LogWarning($"[ORDER-RECOVERY-DBG] allPairs.Count={allPairs.Count}");
			foreach (OrderPair item in allPairs)
			{
				_logger.LogWarning($"[ORDER-RECOVERY-DBG] PAIR {item.Id} SellOrder.Id={item.SellOrder.Id} BuyOrder.Id={item.BuyOrder.Id} BuyOrder.QuantityFilled={item.BuyOrder.QuantityFilled} SellOrder.Quantity={item.SellOrder.Quantity}");
			}
			List<OrderPair> emptyBuyPairs = allPairs.Where((OrderPair p) => string.IsNullOrEmpty(p.BuyOrder.Id)).ToList();
			foreach (OrderPair pair in emptyBuyPairs)
			{
				await orderPairRepo.DeleteByIdAsync(pair.Id);
				_logger.LogInformation("[OrderRecovery] Удалён пустой buy-ордер: " + pair.Id);
			}
			allPairs = allPairs.Except(emptyBuyPairs).ToList();
			List<OrderPair> list = allPairs.Where((OrderPair p) => (!string.IsNullOrEmpty(p.BuyOrder.Id) && !IsFinal(p.BuyOrder.Status)) || (!string.IsNullOrEmpty(p.SellOrder.Id) && !IsFinal(p.SellOrder.Status))).ToList();
			foreach (OrderPair pair in list)
			{
				KaspaBot.Domain.Entities.User user = await userRepository.GetByIdAsync(pair.UserId);
				if (user == null)
				{
					continue;
				}
				ILogger<MexcService> logger = loggerFactory.CreateLogger<MexcService>();
				MexcService mexcService = MexcService.Create(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, logger);
				if (!string.IsNullOrEmpty(pair.BuyOrder.Id) && !IsFinal(pair.BuyOrder.Status))
				{
					Result<MexcOrder> result = await mexcService.GetOrderAsync(pair.BuyOrder.Symbol, pair.BuyOrder.Id, stoppingToken);
					if (result.IsSuccess)
					{
						pair.BuyOrder.Status = result.Value.Status;
						pair.BuyOrder.QuantityFilled = result.Value.QuantityFilled;
						pair.BuyOrder.QuoteQuantityFilled = result.Value.QuoteQuantityFilled;
						if (result.Value.OrderType == OrderType.Market && result.Value.Status == OrderStatus.Filled && result.Value.QuantityFilled > 0m && result.Value.QuoteQuantityFilled > 0m)
						{
							pair.BuyOrder.Price = result.Value.QuoteQuantityFilled / result.Value.QuantityFilled;
						}
						else
						{
							pair.BuyOrder.Price = result.Value.Price;
						}
						pair.BuyOrder.UpdatedAt = DateTime.UtcNow;
						await orderPairRepo.UpdateAsync(pair);
					}
				}
				if (!string.IsNullOrEmpty(pair.SellOrder.Id))
				{
					_logger.LogWarning("[ORDER-RECOVERY-DBG] CHECK SellOrder.Id=" + pair.SellOrder.Id + " for pair " + pair.Id);
					_logger.LogWarning("[ORDER-RECOVERY-DBG] Try restore SellOrderId=" + pair.SellOrder.Id);
					Result<MexcOrder> result2 = await mexcService.GetOrderAsync(pair.SellOrder.Symbol, pair.SellOrder.Id, stoppingToken);
					_logger.LogWarning($"[ORDER-RECOVERY-DBG] SellOrderId={pair.SellOrder.Id} IsSuccess={result2.IsSuccess} Errors={string.Join(", ", result2.Errors.Select((IError e) => e.Message))} Raw={JsonSerializer.Serialize(result2.Value)}");
					if (!result2.IsSuccess)
					{
						_logger.LogWarning("[ORDER-RECOVERY-DBG] SellOrderId=" + pair.SellOrder.Id + " GetOrderAsync failed: " + string.Join(", ", result2.Errors.Select((IError e) => e.Message)));
					}
					else
					{
						_logger.LogInformation($"[ORDER-RECOVERY-DBG] SellOrderId={pair.SellOrder.Id} API.Quantity={result2.Value.Quantity} API.Status={result2.Value.Status} API.Price={result2.Value.Price} API.QuantityFilled={result2.Value.QuantityFilled} BEFORE.Quantity={pair.SellOrder.Quantity}");
						pair.SellOrder.Quantity = result2.Value.Quantity;
						pair.SellOrder.Status = result2.Value.Status;
						pair.SellOrder.QuantityFilled = result2.Value.QuantityFilled;
						pair.SellOrder.Price = result2.Value.Price;
						pair.SellOrder.UpdatedAt = DateTime.UtcNow;
						_logger.LogInformation($"[ORDER-RECOVERY-DBG] SellOrderId={pair.SellOrder.Id} AFTER.Quantity={pair.SellOrder.Quantity} Status={pair.SellOrder.Status} Price={pair.SellOrder.Price} QuantityFilled={pair.SellOrder.QuantityFilled}");
					}
				}
				await orderPairRepo.UpdateAsync(pair);
			}
			foreach (OrderPair pair in allPairs.Where((OrderPair p) => !string.IsNullOrEmpty(p.SellOrder.Id)))
			{
				KaspaBot.Domain.Entities.User user2 = await userRepository.GetByIdAsync(pair.UserId);
				if (user2 == null)
				{
					continue;
				}
				ILogger<MexcService> logger2 = loggerFactory.CreateLogger<MexcService>();
				MexcService mexcService2 = MexcService.Create(user2.ApiCredentials.ApiKey, user2.ApiCredentials.ApiSecret, logger2);
				_logger.LogWarning("[ORDER-RECOVERY-DBG] FORCE UPDATE SellOrder.Id=" + pair.SellOrder.Id + " for pair " + pair.Id);
				Result<MexcOrder> result3 = await mexcService2.GetOrderAsync(pair.SellOrder.Symbol, pair.SellOrder.Id, stoppingToken);
				_logger.LogWarning($"[ORDER-RECOVERY-DBG] SellOrderId={pair.SellOrder.Id} IsSuccess={result3.IsSuccess} Errors={string.Join(", ", result3.Errors.Select((IError e) => e.Message))} Raw={JsonSerializer.Serialize(result3.Value)}");
				if (!result3.IsSuccess)
				{
					continue;
				}
				pair.SellOrder.Quantity = result3.Value.Quantity;
				pair.SellOrder.Status = result3.Value.Status;
				pair.SellOrder.QuantityFilled = result3.Value.QuantityFilled;
				pair.SellOrder.Price = result3.Value.Price;
				pair.SellOrder.UpdatedAt = DateTime.UtcNow;
				if (result3.Value.Status == OrderStatus.Filled && !pair.CompletedAt.HasValue)
				{
					decimal quantity = pair.SellOrder.Quantity;
					decimal valueOrDefault = pair.SellOrder.Price.GetValueOrDefault();
					decimal quantityFilled = result3.Value.QuantityFilled;
					decimal price = result3.Value.Price;
					decimal num = Math.Abs((quantityFilled - quantity) / quantity);
					decimal num2 = Math.Abs((price - valueOrDefault) / valueOrDefault);
					if (num > 0.01m || num2 > 0.01m)
					{
						_logger.LogWarning($"[ORDER-RECOVERY] Отклонения в данных ордера {pair.SellOrder.Id}: количество {quantity} → {quantityFilled} (отклонение {num:P2}), цена {valueOrDefault:F6} → {price:F6} (отклонение {num2:P2})");
						continue;
					}
					if (pair.UserId != 130822044)
					{
						_logger.LogInformation($"Изменение статуса ордера\n\ud83d\udc64 Пользователь: {pair.UserId}\n\ud83c\udd94 Ордер: {pair.SellOrder.Id}\n\ud83d\udcca Статус: New → Filled\n\ud83d\udca1 Причина: Ордер исполнен на бирже\n⏰ Время: {DateTime.UtcNow:HH:mm:ss} UTC");
					}
					pair.CompletedAt = DateTime.UtcNow;
					OrderPair orderPair = pair;
					decimal quantity2 = pair.SellOrder.Quantity;
					decimal? price2 = pair.SellOrder.Price;
					decimal? num3 = (decimal?)quantity2 * price2;
					quantity2 = pair.BuyOrder.QuantityFilled;
					price2 = pair.BuyOrder.Price;
					orderPair.Profit = num3 - (decimal?)quantity2 * price2 - (decimal?)pair.BuyOrder.Commission;
					if (pair.UserId != 130822044)
					{
						decimal quantity3 = pair.SellOrder.Quantity;
						decimal valueOrDefault2 = pair.SellOrder.Price.GetValueOrDefault();
						decimal usdt = quantity3 * valueOrDefault2;
						decimal valueOrDefault3 = pair.Profit.GetValueOrDefault();
						string text = NotificationFormatter.Profit(quantity3, valueOrDefault2, usdt, valueOrDefault3);
						await _botClient.SendMessage(pair.UserId, text, ParseMode.Html);
						_logger.LogInformation("[ORDER-RECOVERY] Отправлено сообщение о продаже для пары " + pair.Id);
					}
				}
				else if (result3.Value.Status == OrderStatus.Filled && pair.CompletedAt.HasValue)
				{
					_logger.LogInformation("[ORDER-RECOVERY] Ордер " + pair.SellOrder.Id + " уже был отмечен как завершенный, пропускаем отправку сообщения");
				}
				await orderPairRepo.UpdateAsync(pair);
				_logger.LogInformation($"[ORDER-RECOVERY-DBG] SellOrderId={pair.SellOrder.Id} UPDATED: Quantity={pair.SellOrder.Quantity} Status={pair.SellOrder.Status} Price={pair.SellOrder.Price} QuantityFilled={pair.SellOrder.QuantityFilled}");
			}
			foreach (OrderPair pair2 in allPairs.Where((OrderPair p) => !string.IsNullOrEmpty(p.BuyOrder.Id) && string.IsNullOrEmpty(p.SellOrder.Id)))
			{
				KaspaBot.Domain.Entities.User user3 = await userRepository.GetByIdAsync(pair2.UserId);
				if (user3 == null)
				{
					continue;
				}
				ILogger<MexcService> logger3 = loggerFactory.CreateLogger<MexcService>();
				MexcService mexcService = MexcService.Create(user3.ApiCredentials.ApiKey, user3.ApiCredentials.ApiSecret, logger3);
				decimal sellPrice = pair2.BuyOrder.Price.GetValueOrDefault() * (1m + user3.Settings.PercentProfit / 100m);
				decimal num4 = 0.001m;
				decimal num5 = Math.Ceiling(1m / sellPrice / num4) * num4;
				decimal sellQty = pair2.BuyOrder.QuantityFilled;
				if (sellQty * sellPrice < 1m)
				{
					sellQty = num5;
				}
				Result<IEnumerable<MexcOrder>> result4 = await mexcService.GetOpenOrdersAsync(pair2.BuyOrder.Symbol, stoppingToken);
				if (result4.IsSuccess)
				{
					List<MexcOrder> list2 = result4.Value.Where((MexcOrder o) => o.Side == OrderSide.Sell).ToList();
					foreach (MexcOrder item2 in list2)
					{
						_logger.LogInformation("[ORDER-RECOVERY-SELL-RAW] " + JsonSerializer.Serialize(item2));
					}
					MexcOrder match = list2.FirstOrDefault(delegate(MexcOrder o)
					{
						PropertyInfo property = o.GetType().GetProperty("time");
						if (property == null)
						{
							return false;
						}
						string text4 = property.GetValue(o) as string;
						if (string.IsNullOrEmpty(text4))
						{
							return false;
						}
						return Math.Abs((DateTime.Parse(text4).ToLocalTime() - pair2.BuyOrder.CreatedAt).TotalMinutes) < 30.0 && Math.Abs(o.Quantity - pair2.BuyOrder.QuantityFilled) < 0.01m && Math.Abs(o.Price - sellPrice) < 0.001m;
					});
					if (match != null)
					{
						string text2 = match.GetType().GetProperty("time")?.GetValue(match) as string;
						pair2.SellOrder.Id = match.OrderId;
						pair2.SellOrder.Price = match.Price;
						pair2.SellOrder.Quantity = match.Quantity;
						pair2.SellOrder.Status = match.Status;
						if (!string.IsNullOrEmpty(text2))
						{
							pair2.SellOrder.CreatedAt = DateTime.Parse(text2);
						}
						await orderPairRepo.UpdateAsync(pair2);
						_logger.LogInformation("[OrderRecovery] Привязан найденный sell-ордер " + match.OrderId + " к паре " + pair2.Id);
						continue;
					}
				}
				Result<string> result5 = await mexcService.PlaceOrderAsync(pair2.BuyOrder.Symbol, OrderSide.Sell, OrderType.Limit, sellQty, sellPrice, TimeInForce.GoodTillCanceled, stoppingToken);
				if (result5.IsSuccess && !string.IsNullOrEmpty(result5.Value))
				{
					pair2.SellOrder.Id = result5.Value;
					pair2.SellOrder.Price = sellPrice;
					pair2.SellOrder.Quantity = sellQty;
					pair2.SellOrder.Status = OrderStatus.New;
					pair2.SellOrder.CreatedAt = DateTime.UtcNow;
					await orderPairRepo.UpdateAsync(pair2);
					_logger.LogInformation("[OrderRecovery] Выставлен sell-ордер: " + pair2.SellOrder.Id + " для пары " + pair2.Id);
					Order buyOrder = pair2.BuyOrder;
					Order sellOrder = pair2.SellOrder;
					DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(66, 5);
					defaultInterpolatedStringHandler.AppendLiteral("КУПЛЕНО\n\n");
					defaultInterpolatedStringHandler.AppendFormatted(buyOrder.QuantityFilled, "F2");
					defaultInterpolatedStringHandler.AppendLiteral(" KAS по ");
					defaultInterpolatedStringHandler.AppendFormatted(buyOrder.Price, "F6");
					defaultInterpolatedStringHandler.AppendLiteral(" USDT\n\nПотрачено\n");
					decimal quantityFilled2 = buyOrder.QuantityFilled;
					decimal? price3 = buyOrder.Price;
					defaultInterpolatedStringHandler.AppendFormatted((decimal?)quantityFilled2 * price3, "F8");
					defaultInterpolatedStringHandler.AppendLiteral(" USDT\n\nВЫСТАВЛЕНО\n\n");
					defaultInterpolatedStringHandler.AppendFormatted(sellOrder.Quantity, "F2");
					defaultInterpolatedStringHandler.AppendLiteral(" KAS по ");
					defaultInterpolatedStringHandler.AppendFormatted(sellOrder.Price, "F6");
					defaultInterpolatedStringHandler.AppendLiteral(" USDT");
					string text3 = defaultInterpolatedStringHandler.ToStringAndClear();
					await _botClient.SendMessage(pair2.UserId, text3);
				}
				else
				{
					_logger.LogError("[ORDER-RECOVERY-SELL-ERROR] Не удалось выставить sell-ордер для пары " + pair2.Id + ": " + string.Join(", ", result5.Errors?.Select((IError e) => e.Message) ?? new string[1] { "Unknown error" }));
				}
			}
		}

		private static bool IsFinal(OrderStatus status)
		{
			if (status != OrderStatus.Filled)
			{
				return status == OrderStatus.Canceled;
			}
			return true;
		}
	}
}
namespace KaspaBot.Presentation.Telegram
{
	public class BotMessenger : IBotMessenger
	{
		private readonly ITelegramBotClient _botClient;

		public BotMessenger(ITelegramBotClient botClient)
		{
			_botClient = botClient;
		}

		public async Task SendMessage(long chatId, string text)
		{
			await _botClient.SendMessage(chatId, text);
		}
	}
}
namespace KaspaBot.Presentation.Telegram.CommandHandlers
{
	public class TradingCommandHandler
	{
		private readonly IMediator _mediator;

		private readonly ITelegramBotClient _botClient;

		private readonly ILogger<TradingCommandHandler> _logger;

		private readonly IServiceProvider _serviceProvider;

		public TradingCommandHandler(IMediator mediator, ITelegramBotClient botClient, ILogger<TradingCommandHandler> logger, IServiceProvider serviceProvider)
		{
			_mediator = mediator;
			_botClient = botClient;
			_logger = logger;
			_serviceProvider = serviceProvider;
		}

		[BotCommand("Купить KASUSDT по рынку")]
		public async Task HandleBuyCommand(Message message, CancellationToken cancellationToken)
		{
			long userId = message.Chat.Id;
			using IServiceScope scope = _serviceProvider.CreateScope();
			IUserRepository userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
			OrderPairRepository orderPairRepo = scope.ServiceProvider.GetRequiredService<OrderPairRepository>();
			ILoggerFactory requiredService = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
			ILogger<MexcService> mexcLogger = requiredService.CreateLogger<MexcService>();
			KaspaBot.Domain.Entities.User user = await userRepository.GetByIdAsync(userId);
			CancellationToken cancellationToken2;
			if (user == null)
			{
				ITelegramBotClient botClient = _botClient;
				ChatId chatId = userId;
				cancellationToken2 = cancellationToken;
				await botClient.SendMessage(chatId, "Пользователь не найден.", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			MexcService mexcService = MexcService.Create(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, mexcLogger);
			Result<MexcAccountInfo> result = await mexcService.GetAccountInfoAsync(cancellationToken);
			decimal freeUsdt = ((!result.IsSuccess) ? 0m : (result.Value.Balances.FirstOrDefault((MexcAccountBalance b) => b.Asset == "USDT")?.Available ?? 0m));
			decimal orderAmount = user.Settings.GetOrderAmount(freeUsdt);
			if (orderAmount < 1m)
			{
				orderAmount = 1m;
			}
			string id = Guid.NewGuid().ToString();
			Order buyOrder = new Order
			{
				Id = string.Empty,
				Symbol = "KASUSDT",
				Side = OrderSide.Buy,
				Type = OrderType.Market,
				Quantity = orderAmount,
				Status = OrderStatus.New,
				CreatedAt = DateTime.UtcNow
			};
			OrderPair orderPair = new OrderPair
			{
				Id = id,
				UserId = userId,
				BuyOrder = buyOrder,
				SellOrder = new Order
				{
					Id = string.Empty,
					Symbol = "KASUSDT",
					Side = OrderSide.Sell,
					Type = OrderType.Limit,
					Quantity = 0m,
					Price = default(decimal),
					Status = OrderStatus.New,
					CreatedAt = DateTime.UtcNow,
					QuantityFilled = 0m,
					QuoteQuantityFilled = 0m,
					Commission = 0m
				},
				CreatedAt = DateTime.UtcNow
			};
			await orderPairRepo.AddAsync(orderPair);
			Result<string> result2 = await mexcService.PlaceOrderAsync("KASUSDT", OrderSide.Buy, OrderType.Market, orderAmount, null, TimeInForce.GoodTillCanceled, cancellationToken);
			if (!result2.IsSuccess)
			{
				ITelegramBotClient botClient2 = _botClient;
				ChatId chatId2 = userId;
				string text = "Ошибка покупки: " + result2.Errors.FirstOrDefault()?.Message;
				cancellationToken2 = cancellationToken;
				await botClient2.SendMessage(chatId2, text, ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			buyOrder.Id = result2.Value;
			buyOrder.Status = OrderStatus.Filled;
			buyOrder.UpdatedAt = DateTime.UtcNow;
			Result<MexcOrder> result3 = await mexcService.GetOrderAsync("KASUSDT", buyOrder.Id, cancellationToken);
			if (!result3.IsSuccess)
			{
				_logger.LogError("[ORDER DEBUG] GetOrderAsync failed: orderId=" + buyOrder.Id + ", errors=" + string.Join(", ", result3.Errors.Select((IError e) => e.Message)));
			}
			decimal buyPrice = default(decimal);
			decimal buyQty = default(decimal);
			decimal totalCommission = default(decimal);
			if (result3.IsSuccess)
			{
				buyPrice = ((!(result3.Value.QuantityFilled > 0m) || !(result3.Value.QuoteQuantityFilled > 0m)) ? result3.Value.Price : (result3.Value.QuoteQuantityFilled / result3.Value.QuantityFilled));
				buyQty = result3.Value.QuantityFilled;
				Result<IEnumerable<MexcUserTrade>> result4 = await mexcService.GetOrderTradesAsync("KASUSDT", buyOrder.Id, cancellationToken);
				if (result4.IsSuccess)
				{
					foreach (MexcUserTrade item in result4.Value)
					{
						totalCommission += item.Fee;
						_ = item.FeeAsset;
						if (buyPrice == 0m && item.Price > 0m)
						{
							buyPrice = item.Price;
						}
					}
				}
				else
				{
					_logger.LogError("[ORDER DEBUG] GetOrderTradesAsync failed: orderId=" + buyOrder.Id + ", errors=" + string.Join(", ", result4.Errors.Select((IError e) => e.Message)));
				}
			}
			buyOrder.Price = buyPrice;
			buyOrder.QuantityFilled = buyQty;
			buyOrder.Commission = totalCommission;
			buyOrder.Status = OrderStatus.Filled;
			orderPair.BuyOrder = buyOrder;
			await orderPairRepo.UpdateAsync(orderPair);
			decimal num = await mexcService.GetTickSizeAsync("KASUSDT", cancellationToken);
			decimal num2 = user.Settings.PercentProfit / 100m;
			decimal num3 = buyPrice * (1m + num2);
			string s = (Math.Floor(num3 / num) * num).ToString("F6", CultureInfo.InvariantCulture);
			decimal num4 = 0.001m;
			decimal sellQty = Math.Floor(buyQty / num4) * num4;
			Order sellOrder = new Order
			{
				Id = string.Empty,
				Symbol = "KASUSDT",
				Side = OrderSide.Sell,
				Type = OrderType.Limit,
				Quantity = sellQty,
				Price = decimal.Parse(s, CultureInfo.InvariantCulture),
				Status = OrderStatus.New,
				CreatedAt = DateTime.UtcNow
			};
			try
			{
				Result<string> result5 = await mexcService.PlaceOrderAsync("KASUSDT", OrderSide.Sell, OrderType.Limit, sellQty, sellOrder.Price, TimeInForce.GoodTillCanceled, cancellationToken);
				if (!result5.IsSuccess)
				{
					ITelegramBotClient botClient3 = _botClient;
					ChatId chatId3 = userId;
					string text2 = "Ошибка выставления продажи: " + result5.Errors.FirstOrDefault()?.Message;
					cancellationToken2 = cancellationToken;
					await botClient3.SendMessage(chatId3, text2, ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
					return;
				}
				sellOrder.Id = result5.Value;
				sellOrder.Status = OrderStatus.New;
				orderPair.SellOrder = sellOrder;
				await orderPairRepo.UpdateAsync(orderPair);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, $"Exception при выставлении sell-ордера: buyPrice={buyPrice}, sellOrder.Price={sellOrder.Price}");
				ITelegramBotClient botClient4 = _botClient;
				ChatId chatId4 = userId;
				string text3 = "Ошибка при выставлении ордера: " + ex.Message;
				cancellationToken2 = cancellationToken;
				await botClient4.SendMessage(chatId4, text3, ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			string text4 = $"\ud83c\udfaf <b>Ручная покупка</b>\n\n✅ <b>КУПЛЕНО</b>\n\ud83d\udcca <b>{buyQty:F2} KAS</b> по <b>{buyPrice:F6} USDT</b>\n\n\ud83d\udcb0 <b>Потрачено:</b> <b>{buyQty * buyPrice:F8} USDT</b>\n\n\ud83d\udcc8 <b>ВЫСТАВЛЕНО</b>\n\ud83d\udcca <b>{sellQty:F2} KAS</b> по <b>{sellOrder.Price:F6} USDT</b>";
			ITelegramBotClient botClient5 = _botClient;
			ChatId chatId5 = userId;
			cancellationToken2 = cancellationToken;
			await botClient5.SendMessage(chatId5, text4, ParseMode.Html, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
			KaspaBot.Domain.Entities.User user2 = await userRepository.GetByIdAsync(userId);
			if (user2 != null && user2.Settings.IsAutoTradeEnabled)
			{
				decimal? price = buyOrder.Price;
				if ((price.GetValueOrDefault() > default(decimal)) & price.HasValue)
				{
					user2.Settings.LastDcaBuyPrice = buyOrder.Price;
					await userRepository.UpdateAsync(user2);
				}
			}
		}

		[BotCommand("Продать весь KAS по рынку")]
		public async Task HandleSellCommand(Message message, CancellationToken cancellationToken)
		{
			long userId = message.Chat.Id;
			using IServiceScope scope = _serviceProvider.CreateScope();
			IUserRepository requiredService = scope.ServiceProvider.GetRequiredService<IUserRepository>();
			ILoggerFactory requiredService2 = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
			ILogger<MexcService> mexcLogger = requiredService2.CreateLogger<MexcService>();
			KaspaBot.Domain.Entities.User user = await requiredService.GetByIdAsync(userId);
			if (user == null)
			{
				ITelegramBotClient botClient = _botClient;
				ChatId chatId = userId;
				CancellationToken cancellationToken2 = cancellationToken;
				await botClient.SendMessage(chatId, "Пользователь не найден.", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			MexcService mexcService = MexcService.Create(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, mexcLogger);
			Result<MexcAccountInfo> result = await mexcService.GetAccountInfoAsync(cancellationToken);
			if (!result.IsSuccess)
			{
				ITelegramBotClient botClient2 = _botClient;
				ChatId chatId2 = userId;
				string text = "Ошибка получения баланса: " + result.Errors.FirstOrDefault()?.Message;
				CancellationToken cancellationToken2 = cancellationToken;
				await botClient2.SendMessage(chatId2, text, ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			decimal kasBalance = result.Value.Balances.FirstOrDefault((MexcAccountBalance b) => b.Asset == "KAS")?.Available ?? 0m;
			if (kasBalance <= 0m)
			{
				ITelegramBotClient botClient3 = _botClient;
				ChatId chatId3 = userId;
				CancellationToken cancellationToken2 = cancellationToken;
				await botClient3.SendMessage(chatId3, "Нет свободных KAS для продажи.", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			Result<string> result2 = await mexcService.PlaceOrderAsync("KASUSDT", OrderSide.Sell, OrderType.Market, kasBalance, null, TimeInForce.GoodTillCanceled, cancellationToken);
			if (!result2.IsSuccess)
			{
				ITelegramBotClient botClient4 = _botClient;
				ChatId chatId4 = userId;
				string text2 = "Ошибка продажи: " + result2.Errors.FirstOrDefault()?.Message;
				CancellationToken cancellationToken2 = cancellationToken;
				await botClient4.SendMessage(chatId4, text2, ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
			}
			else
			{
				ITelegramBotClient botClient5 = _botClient;
				ChatId chatId5 = userId;
				string text3 = $"Продано {kasBalance:F2} KAS по рынку.";
				CancellationToken cancellationToken2 = cancellationToken;
				await botClient5.SendMessage(chatId5, text3, ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
			}
		}

		[BotCommand("Продать X KAS по рынку")]
		public async Task HandleSellAmountCommand(Message message, decimal quantity, CancellationToken cancellationToken)
		{
			long userId = message.Chat.Id;
			using IServiceScope scope = _serviceProvider.CreateScope();
			IUserRepository requiredService = scope.ServiceProvider.GetRequiredService<IUserRepository>();
			ILoggerFactory requiredService2 = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
			ILogger<MexcService> mexcLogger = requiredService2.CreateLogger<MexcService>();
			KaspaBot.Domain.Entities.User user = await requiredService.GetByIdAsync(userId);
			if (user == null)
			{
				ITelegramBotClient botClient = _botClient;
				ChatId chatId = userId;
				CancellationToken cancellationToken2 = cancellationToken;
				await botClient.SendMessage(chatId, "Пользователь не найден.", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			MexcService mexcService = MexcService.Create(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, mexcLogger);
			Result<MexcAccountInfo> result = await mexcService.GetAccountInfoAsync(cancellationToken);
			if (!result.IsSuccess)
			{
				ITelegramBotClient botClient2 = _botClient;
				ChatId chatId2 = userId;
				string text = "Ошибка получения баланса: " + result.Errors.FirstOrDefault()?.Message;
				CancellationToken cancellationToken2 = cancellationToken;
				await botClient2.SendMessage(chatId2, text, ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			decimal num = result.Value.Balances.FirstOrDefault((MexcAccountBalance b) => b.Asset == "KAS")?.Available ?? 0m;
			if (quantity <= 0m)
			{
				ITelegramBotClient botClient3 = _botClient;
				ChatId chatId3 = userId;
				CancellationToken cancellationToken2 = cancellationToken;
				await botClient3.SendMessage(chatId3, "Количество для продажи должно быть больше 0.", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			if (num < quantity)
			{
				ITelegramBotClient botClient4 = _botClient;
				ChatId chatId4 = userId;
				string text2 = $"Недостаточно KAS. Баланс: {num:F2}, запрошено: {quantity:F2}.";
				CancellationToken cancellationToken2 = cancellationToken;
				await botClient4.SendMessage(chatId4, text2, ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			Result<string> result2 = await mexcService.PlaceOrderAsync("KASUSDT", OrderSide.Sell, OrderType.Market, quantity, null, TimeInForce.GoodTillCanceled, cancellationToken);
			if (!result2.IsSuccess)
			{
				ITelegramBotClient botClient5 = _botClient;
				ChatId chatId5 = userId;
				string text3 = "Ошибка продажи: " + result2.Errors.FirstOrDefault()?.Message;
				CancellationToken cancellationToken2 = cancellationToken;
				await botClient5.SendMessage(chatId5, text3, ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
			}
			else
			{
				ITelegramBotClient botClient6 = _botClient;
				ChatId chatId6 = userId;
				string text4 = $"Продано {quantity:F2} KAS по рынку.";
				CancellationToken cancellationToken2 = cancellationToken;
				await botClient6.SendMessage(chatId6, text4, ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
			}
		}

		[BotCommand("Статистика активных ордеров")]
		public async Task HandleStatusCommand(Message message, CancellationToken cancellationToken)
		{
			long userId = message.Chat.Id;
			using IServiceScope scope = _serviceProvider.CreateScope();
			OrderPairRepository orderPairRepo = scope.ServiceProvider.GetRequiredService<OrderPairRepository>();
			ILogger<MexcService> mexcLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<MexcService>();
			KaspaBot.Domain.Entities.User user = await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(userId);
			CancellationToken cancellationToken2;
			if (user == null)
			{
				ITelegramBotClient botClient = _botClient;
				ChatId chatId = userId;
				cancellationToken2 = cancellationToken;
				await botClient.SendMessage(chatId, "Пользователь не найден", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			MexcService mexcService = MexcService.Create(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, mexcLogger);
			decimal makerFee = 0.001m;
			decimal takerFee = 0.001m;
			Result<(decimal, decimal)> result = await mexcService.GetTradeFeeAsync("KASUSDT", cancellationToken);
			if (result.IsSuccess)
			{
				makerFee = result.Value.Item1;
				takerFee = result.Value.Item2;
			}
			List<OrderPair> source = await orderPairRepo.GetAllAsync();
			List<OrderPair> list = (from p in source
				where p.UserId == userId
				orderby p.CreatedAt descending
				select p).Take(20).ToList();
			int value = source.Count((OrderPair p) => p.UserId == userId);
			int value2 = source.Count((OrderPair p) => p.UserId == userId && !string.IsNullOrEmpty(p.BuyOrder.Id) && (string.IsNullOrEmpty(p.SellOrder.Id) || p.SellOrder.Status == OrderStatus.New));
			string value3 = (user.Settings.IsAutoTradeEnabled ? "\ud83d\udfe2 автоторговля ВКЛ" : "\ud83d\udd34 автоторговля ВЫКЛ");
			StringBuilder stringBuilder = new StringBuilder();
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder3 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(7, 1, stringBuilder2);
			handler.AppendLiteral("<b>");
			handler.AppendFormatted(value3);
			handler.AppendLiteral("</b>");
			stringBuilder3.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder4 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(33, 2, stringBuilder2);
			handler.AppendLiteral("Комиссия биржи: Maker ");
			handler.AppendFormatted(makerFee * 100m, "F3");
			handler.AppendLiteral("% / Taker ");
			handler.AppendFormatted(takerFee * 100m, "F3");
			handler.AppendLiteral("%");
			stringBuilder4.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder5 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(11, 1, stringBuilder2);
			handler.AppendLiteral("Всего пар: ");
			handler.AppendFormatted(value);
			stringBuilder5.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder6 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(21, 1, stringBuilder2);
			handler.AppendLiteral("Покупок без продажи: ");
			handler.AppendFormatted(value2);
			stringBuilder6.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder7 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(24, 1, stringBuilder2);
			handler.AppendLiteral("Показаны последние ");
			handler.AppendFormatted(list.Count);
			handler.AppendLiteral(" пар\n");
			stringBuilder7.AppendLine(ref handler);
			stringBuilder.AppendLine("<pre>");
			stringBuilder.AppendLine("Дата        | BuyID   | BuyQ  | BuyP     | BuySt | SellID  | SellQ | SellP    | SellSt | Прибыль  | Комиссия | Закрыт   ");
			stringBuilder.AppendLine("------------|---------|-------|----------|-------|---------|------|----------|--------|----------|----------|----------");
			foreach (OrderPair item in list)
			{
				Order buyOrder = item.BuyOrder;
				Order sellOrder = item.SellOrder;
				string value4 = buyOrder.CreatedAt.ToString("dd.MM HH:mm");
				string value5 = "-";
				if (item.CompletedAt.HasValue)
				{
					value5 = item.CompletedAt.Value.ToString("dd.MM HH:mm");
				}
				else if (sellOrder.Status == OrderStatus.Filled && sellOrder.UpdatedAt.HasValue)
				{
					value5 = sellOrder.UpdatedAt.Value.ToString("dd.MM HH:mm");
				}
				string value6 = "-";
				if (item.Profit.HasValue)
				{
					value6 = item.Profit.Value.ToString("F4");
				}
				else if (buyOrder.Status == OrderStatus.Filled && sellOrder.Status == OrderStatus.Filled)
				{
					decimal num = buyOrder.QuantityFilled * buyOrder.Price.GetValueOrDefault();
					value6 = (sellOrder.QuantityFilled * sellOrder.Price.GetValueOrDefault() - num).ToString("F4");
				}
				decimal num2 = default(decimal);
				if (buyOrder.Status == OrderStatus.Filled)
				{
					num2 += buyOrder.Commission;
				}
				if (sellOrder.Status == OrderStatus.Filled)
				{
					num2 += sellOrder.Commission;
				}
				string value7 = num2.ToString("F4");
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder8 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(33, 12, stringBuilder2);
				handler.AppendFormatted<string>(value4, 10);
				handler.AppendLiteral(" | ");
				handler.AppendFormatted<string>(buyOrder.Id.Substring(0, 6), -6);
				handler.AppendLiteral(" | ");
				handler.AppendFormatted(buyOrder.Quantity, 5, "F2");
				handler.AppendLiteral(" | ");
				handler.AppendFormatted(buyOrder.Price, 8, "F4");
				handler.AppendLiteral(" | ");
				handler.AppendFormatted(buyOrder.Status, 5);
				handler.AppendLiteral(" | ");
				handler.AppendFormatted<string>(sellOrder.Id.Substring(0, 6), -6);
				handler.AppendLiteral(" | ");
				handler.AppendFormatted(sellOrder.Quantity, 5, "F2");
				handler.AppendLiteral(" | ");
				handler.AppendFormatted(sellOrder.Price, 8, "F4");
				handler.AppendLiteral(" | ");
				handler.AppendFormatted(sellOrder.Status, 6);
				handler.AppendLiteral(" | ");
				handler.AppendFormatted<string>(value6, 8);
				handler.AppendLiteral(" | ");
				handler.AppendFormatted<string>(value7, 8);
				handler.AppendLiteral(" | ");
				handler.AppendFormatted<string>(value5, 8);
				stringBuilder8.AppendLine(ref handler);
			}
			stringBuilder.AppendLine("</pre>");
			ITelegramBotClient botClient2 = _botClient;
			ChatId chatId2 = userId;
			string text = stringBuilder.ToString();
			cancellationToken2 = cancellationToken;
			await botClient2.SendMessage(chatId2, text, ParseMode.Html, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
		}

		[BotCommand("Таблица открытых ордеров")]
		public async Task HandleStatCommand(Message message, CancellationToken cancellationToken)
		{
			long userId = message.Chat.Id;
			using IServiceScope scope = _serviceProvider.CreateScope();
			OrderPairRepository orderPairRepo = scope.ServiceProvider.GetRequiredService<OrderPairRepository>();
			IUserRepository requiredService = scope.ServiceProvider.GetRequiredService<IUserRepository>();
			ILoggerFactory requiredService2 = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
			ILogger<MexcService> mexcLogger = requiredService2.CreateLogger<MexcService>();
			KaspaBot.Domain.Entities.User user = await requiredService.GetByIdAsync(userId);
			CancellationToken cancellationToken2;
			if (user == null)
			{
				ITelegramBotClient botClient = _botClient;
				ChatId chatId = userId;
				cancellationToken2 = cancellationToken;
				await botClient.SendMessage(chatId, "Пользователь не найден", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			List<Order> sellOrders = (from p in await orderPairRepo.GetAllAsync()
				where p.UserId == userId && !string.IsNullOrEmpty(p.SellOrder.Id) && p.SellOrder.Status != OrderStatus.Filled && p.SellOrder.Quantity > 0m
				select p.SellOrder).ToList();
			if (sellOrders.Count == 0)
			{
				ITelegramBotClient botClient2 = _botClient;
				ChatId chatId2 = userId;
				cancellationToken2 = cancellationToken;
				await botClient2.SendMessage(chatId2, "Нет активных ордеров на продажу", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			decimal currentPrice = default(decimal);
			try
			{
				Result<decimal> result = await MexcService.Create(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, mexcLogger).GetSymbolPriceAsync(sellOrders.First().Symbol, cancellationToken);
				currentPrice = (result.IsSuccess ? result.Value : sellOrders.First().Price.GetValueOrDefault());
			}
			catch
			{
				currentPrice = sellOrders.First().Price.GetValueOrDefault();
			}
			sellOrders = sellOrders.OrderBy((Order order) => Math.Abs(order.Price.GetValueOrDefault() / currentPrice - 1m)).ToList();
			sellOrders.Sum((Order order) => order.Quantity);
			decimal totalSum = sellOrders.Sum((Order order) => order.Quantity * order.Price.GetValueOrDefault());
			List<(int, decimal, decimal, decimal, decimal)> rows = new List<(int, decimal, decimal, decimal, decimal)>();
			int n = sellOrders.Count;
			int mainCount = Math.Min(10, n);
			for (int i = 0; i < mainCount; i++)
			{
				Order o = sellOrders[i];
				decimal sum = o.Quantity * o.Price.GetValueOrDefault();
				decimal deviation = (o.Price.GetValueOrDefault() / currentPrice - 1m) * 100m;
				deviation = Math.Round(deviation, 2);
				deviation = -deviation;
				if (deviation > 0m)
				{
					_logger.LogWarning($"[STAT-DEBUG] user={userId} Sell-ордер с положительным отклонением: OrderId={o.Id}, Price={o.Price:F6}, CurrentPrice={currentPrice:F6}, Deviation={deviation:F2}%. Ордер должен был исполниться!");
					try
					{
						Result<MexcOrder> result2 = await MexcService.Create(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, mexcLogger).GetOrderAsync("KASUSDT", o.Id, cancellationToken);
						if (result2.IsSuccess)
						{
							MexcOrder value = result2.Value;
							_logger.LogWarning($"[STAT-DEBUG] user={userId} OrderId={o.Id} Статус на бирже: {value.Status}, Исполнено: {value.QuantityFilled:F3}/{value.Quantity:F3}");
							if (value.Status == OrderStatus.Filled)
							{
								_logger.LogError($"[STAT-DEBUG] user={userId} OrderId={o.Id} КРИТИЧЕСКАЯ ОШИБКА: Ордер исполнен на бирже, но не обработан в боте!");
							}
						}
						else
						{
							_logger.LogWarning($"[STAT-DEBUG] user={userId} OrderId={o.Id} Не удалось получить статус с биржи: {string.Join(", ", result2.Errors.Select((IError e) => e.Message))}");
						}
					}
					catch (Exception exception)
					{
						_logger.LogError(exception, $"[STAT-DEBUG] user={userId} OrderId={o.Id} Ошибка при проверке статуса ордера");
					}
				}
				rows.Add((i + 1, o.Quantity, o.Price.GetValueOrDefault(), sum, deviation));
			}
			if (n > 11)
			{
				Order o = sellOrders[n - 1];
				decimal deviation = o.Quantity * o.Price.GetValueOrDefault();
				decimal sum = (o.Price.GetValueOrDefault() / currentPrice - 1m) * 100m;
				sum = Math.Round(sum, 2);
				sum = -sum;
				if (sum > 0m)
				{
					_logger.LogWarning($"[STAT-DEBUG] user={userId} Sell-ордер с положительным отклонением: OrderId={o.Id}, Price={o.Price:F6}, CurrentPrice={currentPrice:F6}, Deviation={sum:F2}%. Ордер должен был исполниться!");
					try
					{
						Result<MexcOrder> result3 = await MexcService.Create(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, mexcLogger).GetOrderAsync("KASUSDT", o.Id, cancellationToken);
						if (result3.IsSuccess)
						{
							MexcOrder value2 = result3.Value;
							_logger.LogWarning($"[STAT-DEBUG] user={userId} OrderId={o.Id} Статус на бирже: {value2.Status}, Исполнено: {value2.QuantityFilled:F3}/{value2.Quantity:F3}");
							if (value2.Status == OrderStatus.Filled)
							{
								_logger.LogError($"[STAT-DEBUG] user={userId} OrderId={o.Id} КРИТИЧЕСКАЯ ОШИБКА: Ордер исполнен на бирже, но не обработан в боте!");
							}
						}
						else
						{
							_logger.LogWarning($"[STAT-DEBUG] user={userId} OrderId={o.Id} Не удалось получить статус с биржи: {string.Join(", ", result3.Errors.Select((IError e) => e.Message))}");
						}
					}
					catch (Exception exception2)
					{
						_logger.LogError(exception2, $"[STAT-DEBUG] user={userId} OrderId={o.Id} Ошибка при проверке статуса ордера");
					}
				}
				rows.Add((n, o.Quantity, o.Price.GetValueOrDefault(), deviation, sum));
			}
			string autotradeStatus = (user.Settings.IsAutoTradeEnabled ? "\ud83d\udfe2 автоторговля ВКЛ" : "\ud83d\udd34 автоторговля ВЫКЛ");
			string autoBuyInfo = "";
			if (user.Settings.IsAutoTradeEnabled)
			{
				decimal? lastDcaBuyPrice = user.Settings.LastDcaBuyPrice;
				if ((lastDcaBuyPrice.GetValueOrDefault() > default(decimal)) & lastDcaBuyPrice.HasValue)
				{
					decimal? lastDcaBuyPrice2 = user.Settings.LastDcaBuyPrice;
					decimal num = user.Settings.PercentPriceChange / 100m;
					decimal? num2 = lastDcaBuyPrice2 * (decimal?)(1m - num);
					decimal? value3 = ((decimal?)currentPrice / num2 - (decimal?)1m) * (decimal?)100m;
					autoBuyInfo = $"\n\ud83d\udcc9 До автопокупки: {value3:F2}% (реальная цель: {num2:F6})";
				}
			}
			string text = NotificationFormatter.StatTable(rows, totalSum, currentPrice, autotradeStatus, autoBuyInfo, sellOrders.Count);
			ITelegramBotClient botClient3 = _botClient;
			ChatId chatId3 = userId;
			cancellationToken2 = cancellationToken;
			await botClient3.SendMessage(chatId3, text, ParseMode.Html, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
		}

		[BotCommand("Таблица профита")]
		public async Task HandleProfitCommand(Message message, CancellationToken cancellationToken)
		{
			long userId = message.Chat.Id;
			using IServiceScope scope = _serviceProvider.CreateScope();
			List<OrderPair> list = (await scope.ServiceProvider.GetRequiredService<OrderPairRepository>().GetAllAsync()).Where((OrderPair p) => p.UserId == userId && p.CompletedAt.HasValue && p.Profit.HasValue).ToList();
			CancellationToken cancellationToken2;
			if (list.Count == 0)
			{
				ITelegramBotClient botClient = _botClient;
				ChatId chatId = userId;
				cancellationToken2 = cancellationToken;
				await botClient.SendMessage(chatId, "Нет завершённых сделок.", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			DateTime date = DateTime.UtcNow.Date;
			DateTime dateTime = date.AddDays(-1.0);
			DateTime weekAgo = date.AddDays(-7.0);
			List<IGrouping<DateTime, OrderPair>> list2 = (from p in list
				group p by p.CompletedAt.Value.Date into g
				orderby g.Key descending
				select g).Take(7).ToList();
			IEnumerable<OrderPair> source = list.Where((OrderPair p) => p.CompletedAt.Value.Date > weekAgo);
			decimal allProfit = list.Sum((OrderPair p) => p.Profit.GetValueOrDefault());
			int count = list.Count;
			decimal weekProfit = source.Sum((OrderPair p) => p.Profit.GetValueOrDefault());
			int weekCount = source.Count();
			List<(string, decimal, int)> list3 = new List<(string, decimal, int)>();
			foreach (IGrouping<DateTime, OrderPair> item4 in list2)
			{
				string item = ((item4.Key == dateTime) ? "вчера" : item4.Key.ToString("dd.MM"));
				decimal item2 = item4.Sum((OrderPair p) => p.Profit.GetValueOrDefault());
				int item3 = item4.Count();
				list3.Add((item, item2, item3));
			}
			string text = NotificationFormatter.ProfitTable(list3, weekProfit, weekCount, allProfit, count);
			ITelegramBotClient botClient2 = _botClient;
			ChatId chatId2 = userId;
			cancellationToken2 = cancellationToken;
			await botClient2.SendMessage(chatId2, text, ParseMode.Html, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
		}

		[BotCommand("Сводка по комиссиям")]
		public async Task HandleFeeCommand(Message message, CancellationToken cancellationToken)
		{
			long userId = message.Chat.Id;
			using IServiceScope scope = _serviceProvider.CreateScope();
			List<OrderPair> list = (await scope.ServiceProvider.GetRequiredService<OrderPairRepository>().GetAllAsync()).Where((OrderPair p) => p.UserId == userId && p.BuyOrder.Status == OrderStatus.Filled && p.SellOrder.Status == OrderStatus.Filled).ToList();
			CancellationToken cancellationToken2;
			if (!list.Any())
			{
				ITelegramBotClient botClient = _botClient;
				ChatId chatId = userId;
				cancellationToken2 = cancellationToken;
				await botClient.SendMessage(chatId, "Нет завершённых сделок для анализа.", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			decimal num = list.Sum((OrderPair p) => p.BuyOrder.Commission + p.SellOrder.Commission);
			decimal num2 = list.Sum((OrderPair p) => p.Profit.GetValueOrDefault());
			int count = list.Count;
			decimal value = ((count > 0) ? (num / (decimal)count) : 0m);
			decimal value2 = ((num2 > 0m) ? (num / num2 * 100m) : 0m);
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("\ud83d\udcb8 <b>Сводка по комиссиям</b>\n");
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder3 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(28, 1, stringBuilder2);
			handler.AppendLiteral("Всего комиссий: <b>");
			handler.AppendFormatted(num, "F6");
			handler.AppendLiteral(" USDT</b>");
			stringBuilder3.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder4 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(27, 1, stringBuilder2);
			handler.AppendLiteral("Всего профита: <b>");
			handler.AppendFormatted(num2, "F6");
			handler.AppendLiteral(" USDT</b>");
			stringBuilder4.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder5 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(25, 1, stringBuilder2);
			handler.AppendLiteral("Комиссия/Профит: <b>");
			handler.AppendFormatted(value2, "F1");
			handler.AppendLiteral("%</b>");
			stringBuilder5.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder6 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(15, 1, stringBuilder2);
			handler.AppendLiteral("Сделок: <b>");
			handler.AppendFormatted(count);
			handler.AppendLiteral("</b>");
			stringBuilder6.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder7 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(30, 1, stringBuilder2);
			handler.AppendLiteral("Средняя комиссия: <b>");
			handler.AppendFormatted(value, "F6");
			handler.AppendLiteral(" USDT</b>");
			stringBuilder7.AppendLine(ref handler);
			ITelegramBotClient botClient2 = _botClient;
			ChatId chatId2 = userId;
			string text = stringBuilder.ToString();
			cancellationToken2 = cancellationToken;
			await botClient2.SendMessage(chatId2, text, ParseMode.Html, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
		}

		[BotCommand("Дамп ордера", AdminOnly = true)]
		public async Task HandleRestCommand(Message message, CancellationToken cancellationToken)
		{
			long userId = message.Chat.Id;
			string[] array = (message.Text?.Trim())?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (array == null || array.Length < 2)
			{
				ITelegramBotClient botClient = _botClient;
				ChatId chatId = userId;
				CancellationToken cancellationToken2 = cancellationToken;
				await botClient.SendMessage(chatId, "Используй: /rest {orderId}", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			string orderId = array[1];
			using IServiceScope scope = _serviceProvider.CreateScope();
			IUserRepository requiredService = scope.ServiceProvider.GetRequiredService<IUserRepository>();
			ILoggerFactory requiredService2 = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
			ILogger<MexcService> mexcLogger = requiredService2.CreateLogger<MexcService>();
			KaspaBot.Domain.Entities.User user = await requiredService.GetByIdAsync(userId);
			CancellationToken cancellationToken2;
			if (user == null)
			{
				ITelegramBotClient botClient2 = _botClient;
				ChatId chatId2 = userId;
				cancellationToken2 = cancellationToken;
				await botClient2.SendMessage(chatId2, "Пользователь не найден", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			Result<MexcOrder> result = await MexcService.Create(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, mexcLogger).GetOrderAsync("KASUSDT", orderId, cancellationToken);
			string text = JsonSerializer.Serialize(result);
			mexcLogger.LogError("[REST CMD RAW] " + text);
			ITelegramBotClient botClient3 = _botClient;
			ChatId chatId3 = userId;
			string text2 = (result.IsSuccess ? "Данные ордера залогированы" : ("Ошибка: " + string.Join(", ", result.Errors.Select((IError e) => e.Message))));
			cancellationToken2 = cancellationToken;
			await botClient3.SendMessage(chatId3, text2, ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
		}

		[BotCommand("Проверить статусы ордеров", AdminOnly = true)]
		public async Task HandleCheckOrdersCommand(Message message, CancellationToken cancellationToken)
		{
			long userId = message.Chat.Id;
			_logger.LogInformation($"[CHECK-ORDERS] user={userId} старт проверки ордеров");
			using IServiceScope scope = _serviceProvider.CreateScope();
			OrderPairRepository orderPairRepo = scope.ServiceProvider.GetRequiredService<OrderPairRepository>();
			ILoggerFactory requiredService = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
			ILogger<MexcService> mexcLogger = requiredService.CreateLogger<MexcService>();
			KaspaBot.Domain.Entities.User user = await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(userId);
			CancellationToken cancellationToken2;
			if (user == null)
			{
				ITelegramBotClient botClient = _botClient;
				ChatId chatId = userId;
				cancellationToken2 = cancellationToken;
				await botClient.SendMessage(chatId, "Пользователь не найден", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			MexcService mexcService = MexcService.Create(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, mexcLogger);
			List<OrderPair> allPairs = await orderPairRepo.GetAllAsync();
			List<OrderPair> list = allPairs.Where((OrderPair p) => p.UserId == userId && !string.IsNullOrEmpty(p.BuyOrder.Id)).ToList();
			int checkedCount = 0;
			int mismatchCount = 0;
			int fixedCount = 0;
			StringBuilder sb = new StringBuilder();
			foreach (OrderPair pair in list)
			{
				Result<MexcOrder> result = await mexcService.GetOrderAsync("KASUSDT", pair.BuyOrder.Id, cancellationToken);
				checkedCount++;
				if (result.IsSuccess)
				{
					MexcOrder value = result.Value;
					bool flag = false;
					if (pair.BuyOrder.QuantityFilled != value.QuantityFilled)
					{
						StringBuilder stringBuilder = sb;
						StringBuilder stringBuilder2 = stringBuilder;
						StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(34, 3, stringBuilder);
						handler.AppendLiteral("Пара ");
						handler.AppendFormatted(pair.Id);
						handler.AppendLiteral(": QuantityFilled база=");
						handler.AppendFormatted(pair.BuyOrder.QuantityFilled);
						handler.AppendLiteral(" биржа=");
						handler.AppendFormatted(value.QuantityFilled);
						stringBuilder2.AppendLine(ref handler);
						pair.BuyOrder.QuantityFilled = value.QuantityFilled;
						flag = true;
					}
					if (pair.BuyOrder.QuoteQuantityFilled != value.QuoteQuantityFilled)
					{
						StringBuilder stringBuilder = sb;
						StringBuilder stringBuilder3 = stringBuilder;
						StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(39, 3, stringBuilder);
						handler.AppendLiteral("Пара ");
						handler.AppendFormatted(pair.Id);
						handler.AppendLiteral(": QuoteQuantityFilled база=");
						handler.AppendFormatted(pair.BuyOrder.QuoteQuantityFilled);
						handler.AppendLiteral(" биржа=");
						handler.AppendFormatted(value.QuoteQuantityFilled);
						stringBuilder3.AppendLine(ref handler);
						pair.BuyOrder.QuoteQuantityFilled = value.QuoteQuantityFilled;
						flag = true;
					}
					decimal num = ((value.QuantityFilled > 0m && value.QuoteQuantityFilled > 0m) ? (value.QuoteQuantityFilled / value.QuantityFilled) : value.Price);
					decimal? price = pair.BuyOrder.Price;
					decimal num2 = num;
					if (!((price.GetValueOrDefault() == num2) & price.HasValue))
					{
						StringBuilder stringBuilder = sb;
						StringBuilder stringBuilder4 = stringBuilder;
						StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(25, 3, stringBuilder);
						handler.AppendLiteral("Пара ");
						handler.AppendFormatted(pair.Id);
						handler.AppendLiteral(": Price база=");
						handler.AppendFormatted(pair.BuyOrder.Price);
						handler.AppendLiteral(" биржа=");
						handler.AppendFormatted(num);
						stringBuilder4.AppendLine(ref handler);
						pair.BuyOrder.Price = num;
						flag = true;
					}
					if (pair.BuyOrder.Status != value.Status)
					{
						StringBuilder stringBuilder = sb;
						StringBuilder stringBuilder5 = stringBuilder;
						StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(26, 3, stringBuilder);
						handler.AppendLiteral("Пара ");
						handler.AppendFormatted(pair.Id);
						handler.AppendLiteral(": Status база=");
						handler.AppendFormatted(pair.BuyOrder.Status);
						handler.AppendLiteral(" биржа=");
						handler.AppendFormatted(value.Status);
						stringBuilder5.AppendLine(ref handler);
						pair.BuyOrder.Status = value.Status;
						flag = true;
					}
					if (flag)
					{
						mismatchCount++;
						await orderPairRepo.UpdateAsync(pair);
						fixedCount++;
					}
				}
				else
				{
					StringBuilder stringBuilder = sb;
					StringBuilder stringBuilder6 = stringBuilder;
					StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(31, 2, stringBuilder);
					handler.AppendLiteral("Пара ");
					handler.AppendFormatted(pair.Id);
					handler.AppendLiteral(": Ошибка запроса к бирже: ");
					handler.AppendFormatted(string.Join(", ", result.Errors.Select((IError e) => e.Message)));
					stringBuilder6.AppendLine(ref handler);
				}
			}
			Result<IEnumerable<MexcOrder>> result2 = await mexcService.GetOpenOrdersAsync("KASUSDT", cancellationToken);
			HashSet<string> allSellOrderIds = (from p in allPairs
				select p.SellOrder.Id into id
				where !string.IsNullOrEmpty(id)
				select id).ToHashSet();
			int restored = 0;
			int cancelled = 0;
			if (result2.IsSuccess)
			{
				foreach (MexcOrder sellOrder in result2.Value.Where((MexcOrder o) => o.Side == OrderSide.Sell))
				{
					if (allSellOrderIds.Contains(sellOrder.OrderId))
					{
						continue;
					}
					string text = sellOrder.GetType().GetProperty("time")?.GetValue(sellOrder) as string;
					DateTime? orderTime = null;
					if (!string.IsNullOrEmpty(text))
					{
						orderTime = DateTime.Parse(text).ToLocalTime();
					}
					OrderPair pair = allPairs.FirstOrDefault((OrderPair p) => orderTime.HasValue && Math.Abs((orderTime.Value - p.BuyOrder.CreatedAt).TotalMinutes) < 30.0 && Math.Abs(sellOrder.Quantity - p.BuyOrder.QuantityFilled) < 0.01m);
					StringBuilder stringBuilder;
					StringBuilder.AppendInterpolatedStringHandler handler;
					if (pair != null)
					{
						if (!string.IsNullOrEmpty(pair.SellOrder.Id))
						{
							Result<bool> result3 = await mexcService.CancelOrderAsync("KASUSDT", sellOrder.OrderId, cancellationToken);
							stringBuilder = sb;
							StringBuilder stringBuilder7 = stringBuilder;
							handler = new StringBuilder.AppendInterpolatedStringHandler(38, 3, stringBuilder);
							handler.AppendLiteral("Лишний sell-ордер ");
							handler.AppendFormatted(sellOrder.OrderId);
							handler.AppendLiteral(" отменён для пары ");
							handler.AppendFormatted(pair.Id);
							handler.AppendLiteral(": ");
							handler.AppendFormatted(result3.IsSuccess ? "OK" : string.Join(", ", result3.Errors.Select((IError e) => e.Message)));
							stringBuilder7.AppendLine(ref handler);
							cancelled++;
							continue;
						}
						pair.SellOrder.Id = sellOrder.OrderId;
						pair.SellOrder.Price = sellOrder.Price;
						pair.SellOrder.Quantity = sellOrder.Quantity;
						pair.SellOrder.Status = sellOrder.Status;
						if (orderTime.HasValue)
						{
							pair.SellOrder.CreatedAt = orderTime.Value;
						}
						await orderPairRepo.UpdateAsync(pair);
						stringBuilder = sb;
						StringBuilder stringBuilder8 = stringBuilder;
						handler = new StringBuilder.AppendInterpolatedStringHandler(28, 2, stringBuilder);
						handler.AppendLiteral("Sell-ордер ");
						handler.AppendFormatted(sellOrder.OrderId);
						handler.AppendLiteral(" привязан к паре ");
						handler.AppendFormatted(pair.Id);
						stringBuilder8.AppendLine(ref handler);
						restored++;
						continue;
					}
					decimal value2 = sellOrder.Quantity * sellOrder.Price;
					List<string> list2 = (from p in allPairs
						where Math.Abs(sellOrder.Quantity - p.BuyOrder.QuantityFilled) < 0.0001m
						select $"Pair {p.Id}: BuyQty={p.BuyOrder.QuantityFilled}, BuyAt={p.BuyOrder.CreatedAt:HH:mm:ss}, BuyId={p.BuyOrder.Id}").ToList();
					List<string> values = ((list2.Count <= 0) ? (from x in (from p in allPairs
							select new
							{
								Pair = p,
								QtyDiff = Math.Abs(sellOrder.Quantity - p.BuyOrder.QuantityFilled)
							} into x
							orderby x.QtyDiff
							select x).Take(3)
						select $"Pair {x.Pair.Id}: BuyQty={x.Pair.BuyOrder.QuantityFilled}, BuyAt={x.Pair.BuyOrder.CreatedAt:HH:mm:ss}, BuyId={x.Pair.BuyOrder.Id}, QtyDiff={x.QtyDiff:F4}").ToList() : list2);
					stringBuilder = sb;
					StringBuilder stringBuilder9 = stringBuilder;
					handler = new StringBuilder.AppendInterpolatedStringHandler(96, 3, stringBuilder);
					handler.AppendLiteral("Sell-ордер ");
					handler.AppendFormatted(sellOrder.OrderId);
					handler.AppendLiteral(" не удалось привязать ни к одной паре, сумма: ");
					handler.AppendFormatted(value2, "F4");
					handler.AppendLiteral(" USDT. Совпадающие пары по количеству:\n");
					handler.AppendFormatted(string.Join("\n", values));
					stringBuilder9.AppendLine(ref handler);
				}
			}
			string text2 = $"Проверено ордеров: {checkedCount}\nНайдено расхождений: {mismatchCount}\nИсправлено: {fixedCount}\nВосстановлено sell-ордеров: {restored}\nОтменено лишних sell-ордеров: {cancelled}\n" + sb.ToString();
			if (text2.Length > 3500)
			{
				text2 = text2.Substring(0, 3500) + "\n... (обрезано)";
			}
			ITelegramBotClient botClient2 = _botClient;
			ChatId chatId2 = userId;
			string text3 = text2;
			cancellationToken2 = cancellationToken;
			await botClient2.SendMessage(chatId2, text3, ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
		}

		[BotCommand("Отменить ордера", AdminOnly = true)]
		public async Task HandleCancelOrdersCommand(Message message, CancellationToken cancellationToken)
		{
			long userId = message.Chat.Id;
			string[] array = (message.Text?.Trim())?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			string mode = ((array != null && array.Length > 1) ? array[1].ToLower() : "all");
			using IServiceScope scope = _serviceProvider.CreateScope();
			OrderPairRepository orderPairRepo = scope.ServiceProvider.GetRequiredService<OrderPairRepository>();
			ILoggerFactory requiredService = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
			ILogger<MexcService> mexcLogger = requiredService.CreateLogger<MexcService>();
			KaspaBot.Domain.Entities.User user = await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(userId);
			CancellationToken cancellationToken2;
			if (user == null)
			{
				ITelegramBotClient botClient = _botClient;
				ChatId chatId = userId;
				cancellationToken2 = cancellationToken;
				await botClient.SendMessage(chatId, "Пользователь не найден", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			MexcService mexcService = MexcService.Create(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, mexcLogger);
			Result<IEnumerable<MexcOrder>> openOrdersResult = await mexcService.GetOpenOrdersAsync("KASUSDT", cancellationToken);
			if (!openOrdersResult.IsSuccess)
			{
				ITelegramBotClient botClient2 = _botClient;
				ChatId chatId2 = userId;
				cancellationToken2 = cancellationToken;
				await botClient2.SendMessage(chatId2, "Ошибка получения открытых ордеров", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			HashSet<string> botOrderIds = (from id in (await orderPairRepo.GetAllAsync()).SelectMany((OrderPair p) => new string[2]
				{
					p.BuyOrder.Id,
					p.SellOrder.Id
				})
				where !string.IsNullOrEmpty(id)
				select id).ToHashSet();
			int cancelled = 0;
			int failed = 0;
			StringBuilder sb = new StringBuilder();
			foreach (MexcOrder order in openOrdersResult.Value)
			{
				if (mode == "bot" && !botOrderIds.Contains(order.OrderId))
				{
					continue;
				}
				Result<bool> result = await mexcService.CancelOrderAsync("KASUSDT", order.OrderId, cancellationToken);
				if (result.IsSuccess)
				{
					StringBuilder stringBuilder = sb;
					StringBuilder stringBuilder2 = stringBuilder;
					StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(20, 4, stringBuilder);
					handler.AppendLiteral("Отменён ордер ");
					handler.AppendFormatted(order.OrderId);
					handler.AppendLiteral(" ");
					handler.AppendFormatted(order.Side);
					handler.AppendLiteral(" ");
					handler.AppendFormatted(order.Quantity);
					handler.AppendLiteral(" по ");
					handler.AppendFormatted(order.Price);
					stringBuilder2.AppendLine(ref handler);
					cancelled++;
				}
				else
				{
					StringBuilder stringBuilder = sb;
					StringBuilder stringBuilder3 = stringBuilder;
					StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(23, 2, stringBuilder);
					handler.AppendLiteral("Ошибка отмены ордера ");
					handler.AppendFormatted(order.OrderId);
					handler.AppendLiteral(": ");
					handler.AppendFormatted(string.Join(", ", result.Errors.Select((IError e) => e.Message)));
					stringBuilder3.AppendLine(ref handler);
					failed++;
				}
			}
			string text = $"Отменено ордеров: {cancelled}\nОшибок: {failed}\n" + sb.ToString();
			if (text.Length > 3500)
			{
				text = text.Substring(0, 3500) + "\n... (обрезано)";
			}
			ITelegramBotClient botClient3 = _botClient;
			ChatId chatId3 = userId;
			string text2 = text;
			cancellationToken2 = cancellationToken;
			await botClient3.SendMessage(chatId3, text2, ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
		}

		[BotCommand("Закрыть все открытые ордера")]
		public async Task HandleCloseAllOrdersCommand(Message message, CancellationToken cancellationToken)
		{
			long userId = message.Chat.Id;
			using IServiceScope scope = _serviceProvider.CreateScope();
			IUserRepository requiredService = scope.ServiceProvider.GetRequiredService<IUserRepository>();
			ILoggerFactory requiredService2 = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
			ILogger<MexcService> mexcLogger = requiredService2.CreateLogger<MexcService>();
			KaspaBot.Domain.Entities.User user = await requiredService.GetByIdAsync(userId);
			CancellationToken cancellationToken2;
			if (user == null)
			{
				ITelegramBotClient botClient = _botClient;
				ChatId chatId = userId;
				cancellationToken2 = cancellationToken;
				await botClient.SendMessage(chatId, "❌ Пользователь не найден", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			MexcService mexcService = MexcService.Create(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, mexcLogger);
			Result<IEnumerable<MexcOrder>> result = await mexcService.GetOpenOrdersAsync("KASUSDT", cancellationToken);
			if (!result.IsSuccess)
			{
				ITelegramBotClient botClient2 = _botClient;
				ChatId chatId2 = userId;
				string text = "❌ Ошибка получения открытых ордеров: " + result.Errors.FirstOrDefault()?.Message;
				cancellationToken2 = cancellationToken;
				await botClient2.SendMessage(chatId2, text, ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			IEnumerable<MexcOrder> value = result.Value;
			if (!value.Any())
			{
				ITelegramBotClient botClient3 = _botClient;
				ChatId chatId3 = userId;
				cancellationToken2 = cancellationToken;
				await botClient3.SendMessage(chatId3, "✅ У вас нет открытых ордеров", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			int cancelled = 0;
			int failed = 0;
			StringBuilder sb = new StringBuilder();
			sb.AppendLine("\ud83d\udd04 <b>Закрытие открытых ордеров</b>\n");
			foreach (MexcOrder order in value)
			{
				Result<bool> result2 = await mexcService.CancelOrderAsync("KASUSDT", order.OrderId, cancellationToken);
				if (result2.IsSuccess)
				{
					StringBuilder stringBuilder = sb;
					StringBuilder stringBuilder2 = stringBuilder;
					StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(16, 1, stringBuilder);
					handler.AppendLiteral("✅ Отменён ордер ");
					handler.AppendFormatted(order.OrderId);
					stringBuilder2.AppendLine(ref handler);
					stringBuilder = sb;
					StringBuilder stringBuilder3 = stringBuilder;
					handler = new StringBuilder.AppendInterpolatedStringHandler(17, 3, stringBuilder);
					handler.AppendLiteral("   ");
					handler.AppendFormatted(order.Side);
					handler.AppendLiteral(" ");
					handler.AppendFormatted(order.Quantity);
					handler.AppendLiteral(" KAS по ");
					handler.AppendFormatted(order.Price);
					handler.AppendLiteral(" USDT");
					stringBuilder3.AppendLine(ref handler);
					cancelled++;
				}
				else
				{
					StringBuilder stringBuilder = sb;
					StringBuilder stringBuilder4 = stringBuilder;
					StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(23, 1, stringBuilder);
					handler.AppendLiteral("❌ Ошибка отмены ордера ");
					handler.AppendFormatted(order.OrderId);
					stringBuilder4.AppendLine(ref handler);
					stringBuilder = sb;
					StringBuilder stringBuilder5 = stringBuilder;
					handler = new StringBuilder.AppendInterpolatedStringHandler(3, 1, stringBuilder);
					handler.AppendLiteral("   ");
					handler.AppendFormatted(string.Join(", ", result2.Errors.Select((IError e) => e.Message)));
					stringBuilder5.AppendLine(ref handler);
					failed++;
				}
				sb.AppendLine();
			}
			string text2 = $"\ud83d\udcca <b>Результат:</b>\n✅ Отменено: {cancelled}\n❌ Ошибок: {failed}\n\n" + sb.ToString();
			if (text2.Length > 4000)
			{
				text2 = text2.Substring(0, 4000) + "\n... (сообщение обрезано)";
			}
			ITelegramBotClient botClient4 = _botClient;
			ChatId chatId4 = userId;
			string text3 = text2;
			cancellationToken2 = cancellationToken;
			await botClient4.SendMessage(chatId4, text3, ParseMode.Html, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
		}

		[BotCommand("Удалить пользователя", AdminOnly = true, UserIdParameter = "userId")]
		public async Task HandleWipeUserCommand(Message message, CancellationToken cancellationToken, long targetUserId = 0L)
		{
			long adminUserId = message.Chat.Id;
			long userId = ((targetUserId > 0) ? targetUserId : adminUserId);
			_logger.LogWarning($"[WIPE-DEBUG] Админ {adminUserId} удаляет пользователя {userId}");
			using IServiceScope scope = _serviceProvider.CreateScope();
			IUserRepository userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
			OrderPairRepository orderPairRepo = scope.ServiceProvider.GetRequiredService<OrderPairRepository>();
			CancellationToken cancellationToken2;
			if (await userRepository.GetByIdAsync(userId) == null)
			{
				ITelegramBotClient botClient = _botClient;
				ChatId chatId = adminUserId;
				string text = $"❌ Пользователь {userId} не найден.";
				cancellationToken2 = cancellationToken;
				await botClient.SendMessage(chatId, text, ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			await orderPairRepo.DeleteByUserId(userId);
			_logger.LogWarning($"[WIPE-DEBUG] Удаление ордеров пользователя {userId} завершено");
			await userRepository.DeleteAsync(userId);
			_logger.LogWarning($"[WIPE-DEBUG] Пользователь {userId} удалён");
			ITelegramBotClient botClient2 = _botClient;
			ChatId chatId2 = adminUserId;
			string text2 = $"✅ Пользователь {userId} и все сделки удалены.";
			cancellationToken2 = cancellationToken;
			await botClient2.SendMessage(chatId2, text2, ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
		}

		[BotCommand("Ручной запуск восстановления ордеров", AdminOnly = true)]
		public async Task HandleOrderRecoveryCommand(Message message, CancellationToken cancellationToken)
		{
			long userId = message.Chat.Id;
			using IServiceScope scope = _serviceProvider.CreateScope();
			IUserRepository requiredService = scope.ServiceProvider.GetRequiredService<IUserRepository>();
			OrderPairRepository orderPairRepo = scope.ServiceProvider.GetRequiredService<OrderPairRepository>();
			ILoggerFactory requiredService2 = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
			ILogger<MexcService> mexcLogger = requiredService2.CreateLogger<MexcService>();
			KaspaBot.Domain.Entities.User user = await requiredService.GetByIdAsync(userId);
			CancellationToken cancellationToken2;
			if (user == null)
			{
				ITelegramBotClient botClient = _botClient;
				ChatId chatId = userId;
				cancellationToken2 = cancellationToken;
				await botClient.SendMessage(chatId, "Пользователь не найден", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			MexcService mexcService = MexcService.Create(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, mexcLogger);
			List<OrderPair> list = (await orderPairRepo.GetAllAsync()).Where((OrderPair p) => p.UserId == userId && !string.IsNullOrEmpty(p.SellOrder.Id)).ToList();
			string diagnosticMsg = "\ud83d\udd0d <b>Диагностика ордеров</b>\n\n";
			diagnosticMsg += $"\ud83d\udcca Всего пар: {list.Count}\n\n";
			foreach (OrderPair pair in list.Take(5))
			{
				diagnosticMsg = diagnosticMsg + "<b>Пара " + pair.Id + "</b>\n";
				diagnosticMsg = diagnosticMsg + "Sell Order ID: " + pair.SellOrder.Id + "\n";
				diagnosticMsg += $"Цена в БД: {pair.SellOrder.Price:F6}\n";
				diagnosticMsg += $"Статус в БД: {pair.SellOrder.Status}\n";
				Result<MexcOrder> result = await mexcService.GetOrderAsync("KASUSDT", pair.SellOrder.Id, cancellationToken);
				if (result.IsSuccess)
				{
					diagnosticMsg += $"Статус на бирже: {result.Value.Status}\n";
					diagnosticMsg += $"Цена на бирже: {result.Value.Price:F6}\n";
					diagnosticMsg += $"Количество: {result.Value.Quantity:F2}\n";
					diagnosticMsg += $"Исполнено: {result.Value.QuantityFilled:F2}\n";
					if (result.Value.Status != pair.SellOrder.Status)
					{
						diagnosticMsg += $"⚠\ufe0f <b>РАСХОЖДЕНИЕ!</b> Статус в БД: {pair.SellOrder.Status}, на бирже: {result.Value.Status}\n";
					}
				}
				else
				{
					diagnosticMsg = diagnosticMsg + "❌ Ошибка получения ордера: " + string.Join(", ", result.Errors.Select((IError e) => e.Message)) + "\n";
				}
				diagnosticMsg += "\n";
			}
			ITelegramBotClient botClient2 = _botClient;
			ChatId chatId2 = userId;
			string text = diagnosticMsg;
			cancellationToken2 = cancellationToken;
			await botClient2.SendMessage(chatId2, text, ParseMode.Html, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
			await scope.ServiceProvider.GetRequiredService<IOrderRecoveryService>().RunRecoveryForUser(userId, cancellationToken);
			ITelegramBotClient botClient3 = _botClient;
			ChatId chatId3 = userId;
			cancellationToken2 = cancellationToken;
			await botClient3.SendMessage(chatId3, "✅ Восстановление ордеров завершено", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
		}

		[BotCommand("Статус WebSocket", AdminOnly = true)]
		public async Task HandleWebSocketStatusCommand(Message message, CancellationToken cancellationToken)
		{
			long userId = message.Chat.Id;
			using IServiceScope scope = _serviceProvider.CreateScope();
			UserStreamManager userStreamManager = scope.ServiceProvider.GetRequiredService<UserStreamManager>();
			CancellationToken cancellationToken2;
			if (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(userId) == null)
			{
				ITelegramBotClient botClient = _botClient;
				ChatId chatId = userId;
				cancellationToken2 = cancellationToken;
				await botClient.SendMessage(chatId, "❌ Пользователь не найден", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			string listenKey = userStreamManager.GetListenKey(userId);
			string text = "\ud83d\udd0c <b>Статус WebSocket</b>\n\n";
			text += $"\ud83d\udc64 User ID: {userId}\n";
			text = text + "\ud83d\udd11 Listen Key: " + (string.IsNullOrEmpty(listenKey) ? "❌ Отсутствует" : ("✅ " + listenKey)) + "\n";
			text = text + "\ud83d\udce1 WebSocket: " + (string.IsNullOrEmpty(listenKey) ? "❌ Не подключен" : "✅ Подключен") + "\n\n";
			text += "\ud83d\udca1 <b>Диагностика:</b>\n";
			text += "• Если Listen Key отсутствует - WebSocket не работает\n";
			text += "• Если Listen Key есть, но события не приходят - проблема с соединением\n";
			text += "• Проверьте логи на наличие ошибок WebSocket\n";
			ITelegramBotClient botClient2 = _botClient;
			ChatId chatId2 = userId;
			string text2 = text;
			cancellationToken2 = cancellationToken;
			await botClient2.SendMessage(chatId2, text2, ParseMode.Html, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
		}

		[BotCommand("Сбросить статус отменённого ордера", AdminOnly = true, UserIdParameter = "userId")]
		public async Task HandleResetCanceledOrdersCommand(Message message, CancellationToken cancellationToken, long targetUserId = 0L)
		{
			long adminUserId = message.Chat.Id;
			long userId = ((targetUserId > 0) ? targetUserId : adminUserId);
			string[] array = (message.Text?.Trim() ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
			string orderId = null;
			if (array.Length > 1)
			{
				orderId = array[1];
			}
			if (string.IsNullOrEmpty(orderId))
			{
				ITelegramBotClient botClient = _botClient;
				ChatId chatId = adminUserId;
				CancellationToken cancellationToken2 = cancellationToken;
				await botClient.SendMessage(chatId, "❌ Укажите ID ордера: /reset_canceled ORDER_ID", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			_logger.LogInformation($"[RESET-DEBUG] Админ {adminUserId} сбрасывает статус ордера {orderId} для пользователя {userId}");
			using IServiceScope scope = _serviceProvider.CreateScope();
			OrderPairRepository orderPairRepo = scope.ServiceProvider.GetRequiredService<OrderPairRepository>();
			OrderPair orderPair = (await orderPairRepo.GetAllAsync()).FirstOrDefault((OrderPair p) => p.UserId == userId && (p.BuyOrder.Id == orderId || p.SellOrder.Id == orderId));
			if (orderPair == null)
			{
				ITelegramBotClient botClient2 = _botClient;
				ChatId chatId2 = adminUserId;
				string text = $"❌ Ордер {orderId} не найден для пользователя {userId}";
				CancellationToken cancellationToken2 = cancellationToken;
				await botClient2.SendMessage(chatId2, text, ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			Order order = ((orderPair.BuyOrder.Id == orderId) ? orderPair.BuyOrder : orderPair.SellOrder);
			if (order.Status != OrderStatus.Canceled)
			{
				ITelegramBotClient botClient3 = _botClient;
				ChatId chatId3 = adminUserId;
				string text2 = $"❌ Ордер {orderId} имеет статус {order.Status}, а не Canceled";
				CancellationToken cancellationToken2 = cancellationToken;
				await botClient3.SendMessage(chatId3, text2, ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
			}
			else
			{
				order.Status = OrderStatus.New;
				await orderPairRepo.UpdateAsync(orderPair);
				_logger.LogInformation("[RESET-DEBUG] Статус ордера " + orderId + " сброшен с Canceled на New");
				ITelegramBotClient botClient4 = _botClient;
				ChatId chatId4 = adminUserId;
				string text3 = "✅ Статус ордера " + orderId + " сброшен с Canceled на New";
				CancellationToken cancellationToken2 = cancellationToken;
				await botClient4.SendMessage(chatId4, text3, ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
			}
		}

		[BotCommand("Применить изменения статусов", AdminOnly = true)]
		public async Task HandleApplyStatusChangesCommand(Message message, CancellationToken cancellationToken)
		{
			long userId = message.Chat.Id;
			if (userId != 130822044)
			{
				ITelegramBotClient botClient = _botClient;
				ChatId chatId = userId;
				CancellationToken cancellationToken2 = cancellationToken;
				await botClient.SendMessage(chatId, "❌ <b>Доступ запрещен</b>\n\n\ud83d\udca1 <i>Эта команда доступна только администратору</i>", ParseMode.Html, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			using IServiceScope scope = _serviceProvider.CreateScope();
			OrderPairRepository orderPairRepo = scope.ServiceProvider.GetRequiredService<OrderPairRepository>();
			IUserRepository userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
			ILoggerFactory loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
			List<OrderPair> list = (await orderPairRepo.GetAllAsync()).Where((OrderPair p) => !p.CompletedAt.HasValue).ToList();
			List<(string orderId, string oldStatus, string newStatus, string reason)> appliedChanges = new List<(string, string, string, string)>();
			foreach (OrderPair pair in list)
			{
				KaspaBot.Domain.Entities.User user = await userRepository.GetByIdAsync(pair.UserId);
				if (user == null)
				{
					continue;
				}
				ILogger<MexcService> logger = loggerFactory.CreateLogger<MexcService>();
				MexcService mexcService = MexcService.Create(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, logger);
				if (!string.IsNullOrEmpty(pair.BuyOrder.Id) && !IsFinal(pair.BuyOrder.Status))
				{
					string oldStatus = pair.BuyOrder.Status.ToString();
					Result<MexcOrder> result = await mexcService.GetOrderAsync(pair.BuyOrder.Symbol, pair.BuyOrder.Id, cancellationToken);
					if (result.IsSuccess)
					{
						string text = result.Value.Status.ToString();
						if (text != oldStatus)
						{
							pair.BuyOrder.Status = result.Value.Status;
							pair.BuyOrder.QuantityFilled = result.Value.QuantityFilled;
							pair.BuyOrder.QuoteQuantityFilled = result.Value.QuoteQuantityFilled;
							if (result.Value.OrderType == OrderType.Market && result.Value.Status == OrderStatus.Filled && result.Value.QuantityFilled > 0m && result.Value.QuoteQuantityFilled > 0m)
							{
								pair.BuyOrder.Price = result.Value.QuoteQuantityFilled / result.Value.QuantityFilled;
							}
							else
							{
								pair.BuyOrder.Price = result.Value.Price;
							}
							pair.BuyOrder.UpdatedAt = DateTime.UtcNow;
							appliedChanges.Add((pair.BuyOrder.Id, oldStatus, text, "Buy-ордер"));
						}
					}
				}
				if (!string.IsNullOrEmpty(pair.SellOrder.Id) && !IsFinal(pair.SellOrder.Status))
				{
					string oldStatus = pair.SellOrder.Status.ToString();
					Result<MexcOrder> result2 = await mexcService.GetOrderAsync(pair.SellOrder.Symbol, pair.SellOrder.Id, cancellationToken);
					if (result2.IsSuccess)
					{
						string text2 = result2.Value.Status.ToString();
						if (text2 != oldStatus)
						{
							pair.SellOrder.Status = result2.Value.Status;
							pair.SellOrder.QuantityFilled = result2.Value.QuantityFilled;
							pair.SellOrder.Price = result2.Value.Price;
							pair.SellOrder.UpdatedAt = DateTime.UtcNow;
							if (result2.Value.Status == OrderStatus.Filled)
							{
								pair.CompletedAt = DateTime.UtcNow;
								decimal num = result2.Value.QuantityFilled * result2.Value.Price;
								decimal num2 = pair.BuyOrder.QuantityFilled * pair.BuyOrder.Price.GetValueOrDefault();
								pair.Profit = num - num2 - pair.BuyOrder.Commission;
							}
							appliedChanges.Add((pair.SellOrder.Id, oldStatus, text2, "Sell-ордер"));
						}
					}
				}
				await orderPairRepo.UpdateAsync(pair);
			}
			if (appliedChanges.Any())
			{
				string text3 = $"✅ <b>Применены изменения статусов</b>\n\n\ud83d\udcca <b>Обновлено ордеров:</b> {appliedChanges.Count}\n\n\ud83d\udccb <b>Список изменений:</b>\n";
				foreach (var item5 in appliedChanges)
				{
					string item = item5.orderId;
					string item2 = item5.oldStatus;
					string item3 = item5.newStatus;
					string item4 = item5.reason;
					text3 += $"• <code>{item}</code>: {item2} → {item3} ({item4})\n";
				}
				ITelegramBotClient botClient2 = _botClient;
				ChatId chatId2 = userId;
				string text4 = text3;
				CancellationToken cancellationToken2 = cancellationToken;
				await botClient2.SendMessage(chatId2, text4, ParseMode.Html, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
			}
			else
			{
				ITelegramBotClient botClient3 = _botClient;
				ChatId chatId3 = userId;
				CancellationToken cancellationToken2 = cancellationToken;
				await botClient3.SendMessage(chatId3, "ℹ\ufe0f <b>Изменений не найдено</b>\n\n\ud83d\udca1 <i>Все ордера имеют актуальные статусы</i>", ParseMode.Html, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
			}
		}

		private static bool IsFinal(OrderStatus status)
		{
			if (status != OrderStatus.Filled)
			{
				return status == OrderStatus.Canceled;
			}
			return true;
		}

		[BotCommand("Показать балансы", AdminOnly = false, UserIdParameter = "userId")]
		public async Task HandleBalanceCommand(Message message, CancellationToken cancellationToken, long targetUserId = 0L)
		{
			long adminUserId = message.Chat.Id;
			long userId = ((targetUserId > 0) ? targetUserId : adminUserId);
			bool flag = adminUserId == 130822044;
			if (targetUserId > 0 && !flag)
			{
				ITelegramBotClient botClient = _botClient;
				ChatId chatId = adminUserId;
				CancellationToken cancellationToken2 = cancellationToken;
				await botClient.SendMessage(chatId, "❌ У вас нет прав для просмотра баланса другого пользователя.", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			using IServiceScope scope = _serviceProvider.CreateScope();
			KaspaBot.Domain.Entities.User user = await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(userId);
			CancellationToken cancellationToken2;
			if (user == null)
			{
				string text = ((targetUserId > 0) ? $"❌ Пользователь {userId} не найден." : "❌ Пользователь не найден.");
				ITelegramBotClient botClient2 = _botClient;
				ChatId chatId2 = adminUserId;
				cancellationToken2 = cancellationToken;
				await botClient2.SendMessage(chatId2, text, ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			ILogger<MexcService> logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<MexcService>();
			MexcService mexcService = MexcService.Create(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, logger);
			Result<MexcAccountInfo> result = await mexcService.GetAccountInfoAsync(cancellationToken);
			if (!result.IsSuccess)
			{
				string text2 = ((targetUserId > 0) ? $"❌ Ошибка получения баланса пользователя {userId}: {result.Errors.FirstOrDefault()?.Message}" : ("❌ Ошибка получения баланса: " + result.Errors.FirstOrDefault()?.Message));
				ITelegramBotClient botClient3 = _botClient;
				ChatId chatId3 = adminUserId;
				cancellationToken2 = cancellationToken;
				await botClient3.SendMessage(chatId3, text2, ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			List<MexcAccountBalance> list = result.Value.Balances.Where((MexcAccountBalance mexcAccountBalance) => mexcAccountBalance.Total > 0m).ToList();
			if (!list.Any())
			{
				string text3 = ((targetUserId > 0) ? $"На счету пользователя {userId} нет средств." : "На вашем счету нет средств.");
				ITelegramBotClient botClient4 = _botClient;
				ChatId chatId4 = adminUserId;
				cancellationToken2 = cancellationToken;
				await botClient4.SendMessage(chatId4, text3, ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			decimal totalUsdt = default(decimal);
			List<(string Asset, decimal Total, decimal Available, decimal Frozen, decimal? UsdtValue)> rows = new List<(string, decimal, decimal, decimal, decimal?)>();
			foreach (MexcAccountBalance b in list)
			{
				decimal? usdtValue = null;
				if (b.Asset == "USDT")
				{
					usdtValue = b.Total;
					totalUsdt += b.Total;
				}
				else
				{
					Result<decimal> result2 = await mexcService.GetSymbolPriceAsync(b.Asset + "USDT", cancellationToken);
					if (result2.IsSuccess)
					{
						usdtValue = b.Total * result2.Value;
						totalUsdt += usdtValue.Value;
					}
				}
				rows.Add((b.Asset, b.Total, b.Available, b.Total - b.Available, usdtValue));
			}
			string text4 = ((targetUserId > 0) ? $"\ud83d\udcb0 <b>Баланс пользователя {userId}</b>" : "\ud83d\udcb0 <b>Ваш баланс</b>") + "\n\n" + NotificationFormatter.BalanceTable(rows, totalUsdt);
			ITelegramBotClient botClient5 = _botClient;
			ChatId chatId5 = adminUserId;
			cancellationToken2 = cancellationToken;
			await botClient5.SendMessage(chatId5, text4, ParseMode.Html, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
		}

		[BotCommand("Включить/выключить автоторговлю")]
		public async Task HandleAutotradeCommand(Message message, CancellationToken cancellationToken)
		{
			long userId = message.Chat.Id;
			using IServiceScope scope = _serviceProvider.CreateScope();
			IUserRepository userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
			KaspaBot.Domain.Entities.User user = await userRepository.GetByIdAsync(userId);
			CancellationToken cancellationToken2;
			if (user == null)
			{
				ITelegramBotClient botClient = _botClient;
				ChatId chatId = userId;
				cancellationToken2 = cancellationToken;
				await botClient.SendMessage(chatId, "❌ Пользователь не найден.", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			user.Settings.IsAutoTradeEnabled = !user.Settings.IsAutoTradeEnabled;
			await userRepository.UpdateAsync(user);
			string text = (user.Settings.IsAutoTradeEnabled ? "\ud83d\udfe2 включена" : "\ud83d\udd34 выключена");
			ITelegramBotClient botClient2 = _botClient;
			ChatId chatId2 = userId;
			string text2 = "✅ Автоторговля " + text;
			cancellationToken2 = cancellationToken;
			await botClient2.SendMessage(chatId2, text2, ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
		}

		[BotCommand("Показать список всех команд")]
		public async Task HandleCommandsListCommand(Message message, CancellationToken cancellationToken)
		{
			long id = message.Chat.Id;
			bool flag = id == 130822044;
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("<b>Доступные команды:</b>\n");
			List<MethodInfo> list = (from m in typeof(TradingCommandHandler).GetMethods(BindingFlags.Instance | BindingFlags.Public)
				where m.GetCustomAttribute<BotCommandAttribute>() != null
				select m).ToList();
			List<string> list2 = new List<string>();
			List<string> list3 = new List<string>();
			foreach (MethodInfo item2 in list)
			{
				BotCommandAttribute customAttribute = item2.GetCustomAttribute<BotCommandAttribute>();
				if (customAttribute == null)
				{
					continue;
				}
				string text = MethodToCommandName(item2);
				string item = "<b>" + text + "</b> — " + customAttribute.Description;
				if (customAttribute.AdminOnly)
				{
					if (flag)
					{
						list3.Add(item);
					}
				}
				else
				{
					list2.Add(item);
				}
			}
			if (list2.Any())
			{
				stringBuilder.AppendLine("<b>\ud83d\udccb Основные команды:</b>");
				foreach (string item3 in list2)
				{
					StringBuilder stringBuilder2 = stringBuilder;
					StringBuilder stringBuilder3 = stringBuilder2;
					StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(2, 1, stringBuilder2);
					handler.AppendLiteral("• ");
					handler.AppendFormatted(item3);
					stringBuilder3.AppendLine(ref handler);
				}
				stringBuilder.AppendLine();
			}
			if (list3.Any())
			{
				stringBuilder.AppendLine("<b>\ud83d\udd27 Админские команды:</b>");
				foreach (string item4 in list3)
				{
					StringBuilder stringBuilder2 = stringBuilder;
					StringBuilder stringBuilder4 = stringBuilder2;
					StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(2, 1, stringBuilder2);
					handler.AppendLiteral("• ");
					handler.AppendFormatted(item4);
					stringBuilder4.AppendLine(ref handler);
				}
			}
			await _botClient.SendMessage(id, stringBuilder.ToString(), ParseMode.Html, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken);
		}

		private static string MethodToCommandName(MethodInfo method)
		{
			string name = method.Name;
			if (name.StartsWith("Handle") && name.EndsWith("Command"))
			{
				string text = name.Substring(6, name.Length - 13).ToLowerInvariant();
				return text switch
				{
					"sellamount" => "/sell", 
					"resetcanceledorders" => "/reset_canceled", 
					"wipeuser" => "/wipe_user", 
					"orderrecovery" => "/order_recovery", 
					"checkorders" => "/check_orders", 
					"cancelorders" => "/cancel_orders", 
					"closeallorders" => "/close_all", 
					"websocketstatus" => "/ws_status", 
					"applystatuschanges" => "/apply_status_changes", 
					"commandslist" => "/commands", 
					"autotrade" => "/autotrade", 
					"balance" => "/balance", 
					"fee" => "/fee", 
					"checkorderstatus" => "/check_order", 
					_ => "/" + text, 
				};
			}
			return "/" + name.ToLowerInvariant();
		}

		private static long? ExtractUserIdFromCommand(string text, string commandName)
		{
			if (!text.StartsWith(commandName, StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			string text2 = text.Substring(commandName.Length).Trim();
			if (string.IsNullOrEmpty(text2))
			{
				return null;
			}
			string[] array = text2.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (array.Length != 0 && long.TryParse(array[0], out var result))
			{
				return result;
			}
			return null;
		}

		public async Task HandleUpdateAsync(Message message, CancellationToken cancellationToken)
		{
			string text = message.Text?.Trim() ?? string.Empty;
			long userId = message.Chat.Id;
			bool isAdmin = userId == 130822044;
			if (text.StartsWith("/sell ", StringComparison.OrdinalIgnoreCase))
			{
				string[] array = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
				if (array.Length == 2 && decimal.TryParse(array[1], out var result))
				{
					await HandleSellAmountCommand(message, result, cancellationToken);
					return;
				}
			}
			List<MethodInfo> list = (from m in typeof(TradingCommandHandler).GetMethods(BindingFlags.Instance | BindingFlags.Public)
				where m.GetCustomAttribute<BotCommandAttribute>() != null
				select m).ToList();
			foreach (MethodInfo item in list)
			{
				BotCommandAttribute customAttribute = item.GetCustomAttribute<BotCommandAttribute>();
				if (customAttribute == null || (customAttribute.AdminOnly && !isAdmin))
				{
					continue;
				}
				string commandName = MethodToCommandName(item);
				if (!text.Equals(commandName, StringComparison.OrdinalIgnoreCase) && !text.StartsWith(commandName + " ", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				try
				{
					if (!string.IsNullOrEmpty(customAttribute.UserIdParameter))
					{
						long? num = ExtractUserIdFromCommand(text, commandName);
						if (num.HasValue)
						{
							if (!isAdmin)
							{
								ITelegramBotClient botClient = _botClient;
								ChatId chatId = userId;
								CancellationToken cancellationToken2 = cancellationToken;
								await botClient.SendMessage(chatId, "❌ У вас нет прав для выполнения этой команды с параметром UserID.", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
								return;
							}
							ParameterInfo[] parameters = item.GetParameters();
							if (parameters.Length != 3 || !(parameters[2].ParameterType == typeof(long)))
							{
								await (Task)item.Invoke(this, new object[2] { message, cancellationToken });
							}
							else
							{
								await (Task)item.Invoke(this, new object[3] { message, cancellationToken, num.Value });
							}
						}
						else
						{
							ParameterInfo[] parameters2 = item.GetParameters();
							if (parameters2.Length != 3 || !(parameters2[2].ParameterType == typeof(long)))
							{
								await (Task)item.Invoke(this, new object[2] { message, cancellationToken });
							}
							else
							{
								await (Task)item.Invoke(this, new object[3] { message, cancellationToken, 0L });
							}
						}
					}
					else
					{
						await (Task)item.Invoke(this, new object[2] { message, cancellationToken });
					}
					return;
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Ошибка при выполнении команды {CommandName}", commandName);
					ITelegramBotClient botClient2 = _botClient;
					ChatId chatId2 = userId;
					string text2 = "❌ Ошибка выполнения команды: " + ex.Message;
					CancellationToken cancellationToken2 = cancellationToken;
					await botClient2.SendMessage(chatId2, text2, ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
					return;
				}
			}
			await _botClient.SendMessage(message.Chat.Id, "❗\ufe0f Неизвестная команда.");
			await HandleCommandsListCommand(message, cancellationToken);
		}

		[BotCommand("Проверить статус ордера", AdminOnly = true)]
		public async Task HandleCheckOrderStatusCommand(Message message, CancellationToken cancellationToken)
		{
			long userId = message.Chat.Id;
			string[] array = (message.Text?.Trim())?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (array == null || array.Length < 2)
			{
				ITelegramBotClient botClient = _botClient;
				ChatId chatId = userId;
				CancellationToken cancellationToken2 = cancellationToken;
				await botClient.SendMessage(chatId, "Используй: /check_order {orderId}", ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			string orderId = array[1];
			using IServiceScope scope = _serviceProvider.CreateScope();
			IUserRepository requiredService = scope.ServiceProvider.GetRequiredService<IUserRepository>();
			OrderPairRepository orderPairRepo = scope.ServiceProvider.GetRequiredService<OrderPairRepository>();
			ILoggerFactory requiredService2 = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
			ILogger<MexcService> mexcLogger = requiredService2.CreateLogger<MexcService>();
			List<KaspaBot.Domain.Entities.User> users = await requiredService.GetAllAsync();
			OrderPair pair = (await orderPairRepo.GetAllAsync()).FirstOrDefault((OrderPair p) => p.BuyOrder.Id == orderId || p.SellOrder.Id == orderId);
			CancellationToken cancellationToken2;
			if (pair == null)
			{
				ITelegramBotClient botClient2 = _botClient;
				ChatId chatId2 = userId;
				string text = "Ордер " + orderId + " не найден в базе данных";
				cancellationToken2 = cancellationToken;
				await botClient2.SendMessage(chatId2, text, ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			KaspaBot.Domain.Entities.User user = users.FirstOrDefault((KaspaBot.Domain.Entities.User u) => u.Id == pair.UserId);
			if (user == null)
			{
				ITelegramBotClient botClient3 = _botClient;
				ChatId chatId3 = userId;
				string text2 = "Пользователь для ордера " + orderId + " не найден";
				cancellationToken2 = cancellationToken;
				await botClient3.SendMessage(chatId3, text2, ParseMode.None, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
				return;
			}
			Result<MexcOrder> result = await MexcService.Create(user.ApiCredentials.ApiKey, user.ApiCredentials.ApiSecret, mexcLogger).GetOrderAsync("KASUSDT", orderId, cancellationToken);
			StringBuilder stringBuilder = new StringBuilder();
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder3 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(27, 1, stringBuilder2);
			handler.AppendLiteral("\ud83d\udd0d <b>Проверка ордера ");
			handler.AppendFormatted(orderId);
			handler.AppendLiteral("</b>\n");
			stringBuilder3.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder4 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(17, 1, stringBuilder2);
			handler.AppendLiteral("\ud83d\udc64 Пользователь: ");
			handler.AppendFormatted(user.Id);
			stringBuilder4.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder5 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(8, 1, stringBuilder2);
			handler.AppendLiteral("\ud83d\udcca Тип: ");
			handler.AppendFormatted((pair.BuyOrder.Id == orderId) ? "Buy" : "Sell");
			stringBuilder5.AppendLine(ref handler);
			if (result.IsSuccess)
			{
				MexcOrder value = result.Value;
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder6 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(26, 1, stringBuilder2);
				handler.AppendLiteral("✅ <b>Статус на бирже:</b> ");
				handler.AppendFormatted(value.Status);
				stringBuilder6.AppendLine(ref handler);
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder7 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(9, 1, stringBuilder2);
				handler.AppendLiteral("\ud83d\udcc8 Цена: ");
				handler.AppendFormatted(value.Price, "F6");
				stringBuilder7.AppendLine(ref handler);
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder8 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(15, 1, stringBuilder2);
				handler.AppendLiteral("\ud83d\udcca Количество: ");
				handler.AppendFormatted(value.Quantity, "F3");
				stringBuilder8.AppendLine(ref handler);
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder9 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(13, 1, stringBuilder2);
				handler.AppendLiteral("✅ Исполнено: ");
				handler.AppendFormatted(value.QuantityFilled, "F3");
				stringBuilder9.AppendLine(ref handler);
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder10 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(10, 1, stringBuilder2);
				handler.AppendLiteral("\ud83d\udcb0 Сумма: ");
				handler.AppendFormatted(value.QuoteQuantityFilled, "F6");
				stringBuilder10.AppendLine(ref handler);
				Order order = ((pair.BuyOrder.Id == orderId) ? pair.BuyOrder : pair.SellOrder);
				stringBuilder.AppendLine("\n\ud83d\udccb <b>В базе данных:</b>");
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder11 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(11, 1, stringBuilder2);
				handler.AppendLiteral("\ud83d\udcca Статус: ");
				handler.AppendFormatted(order.Status);
				stringBuilder11.AppendLine(ref handler);
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder12 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(9, 1, stringBuilder2);
				handler.AppendLiteral("\ud83d\udcc8 Цена: ");
				handler.AppendFormatted(order.Price, "F6");
				stringBuilder12.AppendLine(ref handler);
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder13 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(15, 1, stringBuilder2);
				handler.AppendLiteral("\ud83d\udcca Количество: ");
				handler.AppendFormatted(order.Quantity, "F3");
				stringBuilder13.AppendLine(ref handler);
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder14 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(13, 1, stringBuilder2);
				handler.AppendLiteral("✅ Исполнено: ");
				handler.AppendFormatted(order.QuantityFilled, "F3");
				stringBuilder14.AppendLine(ref handler);
				if (value.Status != order.Status)
				{
					stringBuilder.AppendLine("\n⚠\ufe0f <b>РАСХОЖДЕНИЕ:</b> Статус отличается!");
					stringBuilder2 = stringBuilder;
					StringBuilder stringBuilder15 = stringBuilder2;
					handler = new StringBuilder.AppendInterpolatedStringHandler(16, 2, stringBuilder2);
					handler.AppendLiteral("Биржа: ");
					handler.AppendFormatted(value.Status);
					handler.AppendLiteral(" | База: ");
					handler.AppendFormatted(order.Status);
					stringBuilder15.AppendLine(ref handler);
				}
				if (Math.Abs(value.QuantityFilled - order.QuantityFilled) > 0.001m)
				{
					stringBuilder.AppendLine("⚠\ufe0f <b>РАСХОЖДЕНИЕ:</b> Количество исполнено отличается!");
					stringBuilder2 = stringBuilder;
					StringBuilder stringBuilder16 = stringBuilder2;
					handler = new StringBuilder.AppendInterpolatedStringHandler(16, 2, stringBuilder2);
					handler.AppendLiteral("Биржа: ");
					handler.AppendFormatted(value.QuantityFilled, "F3");
					handler.AppendLiteral(" | База: ");
					handler.AppendFormatted(order.QuantityFilled, "F3");
					stringBuilder16.AppendLine(ref handler);
				}
			}
			else
			{
				stringBuilder.AppendLine("❌ <b>Ошибка получения статуса:</b>");
				stringBuilder.AppendLine(string.Join(", ", result.Errors.Select((IError e) => e.Message)));
				Order order2 = ((pair.BuyOrder.Id == orderId) ? pair.BuyOrder : pair.SellOrder);
				stringBuilder.AppendLine("\n\ud83d\udccb <b>В базе данных:</b>");
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder17 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(11, 1, stringBuilder2);
				handler.AppendLiteral("\ud83d\udcca Статус: ");
				handler.AppendFormatted(order2.Status);
				stringBuilder17.AppendLine(ref handler);
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder18 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(9, 1, stringBuilder2);
				handler.AppendLiteral("\ud83d\udcc8 Цена: ");
				handler.AppendFormatted(order2.Price, "F6");
				stringBuilder18.AppendLine(ref handler);
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder19 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(15, 1, stringBuilder2);
				handler.AppendLiteral("\ud83d\udcca Количество: ");
				handler.AppendFormatted(order2.Quantity, "F3");
				stringBuilder19.AppendLine(ref handler);
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder20 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(13, 1, stringBuilder2);
				handler.AppendLiteral("✅ Исполнено: ");
				handler.AppendFormatted(order2.QuantityFilled, "F3");
				stringBuilder20.AppendLine(ref handler);
			}
			ITelegramBotClient botClient4 = _botClient;
			ChatId chatId4 = userId;
			string text3 = stringBuilder.ToString();
			cancellationToken2 = cancellationToken;
			await botClient4.SendMessage(chatId4, text3, ParseMode.Html, null, null, null, null, null, disableNotification: false, protectContent: false, null, null, allowPaidBroadcast: false, cancellationToken2);
		}
	}
}
