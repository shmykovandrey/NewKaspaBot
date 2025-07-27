using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace KaspaBot.Infrastructure.Services
{
    public class CacheService
    {
        private readonly ConcurrentDictionary<string, (object Value, DateTime Expiry)> _cache = new();
        private readonly ILogger<CacheService> _logger;
        private readonly Timer _cleanupTimer;

        public CacheService(ILogger<CacheService> logger)
        {
            _logger = logger;
            _cleanupTimer = new Timer(CleanupExpiredItems, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }

        public T? Get<T>(string key) where T : class
        {
            if (_cache.TryGetValue(key, out var item) && DateTime.UtcNow < item.Expiry)
            {
                return (T)item.Value;
            }
            
            if (DateTime.UtcNow >= item.Expiry)
            {
                _cache.TryRemove(key, out _);
            }
            
            return null;
        }

        public void Set<T>(string key, T value, TimeSpan expiry) where T : class
        {
            _cache[key] = (value, DateTime.UtcNow.Add(expiry));
        }

        public void Remove(string key)
        {
            _cache.TryRemove(key, out _);
        }

        public void Clear()
        {
            _cache.Clear();
        }

        private void CleanupExpiredItems(object? state)
        {
            var now = DateTime.UtcNow;
            var expiredKeys = _cache
                .Where(kvp => now >= kvp.Value.Expiry)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                _cache.TryRemove(key, out _);
            }

            if (expiredKeys.Count > 0)
            {
                _logger.LogDebug("Cleaned up {Count} expired cache items", expiredKeys.Count);
            }
        }

        public void Dispose()
        {
            _cleanupTimer?.Dispose();
        }
    }
} 