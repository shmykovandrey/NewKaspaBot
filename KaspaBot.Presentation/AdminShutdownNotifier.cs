using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using System;

public class AdminShutdownNotifier : IHostedService
{
    private readonly ITelegramBotClient _botClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AdminShutdownNotifier> _logger;
    private readonly IHostApplicationLifetime _appLifetime;
    private CancellationTokenRegistration? _stoppingRegistration;

    public AdminShutdownNotifier(
        ITelegramBotClient botClient,
        IConfiguration configuration,
        ILogger<AdminShutdownNotifier> logger,
        IHostApplicationLifetime appLifetime)
    {
        _botClient = botClient;
        _configuration = configuration;
        _logger = logger;
        _appLifetime = appLifetime;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingRegistration = _appLifetime.ApplicationStopping.Register(OnStopping);
        return Task.CompletedTask;
    }

    private async void OnStopping()
    {
        try
        {
            var adminId = _configuration["Telegram:AdminChatId"];
            if (long.TryParse(adminId, out var chatId))
            {
                await _botClient.SendMessage(
                    chatId: chatId,
                    text: "✅ Приложение остановлено.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при отправке финального сообщения админу");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stoppingRegistration?.Dispose();
        return Task.CompletedTask;
    }
} 