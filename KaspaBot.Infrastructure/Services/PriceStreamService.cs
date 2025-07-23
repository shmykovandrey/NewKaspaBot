using KaspaBot.Domain.Interfaces;
using Mexc.Net.Clients;
using Microsoft.Extensions.Logging;

namespace KaspaBot.Infrastructure.Services;

public class PriceStreamService : IPriceStreamService, IDisposable
{
    private readonly MexcSocketClient _socketClient;
    private readonly ILogger<PriceStreamService> _logger;
    private IDisposable _subscription;

    public PriceStreamService(ILogger<PriceStreamService> logger)
    {
        _logger = logger;
        _socketClient = new MexcSocketClient();
    }

    public async Task StartStreamAsync(string symbol, Action<decimal> onPriceUpdate)
    {
        var result = await _socketClient.SpotApi.SubscribeToMiniTickerUpdatesAsync(
            symbol,
            update => onPriceUpdate(update.Data.LastPrice));

        if (!result.Success)
        {
            _logger.LogError("WebSocket подписка не удалась: {Message}", result.Error?.Message);
        }
        else
        {
            _subscription = result.Data;
        }
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _socketClient?.Dispose();
    }
}
