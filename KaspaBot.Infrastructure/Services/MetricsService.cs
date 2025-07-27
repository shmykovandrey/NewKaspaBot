using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace KaspaBot.Infrastructure.Services
{
    public class MetricsService
    {
        private readonly ConcurrentDictionary<string, long> _counters = new();
        private readonly ConcurrentDictionary<string, double> _gauges = new();
        private readonly ConcurrentDictionary<string, List<double>> _histograms = new();
        private readonly ILogger<MetricsService> _logger;
        private readonly Timer _reportingTimer;

        public MetricsService(ILogger<MetricsService> logger)
        {
            _logger = logger;
            _reportingTimer = new Timer(ReportMetrics, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }

        public void IncrementCounter(string name, long value = 1)
        {
            _counters.AddOrUpdate(name, value, (key, oldValue) => oldValue + value);
        }

        public void SetGauge(string name, double value)
        {
            _gauges.AddOrUpdate(name, value, (key, oldValue) => value);
        }

        public void RecordHistogram(string name, double value)
        {
            _histograms.AddOrUpdate(name, new List<double> { value }, (key, oldValue) =>
            {
                oldValue.Add(value);
                if (oldValue.Count > 100) // Ограничиваем размер
                {
                    oldValue.RemoveAt(0);
                }
                return oldValue;
            });
        }

        public long GetCounter(string name)
        {
            return _counters.GetValueOrDefault(name, 0);
        }

        public double GetGauge(string name)
        {
            return _gauges.GetValueOrDefault(name, 0);
        }

        public (double Min, double Max, double Avg, double P95) GetHistogram(string name)
        {
            if (!_histograms.TryGetValue(name, out var values) || !values.Any())
            {
                return (0, 0, 0, 0);
            }

            var sorted = values.OrderBy(v => v).ToList();
            var min = sorted.First();
            var max = sorted.Last();
            var avg = sorted.Average();
            var p95Index = (int)(sorted.Count * 0.95);
            var p95 = sorted[p95Index];

            return (min, max, avg, p95);
        }

        private void ReportMetrics(object? state)
        {
            var report = new System.Text.StringBuilder();
            report.AppendLine("=== METRICS REPORT ===");
            
            foreach (var counter in _counters)
            {
                report.AppendLine($"Counter {counter.Key}: {counter.Value}");
            }
            
            foreach (var gauge in _gauges)
            {
                report.AppendLine($"Gauge {gauge.Key}: {gauge.Value:F2}");
            }
            
            foreach (var histogram in _histograms)
            {
                var stats = GetHistogram(histogram.Key);
                report.AppendLine($"Histogram {histogram.Key}: Min={stats.Min:F2}, Max={stats.Max:F2}, Avg={stats.Avg:F2}, P95={stats.P95:F2}");
            }
            
            _logger.LogInformation(report.ToString());
        }

        public void Reset()
        {
            _counters.Clear();
            _gauges.Clear();
            _histograms.Clear();
        }

        public void Dispose()
        {
            _reportingTimer?.Dispose();
        }
    }
} 