using System.Globalization;
using LoadTest.Controller.Api;

namespace LoadTest.Cli.Ci;

public sealed class GateOptions
{
    public double? MaxErrorRatePercent { get; init; }
    public double? MaxP95Ms { get; init; }
    public double? MaxP99Ms { get; init; }
    public double? MinRequestsPerSecond { get; init; }
    public long? MinRequests { get; init; }
    public long? MaxFailedTests { get; init; }
}

public sealed record CheckResult(string Name, bool Passed, string Detail);

/// <summary>
/// Verifiche per la CI. Sempre presenti: run completata e almeno una richiesta misurata (una run vuota non deve risultare verde).
/// Le soglie sui tempi falliscono se i tempi non ci sono (timingDetail: none): meglio un falso rosso di un falso verde.
/// </summary>
public static class ThresholdEvaluator
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public static string F(double v) => v.ToString("0.##", Inv);

    public static string Label(SeriesDto s) => s.Page ?? s.Request ?? s.Transaction ?? s.Test;

    public static IReadOnlyList<CheckResult> Evaluate(RunSummaryDto s, GateOptions g)
    {
        var checks = new List<CheckResult>();
        var completed = s.Status == "Completed";
        checks.Add(new CheckResult("run.completed", completed,
            completed ? "run completata" : $"stato {s.Status}" + (string.IsNullOrEmpty(s.Reason) ? "" : $": {s.Reason}")));

        var requests = s.Series.Where(x => x.Kind == "Request").ToList();
        var total = requests.Sum(x => x.Count);
        var failed = requests.Sum(x => x.Failed);
        checks.Add(new CheckResult("requests.measured", total > 0, $"{total} richieste misurate"));

        if (g.MinRequests is { } minReq)
            checks.Add(new CheckResult("requests.min", total >= minReq, $"{total} richieste (minimo {minReq})"));

        if (g.MaxErrorRatePercent is { } maxRate)
        {
            var rate = total == 0 ? 100.0 : 100.0 * failed / total;
            checks.Add(new CheckResult("errors.rate", rate <= maxRate, $"{F(rate)}% di richieste fallite (massimo {F(maxRate)}%)"));
        }

        if (g.MaxFailedTests is { } maxFailed)
            checks.Add(new CheckResult("tests.failed", s.TestsFailed <= maxFailed, $"{s.TestsFailed} test falliti (massimo {maxFailed})"));

        if (g.MinRequestsPerSecond is { } minRps)
        {
            var rps = s.MeasuredSeconds > 0 ? total / s.MeasuredSeconds : 0;
            checks.Add(new CheckResult("throughput.rps", rps >= minRps, $"{F(rps)} richieste/s (minimo {F(minRps)})"));
        }

        if (g.MaxP95Ms is { } p95) checks.Add(Percentile("latency.p95", requests, x => x.P95Ms, p95));
        if (g.MaxP99Ms is { } p99) checks.Add(Percentile("latency.p99", requests, x => x.P99Ms, p99));
        return checks;
    }

    private static CheckResult Percentile(string name, List<SeriesDto> requests, Func<SeriesDto, double?> pick, double max)
    {
        var withTimes = requests.Where(x => pick(x) is not null).ToList();
        if (withTimes.Count == 0) return new CheckResult(name, false, "nessun tempo disponibile (timingDetail: none?)");
        var worst = withTimes.MaxBy(x => pick(x)!.Value)!;
        var v = pick(worst)!.Value;
        return new CheckResult(name, v <= max, $"peggiore '{Label(worst)}': {F(v)} ms (massimo {F(max)} ms)");
    }
}
