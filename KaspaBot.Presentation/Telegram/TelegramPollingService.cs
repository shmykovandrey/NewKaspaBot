using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

public class TelegramPollingService : BackgroundService
{
    private readonly ITelegramBotClient _botClient;

    public TelegramPollingService(ITelegramBotClient botClient)
    {
        _botClient = botClient;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _botClient.StartReceiving(
            updateHandler: async (client, update, token) =>
            {
                if (update.Type == UpdateType.Message && update.Message?.Text != null)
                {
                    await client.SendTextMessageAsync(
                        chatId: update.Message.Chat.Id,
                        text: "Команда получена",
                        cancellationToken: token);
                }
            },
            pollingErrorHandler: (_, exception, _) =>
            {
                Console.WriteLine($"Ошибка Telegram: {exception.Message}");
                return Task.CompletedTask;
            },
            cancellationToken: stoppingToken
        );

        Console.WriteLine("Telegram бот запущен...");
        await Task.Delay(-1, stoppingToken);
    }
}
