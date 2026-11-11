using LoadTest.Reports;
using LoadTest.Reports.Analysis;

namespace LoadTest.Controller.Api;

public sealed record SeriesDto(
    string Scenario, string Test, string Kind, string? Page, string? Request, string? Transaction,
    long Count, long Failed, double ErrorRatePercent, double PerSecond,
    double? MeanMs, double? P50Ms, double? P90Ms, double? P95Ms, double? P99Ms, double? MaxMs,
    long BytesSent, long BytesReceived, bool AgentSaturated, Dictionary<string, long> ErrorsByType);

/// <summary>Riassunto di una run in forma JSON stabile (API del controller, output della CLI per la CI). Tempi in millisecondi.</summary>
public sealed record RunSummaryDto(
    string RunId, string Name, string Status, string? Reason, DateTimeOffset StartedUtc, DateTimeOffset? EndedUtc,
    double MeasuredSeconds, long TestsCompleted, long TestsFailed, long RequestErrors,
    List<string> Warnings, List<SeriesDto> Series);

public sealed record AgentDto(
    string Id, string Name, string Rid, string Os, int Cores, long RamMb, int MaxUsers, bool Busy,
    double CpuPercent, int ActiveUsers, DateTimeOffset? LastHeartbeatUtc);

public static class SummaryMapper
{
    public static RunSummaryDto From(RunInfo run, RunSummary summary) => new(
        run.RunId, run.Name, run.Status, run.AbortReason, run.StartedUtc, run.EndedUtc,
        summary.MeasuredSeconds, run.TestsCompleted, run.TestsFailed, run.RequestErrors,
        [.. run.Warnings],
        summary.Series.Select(s => new SeriesDto(
            s.Key.Scenario, s.Key.Test, s.Key.Kind.ToString(), s.Key.Page, s.Key.Request, s.Key.Transaction,
            s.Count, s.Failed, s.ErrorRatePercent, s.PerSecond,
            s.MeanMs, s.P50Ms, s.P90Ms, s.P95Ms, s.P99Ms, s.MaxMs,
            s.BytesSent, s.BytesReceived, s.AgentSaturated, s.ErrorsByType.ToDictionary(kv => kv.Key, kv => kv.Value))).ToList());
}
