using System.Collections.Concurrent;
using HdrHistogram;

namespace LoadTest.Metrics;

/// <summary>Parametri fissi per poter fondere istogrammi di agenti diversi (PROPOSTA-CONTRATTO per la sez. 8).</summary>
public static class HistogramSettings
{
    public const long MaxMicros = 3_600_000_000L;   // 1 ora
    public const int SignificantDigits = 3;
    public static LongHistogram Create() => new(MaxMicros, SignificantDigits);
}

public sealed class SeriesSnapshot
{
    public required LongHistogram Histogram { get; init; }
    public long Count { get; init; }
    public long Failed { get; init; }
    public long BytesSent { get; init; }
    public long BytesReceived { get; init; }
    public IReadOnlyDictionary<string, long> ErrorsByType { get; init; } = new Dictionary<string, long>();
}

public sealed class IntervalSnapshot
{
    public int Index { get; init; }
    public DateTimeOffset StartUtc { get; init; }
    public DateTimeOffset EndUtc { get; init; }
    public int ActiveUsers { get; init; }
    public required IReadOnlyDictionary<MetricKey, SeriesSnapshot> Series { get; init; }
}

/// <summary>Accumula misure per chiave; <see cref="Snapshot"/> chiude l'intervallo corrente e ne apre uno nuovo.</summary>
public sealed class MetricsRecorder(bool recordHistograms = true)
{
    private sealed class Series
    {
        private readonly object _gate = new();
        private LongHistogram _h = HistogramSettings.Create();
        private long _count, _failed, _sent, _recv;
        private Dictionary<string, long> _errors = new();

        public void Add(long micros, bool success, string? errorType, long sent, long recv, bool hist)
        {
            lock (_gate)
            {
                _count++;
                if (!success)
                {
                    _failed++;
                    var k = errorType ?? "Unknown";
                    _errors[k] = _errors.GetValueOrDefault(k) + 1;
                }
                _sent += sent;
                _recv += recv;
                if (hist && micros > 0) _h.RecordValue(Math.Min(micros, HistogramSettings.MaxMicros));
            }
        }

        public SeriesSnapshot? Take()
        {
            lock (_gate)
            {
                if (_count == 0) return null;
                var snap = new SeriesSnapshot
                {
                    Histogram = _h, Count = _count, Failed = _failed,
                    BytesSent = _sent, BytesReceived = _recv, ErrorsByType = _errors
                };
                _h = HistogramSettings.Create();
                _errors = new Dictionary<string, long>();
                _count = _failed = _sent = _recv = 0;
                return snap;
            }
        }
    }

    private readonly ConcurrentDictionary<MetricKey, Series> _series = new();

    public void Record(MetricKey key, long micros, bool success, string? errorType = null, long bytesSent = 0, long bytesReceived = 0)
        => _series.GetOrAdd(key, _ => new Series()).Add(micros, success, errorType, bytesSent, bytesReceived, recordHistograms);

    public IntervalSnapshot Snapshot(int index, DateTimeOffset startUtc, DateTimeOffset endUtc, int activeUsers)
    {
        var result = new Dictionary<MetricKey, SeriesSnapshot>();
        foreach (var (key, series) in _series)
            if (series.Take() is { } s) result[key] = s;
        return new IntervalSnapshot { Index = index, StartUtc = startUtc, EndUtc = endUtc, ActiveUsers = activeUsers, Series = result };
    }
}
