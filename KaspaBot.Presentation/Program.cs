using KaspaBot.Domain.Interfaces;
using KaspaBot.Infrastructure.Extensions;
using KaspaBot.Infrastructure.Services;
using KaspaBot.Presentation.Telegram;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

var builder = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        var configuration = context.Configuration;

        // Проверка обязательных настроек
        var mexcApiKey = configuration["Mexc:ApiKey"] ??
            throw new ArgumentNullException("Mexc:ApiKey is not configured");
        var mexcApiSecret = configuration["Mexc:ApiSecret"] ??
            throw new ArgumentNullException("Mexc:ApiSecret is not configured");
        var telegramToken = configuration["Telegram:Token"] ??
            throw new ArgumentNullException("Telegram:Token is not configured");

        services.AddSingleton<IMexcService>(provider =>
            new MexcService(
                mexcApiKey,
                mexcApiSecret,
                provider.GetRequiredService<ILogger<MexcService>>()));

        services.AddInfrastructure(configuration);
        services.AddTelegramBot(configuration);
    })
    .UseSerilog((context, config) =>
    {
        config.ReadFrom.Configuration(context.Configuration);
    });

await builder.Build().RunAsync();