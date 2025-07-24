using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using Telegram.Bot.Polling;

public static class TelegramBotExtensions
{
    public static IServiceCollection AddTelegramBot(this IServiceCollection services, IConfiguration configuration)
    {
        var token = configuration["Telegram:Token"] ??
            throw new ArgumentNullException("Telegram token is not configured");

        services.AddSingleton<ITelegramBotClient>(new TelegramBotClient(token));
        services.AddSingleton<IUpdateHandler, TelegramUpdateHandler>();
        services.AddHostedService<TelegramPollingService>();

        return services;
    }
}