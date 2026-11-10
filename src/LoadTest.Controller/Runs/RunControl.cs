using LoadTest.Protocol;

namespace LoadTest.Controller.Runs;

/// <summary>Richiesta di stop dall'esterno (API, CLI). Abort prevale su graceful.</summary>
public sealed class RunControl
{
    private readonly object _gate = new();
    private (StopMode Mode, string Reason)? _request;
    private readonly TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task StopRequestedTask => _signal.Task;
    public (StopMode Mode, string Reason)? Requested { get { lock (_gate) return _request; } }

    public void Stop(StopMode mode, string reason)
    {
        lock (_gate)
        {
            if (_request is { Mode: StopMode.Abort }) return;
            if (_request is null || mode == StopMode.Abort) _request = (mode, reason);
        }
        _signal.TrySetResult();
    }
}

public sealed record RunOutcome(string Status, string Reason, long TestsCompleted, long TestsFailed, long RequestErrors, IReadOnlyList<string> Warnings);

/// <summary>Avanzamento letto dall'API mentre la run è in corso.</summary>
public sealed class RunProgress
{
    private string _state = "Planned";
    private long _requests, _errors;
    private int _activeUsers;

    public string State { get => Volatile.Read(ref _state); set => Volatile.Write(ref _state, value); }
    public DateTimeOffset? StartedUtc { get; set; }
    public int ActiveUsers { get => Volatile.Read(ref _activeUsers); set => Volatile.Write(ref _activeUsers, value); }
    public long Requests { get => Interlocked.Read(ref _requests); set => Interlocked.Exchange(ref _requests, value); }
    public long Errors { get => Interlocked.Read(ref _errors); set => Interlocked.Exchange(ref _errors, value); }
    public RunOutcome? Outcome { get; set; }
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> AgentStates { get; } = new(StringComparer.Ordinal);
}

public sealed class PlannedRun
{
    public required string RunId { get; init; }
    public required string Name { get; init; }
    public required string LoadTestPath { get; init; }
    public required string LoadTestEntryPath { get; init; }
    public required string DefinitionYaml { get; init; }
    public required string WorkDir { get; init; }
    public required LoadTest.Engine.LoadTesting.LoadTestDefinition Definition { get; init; }
    public required Rig.RigDefinition Rig { get; init; }
    public required IReadOnlyList<Rig.AgentAssignment> Assignments { get; init; }
    public required Packaging.PackageSet Package { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public int Seed { get; init; } = 12345;
    public RunProgress Progress { get; } = new();
}

public sealed class RunRequest
{
    public required string LoadTestPath { get; init; }
    public string? RunName { get; init; }
    public int? Seed { get; init; }
}
