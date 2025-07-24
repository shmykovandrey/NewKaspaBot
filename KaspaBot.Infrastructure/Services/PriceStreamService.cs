using CryptoExchange.Net.Objects.Sockets;
using CryptoExchange.Net.Sockets;
using KaspaBot.Domain.Interfaces;
using Mexc.Net.Clients;
using Mexc.Net.Interfaces.Clients;
using Mexc.Net.Objects.Models.Spot;
using Microsoft.Extensions.Logging;

namespace KaspaBot.Infrastructure.Services
{
    public class PriceStreamService : IPriceStreamService, IDisposable
    {
        private readonly IMexcSocketClient _socketClient;
        private readonly ILogger<PriceStreamService> _logger;
        private UpdateSubscription? _subscription;

        public PriceStreamService(ILogger<PriceStreamService> logger)
        {
            _socketClient = new MexcSocketClient();
            _logger = logger;
        }

        public async Task StartStreamAsync(string symbol, Action<decimal> onPriceUpdate)
        {
            try
            {
                _logger.LogInformation("Subscribing to price updates for {Symbol}...", symbol);

                var result = await _socketClient.SpotApi.SubscribeToMiniTickerUpdatesAsync(
                    symbol,
                    update =>
                    {
                        onPriceUpdate(update.Data.LastPrice);
                    },
                    CancellationToken.None.ToString());

                if (!result.Success)
                {
                    _logger.LogError("Failed to subscribe to price updates: {Error}", result.Error);
                    throw new Exception($"Subscription error: {result.Error}");
                }

                _subscription = result.Data;
                _logger.LogInformation("Successfully subscribed to price updates for {Symbol}", symbol);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error subscribing to price updates: {Message}", ex.Message);
                throw;
            }
        }

        public void Dispose()
        {
            try
            {
                if (_subscription != null)
                {
                    _socketClient.UnsubscribeAsync(_subscription).GetAwaiter().GetResult();
                }
                _socketClient?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error unsubscribing from price updates");
            }
            finally
            {
                GC.SuppressFinalize(this);
            }
        }
    }
}