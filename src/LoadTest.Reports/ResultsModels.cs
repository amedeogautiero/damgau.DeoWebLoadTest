using LoadTest.Metrics;

namespace LoadTest.Reports;

// Tipi del repository dei risultati. Reports dipende solo da Metrics: chi chiama (controller / CLI) mappa LoadTestResult su questi record.

public sealed record RunRecord(string RunId, string Name, DateTimeOffset StartedUtc, string? DefinitionYaml = null);

/// <summary>Status come LoadTestStatus dell'engine: Completed | Aborted | Cancelled.</summary>
public sealed record RunCompletion(
    DateTimeOffset EndedUtc, string Status, string? AbortReason,
    long TestsCompleted, long TestsFailed, long RequestErrors, IReadOnlyList<string> Warnings);

public sealed record RunInfo(
    string RunId, string Name, DateTimeOffset StartedUtc, DateTimeOffset? EndedUtc, string Status, string? AbortReason,
    string? DefinitionYaml, long TestsCompleted, long TestsFailed, long RequestErrors, IReadOnlyList<string> Warnings);

public sealed record IntervalInfo(int Index, string AgentId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int ActiveUsers, bool AgentSaturated);

/// <summary>Una serie in un intervallo per un agente, come riletta dal repository.</summary>
public sealed record SeriesSample(
    int IntervalIndex, string AgentId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, bool AgentSaturated,
    MetricKey Key, SeriesSnapshot Series);

public sealed record ErrorDetail(
    string Scenario, string Test, string? Page, string? Url, string? Transaction,
    int StatusCode, string? ErrorType, string? Message);

public sealed record Annotation(long Id, DateTimeOffset AtUtc, string Text);

public interface IResultsRepository : IDisposable
{
    void BeginRun(RunRecord run);
    /// <summary>Scrive un intervallo di un agente (idempotente: riscrivere lo stesso indice lo sostituisce).</summary>
    void WriteInterval(string runId, string agentId, IntervalSnapshot interval, bool agentSaturated = false);
    void WriteErrorDetails(string runId, IEnumerable<ErrorDetail> errors);
    void CompleteRun(string runId, RunCompletion completion);
    void AddAnnotation(string runId, DateTimeOffset atUtc, string text);

    RunInfo? GetRun(string runId);
    IReadOnlyList<RunInfo> ListRuns();
    IReadOnlyList<IntervalInfo> ReadIntervals(string runId);
    IReadOnlyList<SeriesSample> ReadSamples(string runId);
    IReadOnlyList<ErrorDetail> ReadErrorDetails(string runId);
    IReadOnlyList<Annotation> ReadAnnotations(string runId);
}
