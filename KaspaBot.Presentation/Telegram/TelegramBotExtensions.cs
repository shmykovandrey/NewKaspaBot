using KaspaBot.Presentation.Telegram.CommandHandlers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;

namespace KaspaBot.Presentation.Telegram;

public static class TelegramBotExtensions
{
    public static IServiceCollection AddTelegramBot(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ITelegramBotClient>(_ =>
            new TelegramBotClient(configuration["Telegram:Token"]));

        services.AddScoped<TradingCommandHandler>();

        // Добавим хостинг Telegram-пула
        services.AddHostedService<TelegramPollingService>();

        return services;
    }
}
