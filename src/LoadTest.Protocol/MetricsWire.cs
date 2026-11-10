using Google.Protobuf.WellKnownTypes;
using LoadTest.Metrics;

namespace LoadTest.Protocol;

/// <summary>
/// Conversione fra <see cref="IntervalSnapshot"/> (Metrics) e <see cref="MetricsBatch"/> (filo).
/// Lato agente: ToBatch a ogni campione (aggancio: LoadTestRunnerOptions.OnInterval). Lato controller: FromBatch, poi IResultsRepository.WriteInterval.
/// I dati in ingresso a FromBatch non sono fidati: ogni anomalia lancia InvalidDataException.
/// </summary>
public static class MetricsWire
{
    public static MetricsBatch ToBatch(string runId, IntervalSnapshot interval, HistogramFormat format = HistogramFormat.VarintDeflateV1)
    {
        var batch = new MetricsBatch
        {
            RunId = runId,
            IntervalIndex = interval.Index,
            StartUtc = Timestamp.FromDateTimeOffset(interval.StartUtc),
            EndUtc = Timestamp.FromDateTimeOffset(interval.EndUtc),
            ActiveUsers = interval.ActiveUsers
        };

        foreach (var (key, s) in interval.Series)
        {
            var sample = new SeriesSample
            {
                Key = ToKey(key),
                Count = s.Count,
                Failed = s.Failed,
                BytesSent = s.BytesSent,
                BytesReceived = s.BytesReceived
            };
            foreach (var (type, n) in s.ErrorsByType) sample.ErrorsByType.Add(type, n);
            if (s.Histogram.TotalCount > 0)
            {
                sample.HistFormat = format;
                sample.Hist = WireHistogram.Encode(s.Histogram, format);
            }
            batch.Series.Add(sample);
        }
        return batch;
    }

    public static IntervalSnapshot FromBatch(MetricsBatch batch)
    {
        if (batch.StartUtc is null || batch.EndUtc is null) throw new InvalidDataException("MetricsBatch senza start_utc/end_utc");
        var start = batch.StartUtc.ToDateTimeOffset();
        var end = batch.EndUtc.ToDateTimeOffset();
        if (end < start) throw new InvalidDataException("MetricsBatch con end_utc precedente a start_utc");
        if (batch.IntervalIndex < 0) throw new InvalidDataException("interval_index negativo");

        var series = new Dictionary<MetricKey, SeriesSnapshot>();
        foreach (var s in batch.Series)
        {
            if (s.Key is null) throw new InvalidDataException("SeriesSample senza key");
            if (s.Count < 0 || s.Failed < 0 || s.Failed > s.Count) throw new InvalidDataException("conteggi di serie non validi");
            var key = FromKey(s.Key);
            if (series.ContainsKey(key)) throw new InvalidDataException($"serie duplicata nel batch: {key}");
            series[key] = new SeriesSnapshot
            {
                Histogram = WireHistogram.Decode(s.HistFormat, s.Hist),
                Count = s.Count,
                Failed = s.Failed,
                BytesSent = s.BytesSent,
                BytesReceived = s.BytesReceived,
                ErrorsByType = s.ErrorsByType.ToDictionary(kv => kv.Key, kv => kv.Value)
            };
        }

        return new IntervalSnapshot
        {
            Index = batch.IntervalIndex,
            StartUtc = start,
            EndUtc = end,
            ActiveUsers = batch.ActiveUsers,
            Series = series
        };
    }

    private static SeriesKey ToKey(MetricKey k)
    {
        var key = new SeriesKey { Scenario = k.Scenario, Test = k.Test };
        if (k.Page is not null) key.Page = k.Page;
        if (k.Request is not null) key.Request = k.Request;
        if (k.Transaction is not null) key.Transaction = k.Transaction;
        return key;
    }

    private static MetricKey FromKey(SeriesKey k) => new(
        k.Scenario, k.Test,
        k.HasPage ? k.Page : null,
        k.HasRequest ? k.Request : null,
        k.HasTransaction ? k.Transaction : null);
}
