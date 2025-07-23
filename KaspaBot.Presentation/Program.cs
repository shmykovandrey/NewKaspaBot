// KaspaBot.Presentation/Program.cs
using KaspaBot.Infrastructure.Extensions;
using KaspaBot.Presentation.Telegram;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;

var builder = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        services.AddInfrastructure(context.Configuration);
        services.AddTelegramBot(context.Configuration); // Передаем конфигурацию
    })
    .UseSerilog((context, config) =>
    {
        config.ReadFrom.Configuration(context.Configuration);
    });

var host = builder.Build();
await host.RunAsync();