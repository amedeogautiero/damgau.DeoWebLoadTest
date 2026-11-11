using LoadTest.Contracts;

namespace LoadTest.Metrics;

/// <summary>
/// Aggrega gli intervalli misurati in uno snapshot cumulativo di avanzamento.
/// Riceve gli stessi IntervalSnapshot già prodotti dal MetricsRecorder: non ricalcola
/// le misure HTTP e non introduce un secondo percorso di raccolta.
/// </summary>
/// <remarks>
/// Un'istanza rappresenta una singola sorgente di metriche (ad esempio il runner locale
/// oppure un singolo agent). La fusione di più agenti e la deduplicazione degli intervalli
/// distribuiti appartengono al livello di orchestrazione che verrà realizzato in seguito.
/// </remarks>
public sealed class RunProgressAggregator
{
    private sealed class AggregateSeries
    {
        public LongHistogram Histogram { get; } = HistogramSettings.Create();
        public long Count;
        public long Failed;
        public long BytesSent;
        public long BytesReceived;

        public void Add(SeriesSnapshot snapshot)
        {
            Histogram.Add(snapshot.Histogram);
            Count += snapshot.Count;
            Failed += snapshot.Failed;
            BytesSent += snapshot.BytesSent;
            BytesReceived += snapshot.BytesReceived;
        }
    }

    private readonly object _gate = new();
    private readonly string _runId;
    private readonly Dictionary<MetricKey, AggregateSeries> _series = [];
    private long _sequence;
    private double _measuredSeconds;
    private int _activeUsers;
    private DateTimeOffset? _lastCapturedUtc;

    public RunProgressAggregator(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
            throw new ArgumentException("RunId non può essere vuoto.", nameof(runId));

        _runId = runId;
    }

    /// <summary>Aggiunge un intervallo già raccolto dal motore.</summary>
    public void AddInterval(IntervalSnapshot interval)
    {
        ArgumentNullException.ThrowIfNull(interval);

        lock (_gate)
        {
            var seconds = (interval.EndUtc - interval.StartUtc).TotalSeconds;
            if (seconds > 0)
                _measuredSeconds += seconds;

            _activeUsers = interval.ActiveUsers;
            _lastCapturedUtc = interval.EndUtc;

            foreach (var (key, snapshot) in interval.Series)
            {
                if (!_series.TryGetValue(key, out var aggregate))
                {
                    aggregate = new AggregateSeries();
                    _series.Add(key, aggregate);
                }

                aggregate.Add(snapshot);
            }
        }
    }

    /// <summary>
    /// Crea uno snapshot cumulativo. Lo stato è fornito dal chiamante perché
    /// la gestione del ciclo di vita del run non appartiene al sistema di metriche.
    /// </summary>
    public RunProgressSnapshot CreateSnapshot(
        RunProgressStatus status,
        DateTimeOffset? startedUtc = null,
        DateTimeOffset? capturedUtc = null)
    {
        lock (_gate)
        {
            var captured = capturedUtc ?? _lastCapturedUtc ?? DateTimeOffset.UtcNow;
            var series = _series
                .OrderBy(x => x.Key.Scenario, StringComparer.Ordinal)
                .ThenBy(x => x.Key.Test, StringComparer.Ordinal)
                .ThenBy(x => x.Key.Kind)
                .ThenBy(x => x.Key.Page, StringComparer.Ordinal)
                .ThenBy(x => x.Key.Request, StringComparer.Ordinal)
                .ThenBy(x => x.Key.Transaction, StringComparer.Ordinal)
                .Select(x => ToProgressSeries(x.Key, x.Value))
                .ToArray();

            var tests = _series
                .Where(x => x.Key.Kind == MetricKind.Test)
                .Select(x => x.Value)
                .ToArray();
            var requests = _series
                .Where(x => x.Key.Kind == MetricKind.Request)
                .Select(x => x.Value)
                .ToArray();

            var completedTests = tests.Sum(x => x.Count);
            var failedTests = tests.Sum(x => x.Failed);
            var completedRequests = requests.Sum(x => x.Count);
            var failedRequests = requests.Sum(x => x.Failed);
            var elapsed = startedUtc is { } start
                ? Math.Max(0, (captured - start).TotalSeconds)
                : _measuredSeconds;

            return new RunProgressSnapshot
            {
                RunId = _runId,
                Sequence = ++_sequence,
                Status = status,
                StartedUtc = startedUtc,
                CapturedUtc = captured,
                ElapsedSeconds = elapsed,
                ActiveUsers = _activeUsers,
                CompletedTests = completedTests,
                FailedTests = failedTests,
                CompletedRequests = completedRequests,
                FailedRequests = failedRequests,
                RequestsPerSecond = _measuredSeconds > 0
                    ? completedRequests / _measuredSeconds
                    : 0,
                Series = series
            };
        }
    }

    private static RunProgressSeriesSnapshot ToProgressSeries(MetricKey key, AggregateSeries aggregate)
    {
        var histogram = aggregate.Histogram;
        var hasTimings = histogram.TotalCount > 0;

        double Percentile(double percentile) =>
            hasTimings ? histogram.GetValueAtPercentile(percentile) / 1000.0 : 0;

        long maxMicros = 0;
        if (hasTimings)
        {
            foreach (var value in histogram.RecordedValues())
                if (value.ValueIteratedTo > maxMicros)
                    maxMicros = value.ValueIteratedTo;
        }

        return new RunProgressSeriesSnapshot
        {
            Kind = key.Kind switch
            {
                MetricKind.Test => RunProgressSeriesKind.Test,
                MetricKind.Transaction => RunProgressSeriesKind.Transaction,
                _ => RunProgressSeriesKind.Request
            },
            ScenarioName = key.Scenario,
            TestName = key.Test,
            PageName = key.Page,
            RequestName = key.Request,
            TransactionName = key.Transaction,
            Count = aggregate.Count,
            Failed = aggregate.Failed,
            ErrorRatePercent = aggregate.Count > 0
                ? 100.0 * aggregate.Failed / aggregate.Count
                : 0,
            OperationsPerSecond = aggregate.Count,
            P50Milliseconds = Percentile(50),
            P95Milliseconds = Percentile(95),
            P99Milliseconds = Percentile(99),
            MaxMilliseconds = hasTimings ? maxMicros / 1000.0 : 0,
            BytesSent = aggregate.BytesSent,
            BytesReceived = aggregate.BytesReceived
        };
    }
}
