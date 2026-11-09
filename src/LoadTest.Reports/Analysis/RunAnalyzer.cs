using LoadTest.Metrics;

namespace LoadTest.Reports.Analysis;

public sealed record SeriesSummary(
    MetricKey Key, long Count, long Failed, long BytesSent, long BytesReceived,
    double? MeanMs, double? P50Ms, double? P90Ms, double? P95Ms, double? P99Ms, double? MaxMs,
    double PerSecond, IReadOnlyDictionary<string, long> ErrorsByType, bool AgentSaturated)
{
    public double ErrorRatePercent => Count == 0 ? 0 : 100.0 * Failed / Count;
}

/// <param name="MeasuredSeconds">Somma delle durate degli intervalli (un intervallo contato una volta anche con più agenti).</param>
public sealed record RunSummary(double MeasuredSeconds, IReadOnlyList<SeriesSummary> Series);

/// <summary>
/// Riassunto di una run: gli istogrammi di intervalli e agenti diversi vengono FUSI e i percentili letti dall'istogramma fuso
/// (CONTRATTO sez. 1: mai medie di percentili). Se timingDetail = none gli istogrammi sono vuoti e i tempi restano null.
/// </summary>
public static class RunAnalyzer
{
    public static RunSummary Summarize(IEnumerable<SeriesSample> samples)
    {
        var list = samples as IReadOnlyList<SeriesSample> ?? samples.ToList();
        var seconds = list.GroupBy(s => s.IntervalIndex).Sum(g => g.Max(x => (x.EndUtc - x.StartUtc).TotalSeconds));

        var result = new List<SeriesSummary>();
        foreach (var g in list.GroupBy(s => s.Key))
        {
            var merged = HistogramSettings.Create();
            long count = 0, failed = 0, sent = 0, received = 0;
            var errors = new Dictionary<string, long>();
            var saturated = false;

            foreach (var s in g)
            {
                merged.Add(s.Series.Histogram);
                count += s.Series.Count;
                failed += s.Series.Failed;
                sent += s.Series.BytesSent;
                received += s.Series.BytesReceived;
                saturated |= s.AgentSaturated;
                foreach (var (type, n) in s.Series.ErrorsByType) errors[type] = errors.GetValueOrDefault(type) + n;
            }

            var hasTimes = merged.TotalCount > 0;
            double? Pct(double p) => hasTimes ? merged.GetValueAtPercentile(p) / 1000.0 : null;

            // HdrHistogram 2.5.0 non espone media e massimo: li ricavo dai valori registrati (valore equivalente del bucket, errore < 0,1%).
            double sumMicros = 0;
            long maxMicros = 0;
            foreach (var v in merged.RecordedValues())
            {
                sumMicros += (double)v.ValueIteratedTo * v.CountAtValueIteratedTo;
                if (v.ValueIteratedTo > maxMicros) maxMicros = v.ValueIteratedTo;
            }

            result.Add(new SeriesSummary(
                g.Key, count, failed, sent, received,
                hasTimes ? sumMicros / merged.TotalCount / 1000.0 : null,
                Pct(50), Pct(90), Pct(95), Pct(99),
                hasTimes ? maxMicros / 1000.0 : null,
                seconds > 0 ? count / seconds : 0, errors, saturated));
        }

        var ordered = result
            .OrderBy(x => x.Key.Scenario, StringComparer.Ordinal)
            .ThenBy(x => x.Key.Test, StringComparer.Ordinal)
            .ThenBy(x => x.Key.Kind)
            .ThenBy(x => x.Key.Page, StringComparer.Ordinal)
            .ThenBy(x => x.Key.Request, StringComparer.Ordinal)
            .ThenBy(x => x.Key.Transaction, StringComparer.Ordinal)
            .ToList();
        return new RunSummary(seconds, ordered);
    }
}
