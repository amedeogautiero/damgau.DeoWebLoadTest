using System.Collections.Concurrent;
using LoadTest.Protocol;
using LoadTest.Contracts;

namespace LoadTest.Controller.Runs;

public sealed record AgentSnapshot(string Name, string State);

public sealed record RunSnapshot(
    string RunId, string Name, string State, DateTimeOffset? StartedUtc, string? Status, string? Reason,
    int ActiveUsers, long Requests, long Errors, long CompletedTests, long FailedTests, IReadOnlyList<RunProgressSeriesSnapshot> Series, IReadOnlyList<string> Warnings, IReadOnlyList<AgentSnapshot> Agents);

/// <summary>Run in corso e concluse (in memoria) del controller: avvio, stop, stato. I risultati stanno nel repository SQLite.</summary>
public sealed class RunManager(RunOrchestrator orchestrator, ILogger<RunManager> log)
{
    private sealed class Managed
    {
        public required PlannedRun Plan { get; init; }
        public required RunControl Control { get; init; }
        public Task<RunOutcome> Task { get; set; } = System.Threading.Tasks.Task.FromResult<RunOutcome>(null!);
    }

    private readonly ConcurrentDictionary<string, Managed> _runs = new(StringComparer.Ordinal);

    /// <summary>Pianifica (errori subito, come <see cref="RunPlanException"/>) e avvia in background.</summary>
    public async Task<string> StartAsync(RunRequest request)
    {
        var id = Guid.NewGuid().ToString("N");
        var plan = await orchestrator.PlanAsync(id, request);
        var m = new Managed { Plan = plan, Control = new RunControl() };
        m.Task = Task.Run(() => orchestrator.ExecuteAsync(plan, m.Control));
        _runs[id] = m;
        log.LogInformation("Run {Run} '{Name}' avviata su {Agents} agenti", id, plan.Name, plan.Assignments.Count);
        return id;
    }

    public bool Stop(string runId, StopMode mode, string reason)
    {
        if (!_runs.TryGetValue(runId, out var m)) return false;
        m.Control.Stop(mode, reason);
        return true;
    }

    public RunSnapshot? Get(string runId) => _runs.TryGetValue(runId, out var m) ? Snapshot(m) : null;

    /// <summary>Restituisce il contratto di progressione neutrale rispetto al client e al trasporto.</summary>
    public RunProgressSnapshot? GetProgress(string runId)
    {
        if (!_runs.TryGetValue(runId, out var m)) return null;

        var p = m.Plan.Progress;
        var outcome = p.Outcome;
        var capturedUtc = DateTimeOffset.UtcNow;
        var status = outcome is not null
            ? outcome.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase)
                ? RunProgressStatus.Completed
                : outcome.Status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)
                    ? RunProgressStatus.Cancelled
                    : RunProgressStatus.Failed
            : p.State switch
            {
                "Planned" => RunProgressStatus.Queued,
                "Setup" or "Preparing" => RunProgressStatus.Preparing,
                "Running" => RunProgressStatus.Running,
                "Finishing" => RunProgressStatus.Stopping,
                _ => RunProgressStatus.Queued
            };

        var series = p.Series;
        return new RunProgressSnapshot
        {
            RunId = m.Plan.RunId,
            Sequence = p.Sequence,
            Status = status,
            StartedUtc = p.StartedUtc,
            CapturedUtc = capturedUtc,
            ElapsedSeconds = p.StartedUtc is { } started
                ? Math.Max(0, (capturedUtc - started).TotalSeconds)
                : 0,
            ActiveUsers = p.ActiveUsers,
            CompletedTests = p.CompletedTests,
            FailedTests = p.FailedTests,
            CompletedRequests = p.Requests,
            FailedRequests = p.Errors,
            RequestsPerSecond = series.Where(s => s.Kind == RunProgressSeriesKind.Request).Sum(s => s.OperationsPerSecond),
            Series = series
        };
    }

    public IReadOnlyList<RunSnapshot> List() =>
        _runs.Values.Select(Snapshot).OrderByDescending(s => s.StartedUtc ?? DateTimeOffset.MinValue).ToList();

    private static RunSnapshot Snapshot(Managed m)
    {
        var p = m.Plan.Progress;
        var o = p.Outcome;
        return new RunSnapshot(
            m.Plan.RunId, m.Plan.Name, p.State, p.StartedUtc, o?.Status, o?.Reason,
            p.ActiveUsers, p.Requests, p.Errors, p.CompletedTests, p.FailedTests, p.Series, o?.Warnings ?? [],
            p.AgentStates.Select(kv => new AgentSnapshot(kv.Key, kv.Value)).OrderBy(a => a.Name, StringComparer.Ordinal).ToList());
    }
}
