using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace KaspaBot.Infrastructure.Services
{
    public class RateLimiter
    {
        private readonly ConcurrentDictionary<string, Queue<DateTime>> _requestHistory = new();
        private readonly ILogger<RateLimiter> _logger;
        private readonly Timer _cleanupTimer;

        public RateLimiter(ILogger<RateLimiter> logger)
        {
            _logger = logger;
            _cleanupTimer = new Timer(CleanupOldRequests, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        public bool IsAllowed(string key, int maxRequests, TimeSpan window)
        {
            var now = DateTime.UtcNow;
            var queue = _requestHistory.GetOrAdd(key, _ => new Queue<DateTime>());
            
            lock (queue)
            {
                // Удаляем старые запросы из окна
                while (queue.Count > 0 && now - queue.Peek() > window)
                {
                    queue.Dequeue();
                }

                // Проверяем лимит
                if (queue.Count >= maxRequests)
                {
                    _logger.LogWarning("Rate limit exceeded for key: {Key}, requests: {Count}/{Max}", 
                        key, queue.Count, maxRequests);
                    return false;
                }

                // Добавляем текущий запрос
                queue.Enqueue(now);
                return true;
            }
        }

        public async Task<bool> WaitForAllowanceAsync(string key, int maxRequests, TimeSpan window, 
            TimeSpan timeout = default, CancellationToken cancellationToken = default)
        {
            if (timeout == default)
                timeout = TimeSpan.FromSeconds(30);

            var startTime = DateTime.UtcNow;
            
            while (DateTime.UtcNow - startTime < timeout)
            {
                if (IsAllowed(key, maxRequests, window))
                    return true;

                await Task.Delay(100, cancellationToken);
            }

            _logger.LogError("Rate limit timeout exceeded for key: {Key}", key);
            return false;
        }

        public int GetCurrentRequests(string key)
        {
            if (_requestHistory.TryGetValue(key, out var queue))
            {
                lock (queue)
                {
                    return queue.Count;
                }
            }
            return 0;
        }

        private void CleanupOldRequests(object? state)
        {
            var now = DateTime.UtcNow;
            var keysToRemove = new List<string>();

            foreach (var kvp in _requestHistory)
            {
                var queue = kvp.Value;
                lock (queue)
                {
                    while (queue.Count > 0 && now - queue.Peek() > TimeSpan.FromHours(1))
                    {
                        queue.Dequeue();
                    }

                    if (queue.Count == 0)
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }
            }

            foreach (var key in keysToRemove)
            {
                _requestHistory.TryRemove(key, out _);
            }
        }

        public void Dispose()
        {
            _cleanupTimer?.Dispose();
        }
    }
} 