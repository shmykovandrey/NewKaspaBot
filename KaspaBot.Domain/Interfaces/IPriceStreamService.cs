namespace KaspaBot.Domain.Interfaces;

public interface IPriceStreamService : IDisposable
{
    Task StartStreamAsync(string symbol, Action<decimal> onPriceUpdate);
}