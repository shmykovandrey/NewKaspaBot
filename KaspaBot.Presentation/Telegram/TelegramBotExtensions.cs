// KaspaBot.Presentation/Telegram/TelegramBotExtensions.cs
using Microsoft.Extensions.Configuration; // Добавить эту строку
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;

namespace KaspaBot.Presentation.Telegram;

public static class TelegramBotExtensions
{
    public static IServiceCollection AddTelegramBot(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ITelegramBotClient>(_ =>
            new TelegramBotClient(configuration["Telegram:Token"]));

        return services;
    }
}