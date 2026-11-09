using LoadTest.Metrics;
using LoadTest.Reports.Analysis;
using LoadTest.Reports.Export;
using LoadTest.Reports.Storage;

namespace LoadTest.Reports.Tests;

public class ReportsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
    private static readonly MetricKey Home = new("Utenti", "ricerca", Page: "Home", Request: "https://x.it/");

    private static IntervalSnapshot Interval(MetricKey key, int index, int users, params (long micros, bool ok)[] values)
    {
        var rec = new MetricsRecorder();
        foreach (var (micros, ok) in values) rec.Record(key, micros, ok, ok ? null : "Timeout", 100, 2000);
        return rec.Snapshot(index, T0.AddSeconds(index * 5), T0.AddSeconds((index + 1) * 5), users);
    }

    private static (long, bool)[] Repeat(long micros, int n) => Enumerable.Repeat((micros, true), n).ToArray();

    [Fact]
    public void Codec_roundtrips_counts_and_percentiles()
    {
        var h = HistogramSettings.Create();
        foreach (var v in new long[] { 800, 1000, 1000, 2500, 9000, 120_000, 3_000_000 }) h.RecordValue(v);

        var back = HistogramCodec.Decode(HistogramCodec.Encode(h));

        Assert.Equal(h.TotalCount, back.TotalCount);
        foreach (var p in new[] { 50.0, 90.0, 99.0 })
            Assert.Equal(h.GetValueAtPercentile(p), back.GetValueAtPercentile(p));
        Assert.Empty(HistogramCodec.Decode(null).RecordedValues());
    }

    [Fact]
    public void Repository_roundtrips_run_intervals_errors_and_annotations()
    {
        using var repo = new SqliteResultsRepository(":memory:");
        repo.BeginRun(new RunRecord("r1", "Carico", T0, "version: 1"));
        repo.WriteInterval("r1", "local", Interval(Home, 0, 10, (1000, true), (2000, false)));
        repo.WriteErrorDetails("r1", [new ErrorDetail("Utenti", "ricerca", "Home", "https://x.it/", null, 0, "Timeout", "scaduto")]);
        repo.AddAnnotation("r1", T0.AddSeconds(3), "deploy");
        repo.CompleteRun("r1", new RunCompletion(T0.AddMinutes(1), "Completed", null, 5, 1, 1, ["avviso"]));

        var run = repo.GetRun("r1")!;
        Assert.Equal("Completed", run.Status);
        Assert.Equal("version: 1", run.DefinitionYaml);
        Assert.Equal("avviso", Assert.Single(run.Warnings));

        var sample = Assert.Single(repo.ReadSamples("r1"));
        Assert.Equal(Home, sample.Key);
        Assert.Equal(2, sample.Series.Count);
        Assert.Equal(1, sample.Series.Failed);
        Assert.Equal(1, sample.Series.ErrorsByType["Timeout"]);
        Assert.Equal(4000, sample.Series.BytesReceived);
        Assert.Equal(2, sample.Series.Histogram.TotalCount);

        Assert.Equal(10, Assert.Single(repo.ReadIntervals("r1")).ActiveUsers);
        Assert.Equal("Timeout", Assert.Single(repo.ReadErrorDetails("r1")).ErrorType);
        Assert.Equal("deploy", Assert.Single(repo.ReadAnnotations("r1")).Text);
    }

    [Fact]
    public void Writing_the_same_interval_twice_replaces_it()
    {
        using var repo = new SqliteResultsRepository(":memory:");
        repo.BeginRun(new RunRecord("r1", "Carico", T0));
        repo.WriteInterval("r1", "local", Interval(Home, 0, 10, Repeat(1000, 5)));
        repo.WriteInterval("r1", "local", Interval(Home, 0, 10, Repeat(1000, 3)));

        Assert.Equal(3, Assert.Single(repo.ReadSamples("r1")).Series.Count);
    }

    [Fact]
    public void Keys_without_optional_parts_roundtrip_as_null()
    {
        using var repo = new SqliteResultsRepository(":memory:");
        repo.BeginRun(new RunRecord("r1", "Carico", T0));
        var testKey = new MetricKey("Utenti", "ricerca");
        var txKey = new MetricKey("Utenti", "ricerca", Transaction: "Autenticazione");
        var rec = new MetricsRecorder();
        rec.Record(testKey, 5000, true);
        rec.Record(txKey, 3000, true);
        repo.WriteInterval("r1", "local", rec.Snapshot(0, T0, T0.AddSeconds(5), 1));

        var keys = repo.ReadSamples("r1").Select(s => s.Key).ToHashSet();
        Assert.Contains(testKey, keys);
        Assert.Contains(txKey, keys);
        Assert.Contains(keys, k => k.Kind == MetricKind.Transaction);
    }

    [Fact]
    public void Summary_merges_histograms_across_agents_instead_of_averaging_percentiles()
    {
        using var repo = new SqliteResultsRepository(":memory:");
        repo.BeginRun(new RunRecord("r1", "Carico", T0));
        repo.WriteInterval("r1", "a", Interval(Home, 0, 5, Repeat(1000, 90)));   // 90 richieste da 1 ms
        repo.WriteInterval("r1", "b", Interval(Home, 0, 5, Repeat(9000, 10)));   // 10 richieste da 9 ms

        var summary = RunAnalyzer.Summarize(repo.ReadSamples("r1"));
        var s = Assert.Single(summary.Series);

        Assert.Equal(100, s.Count);
        Assert.InRange(s.P50Ms!.Value, 1.0, 1.01);
        Assert.InRange(s.P95Ms!.Value, 9.0, 9.01);          // la media dei p95 dei due agenti sarebbe ~5 ms
        Assert.Equal(5.0, summary.MeasuredSeconds, 3);      // un solo intervallo di 5 s, anche con due agenti
        Assert.Equal(20.0, s.PerSecond, 3);
    }

    [Fact]
    public void Summary_without_histograms_has_no_timings()
    {
        var rec = new MetricsRecorder(recordHistograms: false);
        rec.Record(Home, 1000, true);
        var snap = rec.Snapshot(0, T0, T0.AddSeconds(5), 1);

        using var repo = new SqliteResultsRepository(":memory:");
        repo.BeginRun(new RunRecord("r1", "Carico", T0));
        repo.WriteInterval("r1", "local", snap);

        var s = Assert.Single(RunAnalyzer.Summarize(repo.ReadSamples("r1")).Series);
        Assert.Equal(1, s.Count);
        Assert.Null(s.P95Ms);
    }

    [Fact]
    public void Csv_has_header_quotes_fields_and_neutralizes_formulas()
    {
        var key = new MetricKey("Utenti", "ricerca", Page: "a,b", Request: "=cmd");
        using var repo = new SqliteResultsRepository(":memory:");
        repo.BeginRun(new RunRecord("r1", "Carico", T0));
        repo.WriteInterval("r1", "local", Interval(key, 0, 1, (1000, true), (3000, false)));

        var sw = new StringWriter();
        CsvExporter.WriteSummary(sw, RunAnalyzer.Summarize(repo.ReadSamples("r1")));
        var lines = sw.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.StartsWith("scenario,test,kind,page,request,transaction,count,failed,", lines[0]);
        Assert.StartsWith("Utenti,ricerca,Request,\"a,b\",'=cmd,,2,1,50,", lines[1]);
        Assert.EndsWith("Timeout=1", lines[1]);
    }
}
