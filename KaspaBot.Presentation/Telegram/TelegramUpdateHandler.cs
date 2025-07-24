using KaspaBot.Presentation.Telegram.CommandHandlers;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

public class TelegramUpdateHandler : IUpdateHandler
{
    private readonly TradingCommandHandler _tradingCommandHandler;
    private readonly ILogger<TelegramUpdateHandler> _logger;

    public TelegramUpdateHandler(
        TradingCommandHandler tradingCommandHandler,
        ILogger<TelegramUpdateHandler> logger)
    {
        _tradingCommandHandler = tradingCommandHandler;
        _logger = logger;
    }

    public async Task HandleUpdateAsync(
        ITelegramBotClient botClient,
        Update update,
        CancellationToken cancellationToken)
    {
        try
        {
            if (update.Type == UpdateType.Message && update.Message?.Text != null)
            {
                if (update.Message.Text.StartsWith("/buy"))
                {
                    await _tradingCommandHandler.HandleBuyCommand(update.Message, cancellationToken);
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
}