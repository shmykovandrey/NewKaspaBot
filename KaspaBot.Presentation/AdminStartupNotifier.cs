using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using KaspaBot.Domain.Interfaces;
using Telegram.Bot;
using System;
using Telegram.Bot.Types;
using KaspaBot.Infrastructure.Services;

public class AdminStartupNotifier : IHostedService
{
    private readonly IUserRepository _userRepository;
    private readonly ITelegramBotClient _botClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AdminStartupNotifier> _logger;
    private bool _notified = false;
    private readonly UserStreamManager _userStreamManager;

    public AdminStartupNotifier(
        IUserRepository userRepository,
        ITelegramBotClient botClient,
        IConfiguration configuration,
        ILogger<AdminStartupNotifier> logger,
        UserStreamManager userStreamManager)
    {
        _userRepository = userRepository;
        _botClient = botClient;
        _configuration = configuration;
        _logger = logger;
        _userStreamManager = userStreamManager;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_notified) return;
        _notified = true;
        try
        {
            var adminId = _configuration["Telegram:AdminChatId"];
            if (long.TryParse(adminId, out var chatIdValue))
            {
                var chatId = new ChatId(chatIdValue);
                var users = await _userRepository.GetAllAsync();
                var text = $"✅ Приложение успешно запущено! Количество пользователей в базе: {users.Count}";
                await _botClient.SendMessage(
                    chatId: chatId,
                    text: text,
                    cancellationToken: cancellationToken);
            }
            // Инициализация listenKey и подключений для всех пользователей
            await _userStreamManager.InitializeAllAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при отправке уведомления админу о старте приложения");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
} 