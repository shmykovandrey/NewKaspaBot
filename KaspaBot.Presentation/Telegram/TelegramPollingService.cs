using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

public class TelegramPollingService : BackgroundService
{
    private readonly ITelegramBotClient _botClient;
    private readonly IUpdateHandler _updateHandler;
    private readonly ILogger<TelegramPollingService> _logger;

    public TelegramPollingService(
        ITelegramBotClient botClient,
        IUpdateHandler updateHandler,
        ILogger<TelegramPollingService> logger)
    {
        _botClient = botClient;
        _updateHandler = updateHandler;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = Array.Empty<UpdateType>()
        };

        _logger.LogInformation("Starting telegram bot polling...");

        // Просто запускаем получение обновлений без очистки очереди
        await _botClient.ReceiveAsync(
            updateHandler: _updateHandler,
            receiverOptions: receiverOptions,
            cancellationToken: stoppingToken);

        _logger.LogInformation("Telegram bot polling is running...");
    }
}