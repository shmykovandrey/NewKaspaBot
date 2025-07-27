using KaspaBot.Presentation.Telegram.CommandHandlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Telegram.Bot.Types.ReplyMarkups;
using System;
using System.Linq;
using System.Collections.Generic;

public class TelegramUpdateHandler : IUpdateHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TelegramUpdateHandler> _logger;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly ILoggerFactory _loggerFactory;

    // Время старта приложения
    private static readonly DateTime AppStartTime = DateTime.UtcNow;

    // In-memory регистрация: userId -> этап регистрации (0 - нет, 1 - ждем API Key, 2 - ждем API Secret, 3 - регистрация завершена)
    private static readonly ConcurrentDictionary<long, int> RegistrationStates = new();
    private static readonly ConcurrentDictionary<long, string> TempApiKeys = new();

    // In-memory состояния для конфигурирования: userId -> ожидаемый параметр
    private static readonly ConcurrentDictionary<long, string> ConfigStates = new();

    public TelegramUpdateHandler(
        IServiceScopeFactory scopeFactory,
        ILogger<TelegramUpdateHandler> logger,
        IHostApplicationLifetime appLifetime,
        ILoggerFactory loggerFactory)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _appLifetime = appLifetime;
        _loggerFactory = loggerFactory;
    }

    public async Task HandleUpdateAsync(
        ITelegramBotClient botClient,
        Update update,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var tradingCommandHandler = scope.ServiceProvider.GetRequiredService<TradingCommandHandler>();
        var userRepository = scope.ServiceProvider.GetRequiredService<KaspaBot.Domain.Interfaces.IUserRepository>();

        try
        {
            if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery != null)
            {
                var callback = update.CallbackQuery;
                var userId = callback.From.Id;
                var data = callback.Data;
                _logger.LogInformation($"Получен CallbackQuery от {userId}: {data}");
                
                if (!await userRepository.ExistsAsync(userId))
                {
                    await botClient.AnswerCallbackQuery(callback.Id, "❌ Вы не зарегистрированы. Отправьте /start", showAlert: false, cancellationToken: cancellationToken);
                    return;
                }
                
                var user = await userRepository.GetByIdAsync(userId);
                if (user == null)
                {
                    await botClient.AnswerCallbackQuery(callback.Id, "❌ Пользователь не найден", showAlert: false, cancellationToken: cancellationToken);
                    return;
                }
                
                await HandleConfigCallback(botClient, callback, user, cancellationToken);
                return;
            }
            else if (update.Type == UpdateType.Message && update.Message?.Text != null)
            {
                var userId = update.Message.Chat.Id;
                var text = update.Message.Text.Trim();
                // Обработка /SoftExit для админа — ПРИОРИТЕТНО!
                if (text.Equals("/SoftExit", StringComparison.OrdinalIgnoreCase) && userId == 130822044)
                {
                    // Фильтрация: выполнять только если команда отправлена после старта приложения (с запасом 10 секунд)
                    if (update.Message.Date.ToUniversalTime() < AppStartTime.AddSeconds(-10))
                    {
                        _logger.LogInformation($"Пропущено устаревшее /SoftExit от {update.Message.Chat.Id}");
                        return;
                    }
                    await botClient.SendMessage(
                        chatId: userId,
                        text: "⏳ Приложение завершает работу, дождитесь завершения всех операций...",
                        cancellationToken: cancellationToken);
                    _appLifetime.StopApplication();
                    return;
                }

                // Если пользователь не зарегистрирован
                if (!await userRepository.ExistsAsync(userId))
                {
                    // Если уже в процессе регистрации
                    if (RegistrationStates.TryGetValue(userId, out var regStep))
                    {
                        if (regStep == 1)
                        {
                            // Ожидаем API Key
                            TempApiKeys[userId] = text;
                            RegistrationStates[userId] = 2;
                            await botClient.SendMessage(
                                chatId: userId,
                                text: "Пожалуйста, отправьте ваш API Secret (секретный ключ).",
                                cancellationToken: cancellationToken);
                            return;
                        }
                        else if (regStep == 2)
                        {
                            // Ожидаем API Secret
                            var apiKey = TempApiKeys[userId];
                            var apiSecret = text;
                            // Валидация ключей через Mexc
                            var mexcLogger = _loggerFactory.CreateLogger<KaspaBot.Infrastructure.Services.MexcService>();
                            var mexcService = new KaspaBot.Infrastructure.Services.MexcService(apiKey, apiSecret, mexcLogger);
                            var result = await mexcService.GetAccountInfoAsync(cancellationToken);
                            if (!result.IsSuccess)
                            {
                                await botClient.SendMessage(
                                    chatId: userId,
                                    text: $"Ошибка: ключи невалидны: {result.Errors.FirstOrDefault()?.Message}",
                                    cancellationToken: cancellationToken);
                                RegistrationStates[userId] = 1;
                                await botClient.SendMessage(
                                    chatId: userId,
                                    text: "Пожалуйста, отправьте ваш API Key (публичный ключ) заново.",
                                    cancellationToken: cancellationToken);
                                return;
                            }
                            // Сохраняем пользователя в БД с дефолтными параметрами
                            var user = new KaspaBot.Domain.Entities.User(userId, update.Message.From?.Username ?? $"user{userId}")
                            {
                                ApiCredentials = new KaspaBot.Domain.ValueObjects.UserApiCredentials
                                {
                                    ApiKey = apiKey,
                                    ApiSecret = apiSecret
                                },
                                Settings = new KaspaBot.Domain.ValueObjects.UserSettings
                                {
                                    OrderAmount = 1m, // Сумма ордера в USDT
                                    MaxUsdtUsing = 200m, // Максимальная сумма для торговли
                                    PercentPriceChange = 0.5m, // Процент падения цены для покупки
                                    PercentProfit = 0.5m // Процент для продажи
                                },
                                IsActive = true
                            };
                            await userRepository.AddAsync(user);
                            RegistrationStates.TryRemove(userId, out _);
                            TempApiKeys.TryRemove(userId, out _);
                            RegistrationStates[userId] = 3;
                            await botClient.SendMessage(
                                chatId: userId,
                                text: "✅ Регистрация завершена! Ваши ключи сохранены и проверены.",
                                cancellationToken: cancellationToken);
                            await botClient.SendMessage(
                                chatId: userId,
                                text: "Автоторговля по умолчанию выключена. Для включения используйте команду /autotrade",
                                cancellationToken: cancellationToken);
                            await botClient.SendMessage(
                                chatId: userId,
                                text: "Ваши дефолтные настройки:\n" +
                                      "Сумма ордера: 1 USDT\n" +
                                      "Максимальная сумма: 200 USDT\n" +
                                      "% падения: 0.5%\n" +
                                      "% прибыли: 0.5%\n" +
                                      "\nДоступные команды:\n" +
                                      "/config — изменить настройки\n" +
                                      "/buy — купить KASUSDT (пример)\n",
                                cancellationToken: cancellationToken);
                            return;
                        }
                        else if (regStep == 3)
                        {
                            await botClient.SendMessage(
                                chatId: userId,
                                text: "Вы уже завершили регистрацию. Можете пользоваться ботом.",
                                cancellationToken: cancellationToken);
                            return;
                        }
                    }

                    // Если пользователь отправил /start — начинаем регистрацию
                    if (text.Equals("/start", StringComparison.OrdinalIgnoreCase))
                    {
                        RegistrationStates[userId] = 1;
                        await botClient.SendMessage(
                            chatId: userId,
                            text: "Добро пожаловать! Для работы с ботом необходимо пройти регистрацию.\nПожалуйста, отправьте ваш API Key (публичный ключ).",
                            cancellationToken: cancellationToken);
                        return;
                    }

                    // Если не в процессе регистрации и не /start
                    await botClient.SendMessage(
                        chatId: userId,
                        text: "❗️Вы не зарегистрированы в системе. Для регистрации отправьте /start в этот чат.",
                        cancellationToken: cancellationToken);
                    return;
                }

                // Обработка /config для зарегистрированного пользователя
                if (text.Equals("/config", StringComparison.OrdinalIgnoreCase))
                {
                    var user = await userRepository.GetByIdAsync(userId);
                    if (user == null)
                    {
                        await botClient.SendMessage(
                            chatId: userId,
                            text: "Пользователь не найден.",
                            cancellationToken: cancellationToken);
                        return;
                    }
                    var keyboard = new ReplyKeyboardMarkup(new[]
                    {
                        new[] { new KeyboardButton("Изменить сумму ордера"), new KeyboardButton("Изменить макс. сумму") },
                        new[] { new KeyboardButton("Изменить % падения"), new KeyboardButton("Изменить % прибыли") },
                        new[] { new KeyboardButton("Изменить API Key"), new KeyboardButton("Изменить API Secret") },
                        new[] { new KeyboardButton("Отмена") }
                    })
                    {
                        ResizeKeyboard = true
                    };
                    await botClient.SendMessage(
                        chatId: userId,
                        text: $"Ваши текущие настройки:\n" +
                              $"Сумма ордера: {user.Settings.OrderAmount} USDT\n" +
                              $"Макс. сумма: {user.Settings.MaxUsdtUsing} USDT\n" +
                              $"% падения: {user.Settings.PercentPriceChange}%\n" +
                              $"% прибыли: {user.Settings.PercentProfit}%\n" +
                              $"API Key: {user.ApiCredentials.ApiKey[..Math.Min(6, user.ApiCredentials.ApiKey.Length)]}...",
                        cancellationToken: cancellationToken,
                        replyMarkup: keyboard);
                    ConfigStates[userId] = "menu";
                    return;
                }

                // Если пользователь в процессе конфигурирования
                if (ConfigStates.TryGetValue(userId, out var configStep))
                {
                    var user = await userRepository.GetByIdAsync(userId);
                    if (user == null)
                    {
                        await botClient.SendMessage(chatId: userId, text: "Пользователь не найден.", cancellationToken: cancellationToken);
                        ConfigStates.TryRemove(userId, out _);
                        return;
                    }
                    if (text == "Отмена")
                    {
                        await botClient.SendMessage(chatId: userId, text: "Изменение настроек отменено.", cancellationToken: cancellationToken, replyMarkup: new ReplyKeyboardRemove());
                        ConfigStates.TryRemove(userId, out _);
                        return;
                    }
                    if (configStep == "menu")
                    {
                        if (text == "Изменить сумму ордера")
                        {
                            await botClient.SendMessage(chatId: userId, text: "Введите новую сумму ордера (USDT):", cancellationToken: cancellationToken, replyMarkup: new ReplyKeyboardRemove());
                            ConfigStates[userId] = "OrderAmount";
                            return;
                        }
                        if (text == "Изменить макс. сумму")
                        {
                            await botClient.SendMessage(chatId: userId, text: "Введите новую максимальную сумму для торговли (USDT):", cancellationToken: cancellationToken, replyMarkup: new ReplyKeyboardRemove());
                            ConfigStates[userId] = "MaxUsdtUsing";
                            return;
                        }
                        if (text == "Изменить % падения")
                        {
                            await botClient.SendMessage(chatId: userId, text: "Введите новый процент падения цены (например, 0.5):", cancellationToken: cancellationToken, replyMarkup: new ReplyKeyboardRemove());
                            ConfigStates[userId] = "PercentPriceChange";
                            return;
                        }
                        if (text == "Изменить % прибыли")
                        {
                            await botClient.SendMessage(chatId: userId, text: "Введите новый процент прибыли (например, 0.5):", cancellationToken: cancellationToken, replyMarkup: new ReplyKeyboardRemove());
                            ConfigStates[userId] = "PercentProfit";
                            return;
                        }
                        if (text == "Изменить API Key")
                        {
                            await botClient.SendMessage(chatId: userId, text: "Введите новый API Key:", cancellationToken: cancellationToken, replyMarkup: new ReplyKeyboardRemove());
                            ConfigStates[userId] = "ApiKey";
                            return;
                        }
                        if (text == "Изменить API Secret")
                        {
                            await botClient.SendMessage(chatId: userId, text: "Введите новый API Secret:", cancellationToken: cancellationToken, replyMarkup: new ReplyKeyboardRemove());
                            ConfigStates[userId] = "ApiSecret";
                            return;
                        }
                        await botClient.SendMessage(chatId: userId, text: "Пожалуйста, выберите действие из меню.", cancellationToken: cancellationToken);
                        return;
                    }
                    // Обработка ввода значения
                    if (configStep == "OrderAmount")
                    {
                        if (decimal.TryParse(text.Replace(",", "."), out var value) && value >= 1)
                        {
                            user.Settings.OrderAmount = value;
                            await userRepository.UpdateAsync(user);
                            await botClient.SendMessage(chatId: userId, text: $"Сумма ордера обновлена: {value} USDT", cancellationToken: cancellationToken);
                        }
                        else
                        {
                            await botClient.SendMessage(chatId: userId, text: "Минимальная сумма ордера — 1 USDT", cancellationToken: cancellationToken);
                            return;
                        }
                        ConfigStates[userId] = "menu";
                        await botClient.SendMessage(chatId: userId, text: "Выберите следующий параметр или 'Отмена'", cancellationToken: cancellationToken, replyMarkup: null);
                        return;
                    }
                    if (configStep == "MaxUsdtUsing")
                    {
                        if (decimal.TryParse(text.Replace(",", "."), out var value) && value > 0)
                        {
                            user.Settings.MaxUsdtUsing = value;
                            await userRepository.UpdateAsync(user);
                            await botClient.SendMessage(chatId: userId, text: $"Максимальная сумма обновлена: {value} USDT", cancellationToken: cancellationToken);
                        }
                        else
                        {
                            await botClient.SendMessage(chatId: userId, text: "Некорректное значение. Введите число больше 0.", cancellationToken: cancellationToken);
                            return;
                        }
                        ConfigStates[userId] = "menu";
                        await botClient.SendMessage(chatId: userId, text: "Выберите следующий параметр или 'Отмена'", cancellationToken: cancellationToken, replyMarkup: null);
                        return;
                    }
                    if (configStep == "PercentPriceChange")
                    {
                        if (decimal.TryParse(text.Replace(",", "."), out var value) && value > 0)
                        {
                            user.Settings.PercentPriceChange = value;
                            await userRepository.UpdateAsync(user);
                            await botClient.SendMessage(chatId: userId, text: $"% падения обновлён: {value}%", cancellationToken: cancellationToken);
                        }
                        else
                        {
                            await botClient.SendMessage(chatId: userId, text: "Некорректное значение. Введите число больше 0.", cancellationToken: cancellationToken);
                            return;
                        }
                        ConfigStates[userId] = "menu";
                        await botClient.SendMessage(chatId: userId, text: "Выберите следующий параметр или 'Отмена'", cancellationToken: cancellationToken, replyMarkup: null);
                        return;
                    }
                    if (configStep == "PercentProfit")
                    {
                        if (decimal.TryParse(text.Replace(",", "."), out var value) && value > 0)
                        {
                            user.Settings.PercentProfit = value;
                            await userRepository.UpdateAsync(user);
                            await botClient.SendMessage(chatId: userId, text: $"% прибыли обновлён: {value}%", cancellationToken: cancellationToken);
                        }
                        else
                        {
                            await botClient.SendMessage(chatId: userId, text: "Некорректное значение. Введите число больше 0.", cancellationToken: cancellationToken);
                            return;
                        }
                        ConfigStates[userId] = "menu";
                        await botClient.SendMessage(chatId: userId, text: "Выберите следующий параметр или 'Отмена'", cancellationToken: cancellationToken, replyMarkup: null);
                        return;
                    }
                    if (configStep == "ApiKey")
                    {
                        // Временное хранение нового ключа
                        TempApiKeys[userId] = text;
                        await botClient.SendMessage(chatId: userId, text: "Теперь введите новый API Secret:", cancellationToken: cancellationToken);
                        ConfigStates[userId] = "ApiSecretUpdate";
                        return;
                    }
                    if (configStep == "ApiSecretUpdate")
                    {
                        var newApiKey = TempApiKeys.TryGetValue(userId, out var k) ? k : null;
                        var newApiSecret = text;
                        if (string.IsNullOrEmpty(newApiKey))
                        {
                            await botClient.SendMessage(chatId: userId, text: "Сначала введите новый API Key.", cancellationToken: cancellationToken);
                            ConfigStates[userId] = "ApiKey";
                            return;
                        }
                        // Валидация новых ключей
                        var mexcLogger = _loggerFactory.CreateLogger<KaspaBot.Infrastructure.Services.MexcService>();
                        var mexcService = new KaspaBot.Infrastructure.Services.MexcService(newApiKey, newApiSecret, mexcLogger);
                        var result = await mexcService.GetAccountInfoAsync(cancellationToken);
                        if (!result.IsSuccess)
                        {
                            await botClient.SendMessage(chatId: userId, text: $"Ошибка: ключи невалидны: {result.Errors.FirstOrDefault()?.Message}", cancellationToken: cancellationToken);
                            ConfigStates[userId] = "ApiKey";
                            return;
                        }
                        user.ApiCredentials.ApiKey = newApiKey;
                        user.ApiCredentials.ApiSecret = newApiSecret;
                        await userRepository.UpdateAsync(user);
                        // Пересоздать сервисы и listenKey
                        var userStreamManager = scope.ServiceProvider.GetRequiredService<KaspaBot.Infrastructure.Services.UserStreamManager>();
                        await userStreamManager.ReloadUserAsync(user, cancellationToken);
                        await botClient.SendMessage(chatId: userId, text: "API ключи успешно обновлены и переподключены.", cancellationToken: cancellationToken);
                        TempApiKeys.TryRemove(userId, out _);
                        ConfigStates[userId] = "menu";
                        await botClient.SendMessage(chatId: userId, text: "Выберите следующий параметр или 'Отмена'", cancellationToken: cancellationToken, replyMarkup: null);
                        return;
                    }
                    if (configStep == "ApiSecret")
                    {
                        await botClient.SendMessage(chatId: userId, text: "Для смены ключей используйте сначала 'Изменить API Key'", cancellationToken: cancellationToken);
                        ConfigStates[userId] = "ApiKey";
                        return;
                    }
                }

                // Если пользователь зарегистрирован
                if (text.StartsWith("/buy"))
                {
                    await tradingCommandHandler.HandleBuyCommand(update.Message, cancellationToken);
                }
                else if (text.StartsWith("/sell "))
                {
                    // /sell x — продать x KAS
                    var parts = text.Split(' ', 2);
                    if (parts.Length == 2 && decimal.TryParse(parts[1].Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var qty) && qty > 0)
                    {
                        await tradingCommandHandler.HandleSellAmountCommand(update.Message, qty, cancellationToken);
                    }
                    else
                    {
                        await botClient.SendMessage(chatId: userId, text: "Используйте: /sell x, где x — количество KAS для продажи.", cancellationToken: cancellationToken);
                    }
                }
                else if (text.Equals("/sell", StringComparison.OrdinalIgnoreCase))
                {
                    // /sell — продать весь Free KAS
                    await tradingCommandHandler.HandleSellCommand(update.Message, cancellationToken);
                }
                else if (text.Equals("/status", StringComparison.OrdinalIgnoreCase))
                {
                    await tradingCommandHandler.HandleStatusCommand(update.Message, cancellationToken);
                }
                else if (text.Equals("/stat", StringComparison.OrdinalIgnoreCase))
                {
                    await tradingCommandHandler.HandleStatusCommand(update.Message, cancellationToken);
                    return;
                }
                // /balance — получить баланс пользователя
                else if (text.Equals("/balance", StringComparison.OrdinalIgnoreCase))
                {
                    var user = await userRepository.GetByIdAsync(userId);
                    if (user == null)
                    {
                        await botClient.SendMessage(chatId: userId, text: "Пользователь не найден.", cancellationToken: cancellationToken);
                        return;
                    }
                    var mexcLogger = _loggerFactory.CreateLogger<KaspaBot.Infrastructure.Services.MexcService>();
                    var mexcService = new KaspaBot.Infrastructure.Services.MexcService(
                        user.ApiCredentials.ApiKey,
                        user.ApiCredentials.ApiSecret,
                        mexcLogger);
                    var result = await mexcService.GetAccountInfoAsync(cancellationToken);
                    if (result.IsSuccess)
                    {
                        var balances = result.Value.Balances
                            .Where(b => b.Total > 0)
                            .ToList();
                        if (!balances.Any())
                        {
                            await botClient.SendMessage(chatId: userId, text: "На вашем счету нет средств.", cancellationToken: cancellationToken);
                            return;
                        }
                        // Получить курсы к USDT
                        decimal totalUsdt = 0;
                        var lines = new List<string> { "Ваши балансы:" };
                        foreach (var b in balances)
                        {
                            string line = $"{b.Asset}: {b.Total:F2} | свободно: {b.Available:F2} | заморожено: {(b.Total - b.Available):F2}";
                            lines.Add(line);
                            if (b.Asset == "USDT")
                                totalUsdt += b.Total;
                            else
                            {
                                // Получить цену актива к USDT
                                var priceResult = await mexcService.GetSymbolPriceAsync(b.Asset + "USDT", cancellationToken);
                                if (priceResult.IsSuccess)
                                    totalUsdt += b.Total * priceResult.Value;
                            }
                        }
                        lines.Add($"\nВсего в USDT: {totalUsdt:F2}");
                        await botClient.SendMessage(chatId: userId, text: string.Join("\n", lines), cancellationToken: cancellationToken);
                    }
                    else
                    {
                        await botClient.SendMessage(chatId: userId, text: $"Ошибка получения баланса: {result.Errors.FirstOrDefault()?.Message}", cancellationToken: cancellationToken);
                    }
                    return;
                }
                // /autotrade — включить/выключить автоторговлю
                else if (text.Equals("/autotrade", StringComparison.OrdinalIgnoreCase))
                {
                    await tradingCommandHandler.HandleAutotradeCommand(update.Message, cancellationToken);
                    return;
                }
                // /profit — получить прибыль
                else if (text.Equals("/profit", StringComparison.OrdinalIgnoreCase))
                {
                    await tradingCommandHandler.HandleProfitCommand(update.Message, cancellationToken);
                    return;
                }
                // /wipe {userId} — удалить пользователя (только для админа)
                else if (text.StartsWith("/wipe ") && userId == 130822044)
                {
                    var parts = text.Split(' ', 2);
                    if (parts.Length == 2 && long.TryParse(parts[1], out var targetUserId))
                    {
                        // Вместо прямого удаления вызываем handler с подменой chat.Id
                        var wipeMsg = new Telegram.Bot.Types.Message {
                            Chat = new Telegram.Bot.Types.Chat { Id = targetUserId }
                        };
                        await tradingCommandHandler.HandleWipeUserCommand(wipeMsg, cancellationToken);
                        await botClient.SendMessage(chatId: userId, text: $"Пользователь {targetUserId} удалён.", cancellationToken: cancellationToken);
                    }
                    else
                    {
                        await botClient.SendMessage(chatId: userId, text: "Используйте: /wipe {userId}", cancellationToken: cancellationToken);
                    }
                    return;
                }
                else if (text.StartsWith("/rest", StringComparison.OrdinalIgnoreCase))
                {
                    await tradingCommandHandler.HandleRestCommand(update.Message, cancellationToken);
                    return;
                }
                else if (text.Equals("/check_orders", StringComparison.OrdinalIgnoreCase))
                {
                    await tradingCommandHandler.HandleCheckOrdersCommand(update.Message, cancellationToken);
                    return;
                }
                else if (text.StartsWith("/cancel_orders"))
                {
                    await tradingCommandHandler.HandleCancelOrdersCommand(update.Message, cancellationToken);
                    return;
                }
                else if (text.Equals("/order_recovery", StringComparison.OrdinalIgnoreCase))
                {
                    await tradingCommandHandler.HandleOrderRecoveryCommand(update.Message, cancellationToken);
                    return;
                }
                else if (text.Equals("/fee", StringComparison.OrdinalIgnoreCase))
                {
                    await tradingCommandHandler.HandleFeeCommand(update.Message, cancellationToken);
                    return;
                }
                else if (text.Equals("/close_all", StringComparison.OrdinalIgnoreCase))
                {
                    await tradingCommandHandler.HandleCloseAllOrdersCommand(update.Message, cancellationToken);
                    return;
                }
                else if (text.Equals("/commands", StringComparison.OrdinalIgnoreCase))
                {
                    await tradingCommandHandler.HandleCommandsListCommand(update.Message, cancellationToken);
                    return;
                }
                else if (text.Equals("/ws_status", StringComparison.OrdinalIgnoreCase))
                {
                    await tradingCommandHandler.HandleWebSocketStatusCommand(update.Message, cancellationToken);
                    return;
                }
                else if (text.StartsWith("/reset_canceled", StringComparison.OrdinalIgnoreCase))
                {
                    await tradingCommandHandler.HandleResetCanceledOrdersCommand(update.Message, cancellationToken);
                    return;
                }
                else if (text.StartsWith("/check_order", StringComparison.OrdinalIgnoreCase))
                {
                    await tradingCommandHandler.HandleCheckOrderStatusCommand(update.Message, cancellationToken);
                    return;
                }
                else if (text.Equals("/apply_status_changes", StringComparison.OrdinalIgnoreCase))
                {
                    await tradingCommandHandler.HandleApplyStatusChangesCommand(update.Message, cancellationToken);
                    return;
                }
                else if (text.StartsWith("/balance", StringComparison.OrdinalIgnoreCase))
                {
                    await tradingCommandHandler.HandleBalanceCommand(update.Message, cancellationToken);
                    return;
                }
                else if (text.Equals("/config", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleConfigCommand(botClient, update.Message, cancellationToken);
                    return;
                }
                else if (ConfigStates.TryGetValue(userId, out var currentConfigStep))
                {
                    await HandleConfigTextInput(botClient, update.Message, currentConfigStep, cancellationToken);
                    return;
                }
                else
                {
                    await botClient.SendMessage(
                        chatId: update.Message.Chat.Id,
                        text: "Команда получена",
                        cancellationToken: cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing update");
        }
    }

    public async Task HandlePollingErrorAsync(
        ITelegramBotClient botClient,
        Exception exception,
        CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Telegram polling error");
        await Task.CompletedTask;
    }

    public async Task HandleErrorAsync(
        ITelegramBotClient botClient,
        Exception exception,
        HandleErrorSource errorSource,
        CancellationToken cancellationToken)
    {
        _logger.LogError(exception, $"Telegram error from {errorSource}");
        await Task.CompletedTask;
    }

    private async Task HandleConfigCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var userRepository = scope.ServiceProvider.GetRequiredService<KaspaBot.Domain.Interfaces.IUserRepository>();
        var mexcService = scope.ServiceProvider.GetRequiredService<KaspaBot.Domain.Interfaces.IMexcService>();
        
        var userId = message.Chat.Id;
        var user = await userRepository.GetByIdAsync(userId);
        
        if (user == null)
        {
            await botClient.SendMessage(chatId: userId, text: "❌ Пользователь не найден.", cancellationToken: cancellationToken);
            return;
        }
        
        decimal usdtBalance = 0m;
        try
        {
            var accResult = await mexcService.GetAccountInfoAsync(cancellationToken);
            if (accResult.IsSuccess)
            {
                usdtBalance = accResult.Value.Balances.FirstOrDefault(b => b.Asset == "USDT")?.Available ?? 0m;
            }
        }
        catch { }
        
        var orderAmountText = user.Settings.OrderAmountMode == KaspaBot.Domain.ValueObjects.OrderAmountMode.Fixed 
            ? $"💰 <b>Сумма ордера:</b> <code>{user.Settings.OrderAmount:F2} USDT</code>"
            : $"⚙️ <b>Коэффициент:</b> <code>{user.Settings.DynamicOrderCoef:F2}</code>\n💰 <b>Текущий размер:</b> <code>{user.Settings.GetOrderAmount(usdtBalance):F2} USDT</code>";
        
        var autotradeStatus = user.Settings.IsAutoTradeEnabled ? "🟢 автоторговля ВКЛ" : "🔴 автоторговля ВЫКЛ";
        
        var configText = $"⚙️ <b>Настройки бота</b>\n\n{autotradeStatus}\n🔧 <b>Настройки ордера:</b> <code>{(user.Settings.OrderAmountMode == KaspaBot.Domain.ValueObjects.OrderAmountMode.Fixed ? "Фиксированный" : "Динамический")}</code>\n" +
            orderAmountText + "\n" +
            $"💎 <b>Макс. сумма:</b> <code>{user.Settings.MaxUsdtUsing:F2} USDT</code>\n" +
            $"📉 <b>% падения:</b> <code>{user.Settings.PercentPriceChange:F1}%</code>\n" +
            $"📈 <b>% прибыли:</b> <code>{user.Settings.PercentProfit:F1}%</code>\n" +
            $"🔑 <b>API Key:</b> <code>{user.ApiCredentials.ApiKey.Substring(0, Math.Min(8, user.ApiCredentials.ApiKey.Length))}...</code>\n\n" +
            "💡 <i>Выберите параметр для изменения:</i>";
        
        var replyMarkup = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData(user.Settings.IsAutoTradeEnabled ? "🛑 Отключить автоторговлю" : "▶️ Включить автоторговлю", "config_toggle_autotrade") },
            new[] { InlineKeyboardButton.WithCallbackData("🔧 Настройки ордера", "config_OrderAmountMode") },
            new[] { InlineKeyboardButton.WithCallbackData("💎 Макс. сумма", "config_MaxUsdtUsing") },
            new[] 
            { 
                InlineKeyboardButton.WithCallbackData("📉 % падения", "config_PercentPriceChange"),
                InlineKeyboardButton.WithCallbackData("📈 % прибыли", "config_PercentProfit")
            },
            new[] { InlineKeyboardButton.WithCallbackData("🔑 API ключи", "config_ApiKeys") },
            new[] { InlineKeyboardButton.WithCallbackData("❌ Закрыть", "config_close") }
        });
        
        await botClient.SendMessage(chatId: userId, text: configText, parseMode: ParseMode.Html, replyMarkup: replyMarkup, cancellationToken: cancellationToken);
        ConfigStates[userId] = "menu";
    }

    private async Task HandleConfigCallback(ITelegramBotClient botClient, CallbackQuery callback, KaspaBot.Domain.Entities.User user, CancellationToken cancellationToken)
    {
        var userId = user.Id;
        var data = callback.Data;
        
        switch (data)
        {
            case "config_OrderAmount":
                await botClient.SendMessage(chatId: userId, text: "💰 <b>Введите новую сумму ордера (USDT):</b>\n\n💡 <i>Минимум: 1 USDT</i>", parseMode: ParseMode.Html, replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                ConfigStates[userId] = "OrderAmount";
                await botClient.AnswerCallbackQuery(callback.Id, cancellationToken: cancellationToken);
                break;
                
            case "config_MaxUsdtUsing":
                await botClient.SendMessage(chatId: userId, text: "💎 <b>Введите новую максимальную сумму (USDT):</b>\n\n💡 <i>Максимальная сумма для торговли</i>", parseMode: ParseMode.Html, replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                ConfigStates[userId] = "MaxUsdtUsing";
                await botClient.AnswerCallbackQuery(callback.Id, cancellationToken: cancellationToken);
                break;
                
            case "config_PercentPriceChange":
                await botClient.SendMessage(chatId: userId, text: "📉 <b>Введите новый процент падения:</b>\n\n💡 <i>Например: 0.5 (0.5%)</i>", parseMode: ParseMode.Html, replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                ConfigStates[userId] = "PercentPriceChange";
                await botClient.AnswerCallbackQuery(callback.Id, cancellationToken: cancellationToken);
                break;
                
            case "config_PercentProfit":
                await botClient.SendMessage(chatId: userId, text: "📈 <b>Введите новый процент прибыли:</b>\n\n💡 <i>Например: 0.5 (0.5%)</i>", parseMode: ParseMode.Html, replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                ConfigStates[userId] = "PercentProfit";
                await botClient.AnswerCallbackQuery(callback.Id, cancellationToken: cancellationToken);
                break;
                
            case "config_ApiKeys":
                await botClient.SendMessage(chatId: userId, text: "🔑 <b>Введите новый API Key:</b>\n\n💡 <i>Публичный ключ от MEXC</i>", parseMode: ParseMode.Html, replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                ConfigStates[userId] = "ApiKey";
                await botClient.AnswerCallbackQuery(callback.Id, cancellationToken: cancellationToken);
                break;
                
            case "config_OrderAmountMode":
                var orderModeText = user.Settings.OrderAmountMode == KaspaBot.Domain.ValueObjects.OrderAmountMode.Fixed ? "Фиксированный" : "Динамический";
                var orderModeMarkup = new InlineKeyboardMarkup(new[]
                {
                    new[] 
                    { 
                        InlineKeyboardButton.WithCallbackData("Фиксированный", "set_OrderAmountMode_Fixed"),
                        InlineKeyboardButton.WithCallbackData("Динамический", "set_OrderAmountMode_Dynamic")
                    },
                    new[] { InlineKeyboardButton.WithCallbackData("⬅️ Назад", "config_back") }
                });
                await botClient.SendMessage(chatId: userId, text: $"🔧 <b>Настройки ордера</b>\n\n<b>Режим:</b> <code>{orderModeText}</code>\n\n<b>Выберите режим:</b>", parseMode: ParseMode.Html, replyMarkup: orderModeMarkup, cancellationToken: cancellationToken);
                ConfigStates[userId] = "OrderAmountMode";
                await botClient.AnswerCallbackQuery(callback.Id, cancellationToken: cancellationToken);
                break;
                
            case "set_OrderAmountMode_Fixed":
                user.Settings.OrderAmountMode = KaspaBot.Domain.ValueObjects.OrderAmountMode.Fixed;
                using (var updateScope = _scopeFactory.CreateScope())
                {
                    await updateScope.ServiceProvider.GetRequiredService<KaspaBot.Domain.Interfaces.IUserRepository>().UpdateAsync(user);
                }
                await botClient.SendMessage(chatId: userId, text: "💰 <b>Введите сумму ордера (USDT):</b>\n\n<code>Минимум: 1 USDT</code>", parseMode: ParseMode.Html, replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                ConfigStates[userId] = "OrderAmount";
                await botClient.AnswerCallbackQuery(callback.Id, cancellationToken: cancellationToken);
                break;
                
            case "set_OrderAmountMode_Dynamic":
                _logger.LogInformation($"[CONFIG] set_OrderAmountMode_Dynamic: user={userId}");
                user.Settings.OrderAmountMode = KaspaBot.Domain.ValueObjects.OrderAmountMode.Dynamic;
                using (var updateScope = _scopeFactory.CreateScope())
                {
                    await updateScope.ServiceProvider.GetRequiredService<KaspaBot.Domain.Interfaces.IUserRepository>().UpdateAsync(user);
                }
                await botClient.SendMessage(chatId: userId, text: "⚙️ <b>Введите коэффициент для динамического режима:</b>\n\n<code>Например: 40</code>\n\n<i>Отправьте число в чат. Для отмены — напишите 'Отмена'.</i>", parseMode: ParseMode.Html, replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                ConfigStates[userId] = "DynamicOrderCoef";
                await botClient.AnswerCallbackQuery(callback.Id, cancellationToken: cancellationToken);
                _logger.LogInformation($"[CONFIG] Ожидание ввода коэффициента: user={userId}");
                break;
                
            case "config_back":
                await ShowInlineConfigMenu(botClient, user, cancellationToken);
                await botClient.AnswerCallbackQuery(callback.Id, cancellationToken: cancellationToken);
                break;
                
            case "config_close":
                if (callback.Message != null)
                {
                    await botClient.DeleteMessage(userId, callback.Message.MessageId, cancellationToken);
                }
                ConfigStates.TryRemove(userId, out _);
                await botClient.AnswerCallbackQuery(callback.Id, "❌ Меню закрыто", showAlert: false, cancellationToken: cancellationToken);
                break;
                
            case "config_toggle_autotrade":
                user.Settings.IsAutoTradeEnabled = !user.Settings.IsAutoTradeEnabled;
                using (var updateScope = _scopeFactory.CreateScope())
                {
                    await updateScope.ServiceProvider.GetRequiredService<KaspaBot.Domain.Interfaces.IUserRepository>().UpdateAsync(user);
                }
                await ShowInlineConfigMenu(botClient, user, cancellationToken);
                var toggleText = user.Settings.IsAutoTradeEnabled ? "Автоторговля включена" : "Автоторговля отключена";
                await botClient.AnswerCallbackQuery(callback.Id, toggleText, showAlert: false, cancellationToken: cancellationToken);
                break;
                
            default:
                _logger.LogWarning($"[CONFIG] Неизвестная команда callback: {callback.Data} для user={userId}");
                await botClient.AnswerCallbackQuery(callback.Id, "❌ Неизвестная команда", showAlert: false, cancellationToken: cancellationToken);
                break;
        }
    }

    private async Task ShowInlineConfigMenu(ITelegramBotClient botClient, KaspaBot.Domain.Entities.User user, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var mexcService = scope.ServiceProvider.GetRequiredService<KaspaBot.Domain.Interfaces.IMexcService>();
        
        decimal usdtBalance = 0m;
        try
        {
            var accResult = await mexcService.GetAccountInfoAsync(cancellationToken);
            if (accResult.IsSuccess)
            {
                usdtBalance = accResult.Value.Balances.FirstOrDefault(b => b.Asset == "USDT")?.Available ?? 0m;
            }
        }
        catch { }
        
        var orderAmountText = user.Settings.OrderAmountMode == KaspaBot.Domain.ValueObjects.OrderAmountMode.Fixed 
            ? $"💰 <b>Сумма ордера:</b> <code>{user.Settings.OrderAmount:F2} USDT</code>"
            : $"⚙️ <b>Коэффициент:</b> <code>{user.Settings.DynamicOrderCoef:F2}</code>\n💰 <b>Текущий размер:</b> <code>{user.Settings.GetOrderAmount(usdtBalance):F2} USDT</code>";
        
        var autotradeStatus = user.Settings.IsAutoTradeEnabled ? "🟢 автоторговля ВКЛ" : "🔴 автоторговля ВЫКЛ";
        
        var configText = $"⚙️ <b>Настройки бота</b>\n\n{autotradeStatus}\n🔧 <b>Настройки ордера:</b> <code>{(user.Settings.OrderAmountMode == KaspaBot.Domain.ValueObjects.OrderAmountMode.Fixed ? "Фиксированный" : "Динамический")}</code>\n" +
            orderAmountText + "\n" +
            $"💎 <b>Макс. сумма:</b> <code>{user.Settings.MaxUsdtUsing:F2} USDT</code>\n" +
            $"📉 <b>% падения:</b> <code>{user.Settings.PercentPriceChange:F1}%</code>\n" +
            $"📈 <b>% прибыли:</b> <code>{user.Settings.PercentProfit:F1}%</code>\n" +
            $"🔑 <b>API Key:</b> <code>{user.ApiCredentials.ApiKey.Substring(0, Math.Min(8, user.ApiCredentials.ApiKey.Length))}...</code>\n\n" +
            "💡 <i>Выберите параметр для изменения:</i>";
        
        var replyMarkup = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData(user.Settings.IsAutoTradeEnabled ? "🛑 Отключить автоторговлю" : "▶️ Включить автоторговлю", "config_toggle_autotrade") },
            new[] { InlineKeyboardButton.WithCallbackData("🔧 Настройки ордера", "config_OrderAmountMode") },
            new[] { InlineKeyboardButton.WithCallbackData("💎 Макс. сумма", "config_MaxUsdtUsing") },
            new[] 
            { 
                InlineKeyboardButton.WithCallbackData("📉 % падения", "config_PercentPriceChange"),
                InlineKeyboardButton.WithCallbackData("📈 % прибыли", "config_PercentProfit")
            },
            new[] { InlineKeyboardButton.WithCallbackData("🔑 API ключи", "config_ApiKeys") },
            new[] { InlineKeyboardButton.WithCallbackData("❌ Закрыть", "config_close") }
        });
        
        await botClient.SendMessage(chatId: user.Id, text: configText, parseMode: ParseMode.Html, replyMarkup: replyMarkup, cancellationToken: cancellationToken);
        ConfigStates[user.Id] = "menu";
    }

    private async Task HandleConfigTextInput(ITelegramBotClient botClient, Message message, string configStep, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var userRepository = scope.ServiceProvider.GetRequiredService<KaspaBot.Domain.Interfaces.IUserRepository>();
        var userId = message.Chat.Id;
        var data = message.Text.Trim();
        var user = await userRepository.GetByIdAsync(userId);
        
        if (user == null)
        {
            await botClient.SendMessage(chatId: userId, text: "Пользователь не найден.", cancellationToken: cancellationToken);
            ConfigStates.TryRemove(userId, out _);
            return;
        }
        
        if (data == "Отмена")
        {
            await botClient.SendMessage(chatId: userId, text: "❌ <b>Изменение настроек отменено</b>", parseMode: ParseMode.Html, replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
            ConfigStates.TryRemove(userId, out _);
            return;
        }
        
        decimal value;
        switch (configStep)
        {
            case "menu":
                switch (data)
                {
                    case "🔧 Настройки ордера":
                        var orderModeText = user.Settings.OrderAmountMode == KaspaBot.Domain.ValueObjects.OrderAmountMode.Fixed ? "Фиксированный" : "Динамический";
                        var orderModeMarkup = new InlineKeyboardMarkup(new[]
                        {
                            new[] 
                            { 
                                InlineKeyboardButton.WithCallbackData("Фиксированный", "set_OrderAmountMode_Fixed"),
                                InlineKeyboardButton.WithCallbackData("Динамический", "set_OrderAmountMode_Dynamic")
                            },
                            new[] { InlineKeyboardButton.WithCallbackData("⬅️ Назад", "config_back") }
                        });
                        await botClient.SendMessage(chatId: userId, text: $"🔧 <b>Настройки ордера</b>\n\n<b>Режим:</b> <code>{orderModeText}</code>\n\n<b>Выберите режим:</b>", parseMode: ParseMode.Html, replyMarkup: orderModeMarkup, cancellationToken: cancellationToken);
                        ConfigStates[userId] = "OrderAmountMode";
                        break;
                        
                    case "💎 Макс. сумма":
                        await botClient.SendMessage(chatId: userId, text: "💎 <b>Введите новую максимальную сумму (USDT):</b>\n\n💡 <i>Максимальная сумма для торговли</i>", parseMode: ParseMode.Html, replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                        ConfigStates[userId] = "MaxUsdtUsing";
                        break;
                        
                    case "📉 % падения":
                        await botClient.SendMessage(chatId: userId, text: "📉 <b>Введите новый процент падения:</b>\n\n💡 <i>Например: 0.5 (0.5%)</i>", parseMode: ParseMode.Html, replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                        ConfigStates[userId] = "PercentPriceChange";
                        break;
                        
                    case "📈 % прибыли":
                        await botClient.SendMessage(chatId: userId, text: "📈 <b>Введите новый процент прибыли:</b>\n\n💡 <i>Например: 0.5 (0.5%)</i>", parseMode: ParseMode.Html, replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                        ConfigStates[userId] = "PercentProfit";
                        break;
                        
                    case "🔑 API ключи":
                        await botClient.SendMessage(chatId: userId, text: "🔑 <b>Введите новый API Key:</b>\n\n💡 <i>Публичный ключ от Mexc</i>", parseMode: ParseMode.Html, replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                        ConfigStates[userId] = "ApiKey";
                        break;
                        
                    case "❌ Отмена":
                        await botClient.SendMessage(chatId: userId, text: "❌ <b>Изменение настроек отменено</b>", parseMode: ParseMode.Html, replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                        ConfigStates.TryRemove(userId, out _);
                        break;
                        
                    default:
                        await botClient.SendMessage(chatId: userId, text: "❌ <b>Пожалуйста, выберите действие из меню</b>", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                        break;
                }
                return;
                
            case "OrderAmount":
                if (decimal.TryParse(data.Replace(",", "."), out value) && value >= 1m)
                {
                    user.Settings.OrderAmount = value;
                    await userRepository.UpdateAsync(user);
                    await botClient.SendMessage(chatId: userId, text: $"✅ <b>Сумма ордера обновлена:</b> <code>{value:F2} USDT</code>", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                    await ShowInlineConfigMenu(botClient, user, cancellationToken);
                }
                else
                {
                    await botClient.SendMessage(chatId: userId, text: "❌ <b>Ошибка!</b> Минимальная сумма ордера — 1 USDT", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                }
                return;
                
            case "MaxUsdtUsing":
                if (decimal.TryParse(data.Replace(",", "."), out value) && value > 0m)
                {
                    user.Settings.MaxUsdtUsing = value;
                    await userRepository.UpdateAsync(user);
                    await botClient.SendMessage(chatId: userId, text: $"✅ <b>Максимальная сумма обновлена:</b> <code>{value:F2} USDT</code>", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                    await ShowInlineConfigMenu(botClient, user, cancellationToken);
                }
                else
                {
                    await botClient.SendMessage(chatId: userId, text: "❌ <b>Ошибка!</b> Введите число больше 0.", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                }
                return;
                
            case "PercentPriceChange":
                if (decimal.TryParse(data.Replace(",", "."), out value) && value > 0m)
                {
                    user.Settings.PercentPriceChange = value;
                    await userRepository.UpdateAsync(user);
                    await botClient.SendMessage(chatId: userId, text: $"✅ <b>% падения обновлён:</b> <code>{value:F1}%</code>", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                    await ShowInlineConfigMenu(botClient, user, cancellationToken);
                }
                else
                {
                    await botClient.SendMessage(chatId: userId, text: "❌ <b>Ошибка!</b> Введите число больше 0.", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                }
                return;
                
            case "PercentProfit":
                if (decimal.TryParse(data.Replace(",", "."), out value) && value > 0m)
                {
                    user.Settings.PercentProfit = value;
                    await userRepository.UpdateAsync(user);
                    await botClient.SendMessage(chatId: userId, text: $"✅ <b>% прибыли обновлён:</b> <code>{value:F1}%</code>", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                    await ShowInlineConfigMenu(botClient, user, cancellationToken);
                }
                else
                {
                    await botClient.SendMessage(chatId: userId, text: "❌ <b>Ошибка!</b> Введите число больше 0.", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                }
                return;
                
            case "ApiKey":
                TempApiKeys[userId] = data;
                await botClient.SendMessage(chatId: userId, text: "Теперь введите новый API Secret:", cancellationToken: cancellationToken);
                ConfigStates[userId] = "ApiSecretUpdate";
                return;
                
            case "ApiSecretUpdate":
                string apiSecret;
                if (!TempApiKeys.TryGetValue(userId, out apiSecret))
                {
                    await botClient.SendMessage(chatId: userId, text: "Сначала введите новый API Key.", cancellationToken: cancellationToken);
                    ConfigStates[userId] = "ApiKey";
                    return;
                }
                
                var apiKey = data;
                var logger = _loggerFactory.CreateLogger<KaspaBot.Infrastructure.Services.MexcService>();
                var testResult = await KaspaBot.Infrastructure.Services.MexcService.Create(apiSecret, apiKey, logger).GetAccountInfoAsync(cancellationToken);
                
                if (!testResult.IsSuccess)
                {
                    await botClient.SendMessage(chatId: userId, text: $"Ошибка: ключи невалидны: {testResult.Errors.FirstOrDefault()?.Message}", cancellationToken: cancellationToken);
                    ConfigStates[userId] = "ApiKey";
                }
                else
                {
                    user.ApiCredentials.ApiKey = apiSecret;
                    user.ApiCredentials.ApiSecret = apiKey;
                    await userRepository.UpdateAsync(user);
                    await scope.ServiceProvider.GetRequiredService<KaspaBot.Infrastructure.Services.UserStreamManager>().ReloadUserAsync(user, cancellationToken);
                    await botClient.SendMessage(chatId: userId, text: "✅ <b>API ключи успешно обновлены и переподключены!</b>", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                    TempApiKeys.TryRemove(userId, out _);
                    await ShowInlineConfigMenu(botClient, user, cancellationToken);
                }
                return;
                
            case "DynamicOrderCoef":
                _logger.LogInformation($"[CONFIG] Ввод коэффициента: '{data}' для user={userId}");
                if (decimal.TryParse(data, out value) && value >= 1m && value <= 1000m)
                {
                    user.Settings.DynamicOrderCoef = value;
                    await userRepository.UpdateAsync(user);
                    await ShowInlineConfigMenu(botClient, user, cancellationToken);
                    _logger.LogInformation($"[CONFIG] Коэффициент обновлён: {value} для user={userId}");
                }
                else
                {
                    await botClient.SendMessage(userId, "Некорректный коэффициент. Введите число от 1 до 1000 или напишите 'Отмена'.", cancellationToken: cancellationToken);
                    _logger.LogWarning($"[CONFIG] Некорректный ввод коэффициента: '{data}' для user={userId}");
                }
                return;
        }
    }
}