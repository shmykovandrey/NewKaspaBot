namespace KaspaBot.Domain.Interfaces
{
    public interface IBotMessenger
    {
        Task SendMessage(long chatId, string text);
    }
} 