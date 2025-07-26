//Program.cs
using KaspaBot.Domain.Interfaces;
using KaspaBot.Infrastructure.Extensions;
using KaspaBot.Infrastructure.Services;
using KaspaBot.Presentation.Telegram;
using KaspaBot.Presentation.Telegram.CommandHandlers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types;
using System.Threading;
using KaspaBot.Presentation;

var builder = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        var configuration = context.Configuration;

        // Добавление инфраструктуры (должно быть первым)
        services.AddInfrastructure(configuration);

        // Проверка обязательных настроек
        var mexcApiKey = configuration["Mexc:ApiKey"] ??
            throw new ArgumentNullException("Mexc:ApiKey is not configured");
        var mexcApiSecret = configuration["Mexc:ApiSecret"] ??
            throw new ArgumentNullException("Mexc:ApiSecret is not configured");
        var telegramToken = configuration["Telegram:Token"] ??
            throw new ArgumentNullException("Telegram:Token is not configured");

        // Регистрация Telegram бота
        services.AddSingleton<ITelegramBotClient>(new TelegramBotClient(telegramToken));

        // Регистрация обработчиков команд
        services.AddScoped<TradingCommandHandler>();

        // Регистрация обработчика обновлений с поддержкой IHostApplicationLifetime и ILoggerFactory
        services.AddScoped<IUpdateHandler, TelegramUpdateHandler>(provider =>
            new TelegramUpdateHandler(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<ILogger<TelegramUpdateHandler>>(),
                provider.GetRequiredService<IHostApplicationLifetime>(),
                provider.GetRequiredService<ILoggerFactory>()));

        // Регистрация фонового сервиса
        services.AddHostedService<TelegramPollingService>();
        // Уведомление админу о старте
        services.AddHostedService<AdminStartupNotifier>();
        // Уведомление админу о завершении
        services.AddHostedService<AdminShutdownNotifier>();
        // Восстановление статусов ордеров
        services.AddHostedService<OrderRecoveryService>();
        services.AddSingleton<OrderRecoveryService>();
        services.AddSingleton<IOrderRecoveryService>(sp => sp.GetRequiredService<OrderRecoveryService>());
        // DCA автоторговля
        services.AddHostedService<DcaBuyService>();
        services.AddSingleton<IBotMessenger, KaspaBot.Presentation.Telegram.BotMessenger>();
    })
    .UseSerilog((context, config) =>
    {
        config.ReadFrom.Configuration(context.Configuration);
    });

var host = builder.Build();

// Подписка на событие продажи
using (var scope = host.Services.CreateScope())
{
    var userStreamManager = scope.ServiceProvider.GetRequiredService<UserStreamManager>();
    var botClient = scope.ServiceProvider.GetRequiredService<ITelegramBotClient>();
    userStreamManager.OnOrderSold += async (userId, qty, price, usdt, profit) =>
    {
        var msg = $"ПРОДАНО\n\n{qty:F2} KAS по {price:F6} USDT\n\nПолучено\n{usdt:F8} USDT\n\nПРИБЫЛЬ\n{profit:F8} USDT";
        await botClient.SendMessage(chatId: userId, text: msg);
    };
}

// Получаем сервисы
// (Блок отправки сообщения админу удалён, теперь это делает только AdminStartupNotifier)

await host.RunAsync();