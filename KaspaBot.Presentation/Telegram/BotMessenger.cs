using System.Threading.Tasks;
using KaspaBot.Domain.Interfaces;
using Telegram.Bot;

namespace KaspaBot.Presentation.Telegram
{
    public class BotMessenger : IBotMessenger
    {
        private readonly ITelegramBotClient _botClient;
        public BotMessenger(ITelegramBotClient botClient)
        {
            _botClient = botClient;
        }
        public async Task SendMessage(long chatId, string text)
        {
            await _botClient.SendMessage(chatId, text);
        }
    }
} 