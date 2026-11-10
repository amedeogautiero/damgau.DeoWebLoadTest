using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using LoadTest.Agent.Transfer;
using LoadTest.Engine.LoadTesting;
using LoadTest.Engine.Runtime;
using LoadTest.Plugins;
using LoadTest.Protocol;
using Microsoft.Extensions.Logging;

namespace LoadTest.Agent;

/// <summary>
/// Una connessione al controller, dall'Hello alla chiusura dello stream. Una sola run alla volta.
/// Stati: Idle → Preparing (PrepareRun, trasferimento) → Prepared (Ready inviato) → Running (StartRun) → Idle (Finished inviato).
/// Se lo stream cade durante una run la run viene annullata (le metriche non avrebbero dove andare): vedi G10.
/// Tutte le scritture passano da un solo writer (gli stream gRPC non ammettono scritture concorrenti).
/// </summary>
public sealed class AgentSession
{
    private enum State { Idle, Preparing, Prepared, Running }

    private sealed class PrepState(PrepareRun plan, IReadOnlyList<ManifestEntry> entries, TransferReceiver receiver)
    {
        public PrepareRun Plan { get; } = plan;
        public IReadOnlyList<ManifestEntry> Entries { get; } = entries;
        public TransferReceiver Receiver { get; } = receiver;
    }

    private sealed class ActiveRun(PreparedRun prepared, LoadCoordinator coordinator, CancellationTokenSource cts)
    {
        public PreparedRun Prepared { get; } = prepared;
        public LoadCoordinator Coordinator { get; } = coordinator;
        public CancellationTokenSource Cts { get; } = cts;
        public string RunId => Prepared.Workspace.RunId;
        public volatile bool Started;
        public Task Task = Task.CompletedTask;
    }

    private static readonly TimeSpan MaxStartWait = TimeSpan.FromMinutes(10);

    private readonly AgentOptions _opt;
    private readonly string _agentId;
    private readonly IControllerLink _link;
    private readonly Func<IHttpExecutor> _http;
    private readonly ILogger _log;
    private readonly AgentCaches _caches;
    private readonly SystemSampler _sampler = new();
    private readonly Channel<AgentMsg> _out = Channel.CreateUnbounded<AgentMsg>(new UnboundedChannelOptions { SingleReader = true });

    private volatile State _state = State.Idle;
    private PrepState? _prep;
    private PreparedRun? _prepared;
    private ActiveRun? _run;
    private long _clockOffsetMs;
    private volatile int _activeUsers;
    private volatile string _hbRunId = "";

    public AgentSession(AgentOptions options, string agentId, IControllerLink link, Func<IHttpExecutor> httpFactory, ILogger logger)
    {
        _opt = options; _agentId = agentId; _link = link; _http = httpFactory; _log = logger;
        _caches = new AgentCaches(options.WorkDirectory);
    }

    /// <summary>Vero dopo un Welcome accettato.</summary>
    public bool Welcomed { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        var writer = Task.Run(() => WriteLoopAsync(ct), CancellationToken.None);
        using var hbCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? heartbeat = null;
        try
        {
            Send(MakeHello());
            await foreach (var msg in _link.ReadAllAsync(ct))
            {
                if (!Welcomed)
                {
                    if (msg.MCase != ControllerMsg.MOneofCase.Welcome) throw new InvalidDataException("atteso Welcome come primo messaggio");
                    OnWelcome(msg.Welcome);
                    heartbeat = Task.Run(() => HeartbeatLoopAsync(hbCts.Token), CancellationToken.None);
                    continue;
                }
                await HandleAsync(msg, ct);
            }
        }
        finally
        {
            hbCts.Cancel();
            await AbortCurrentRunAsync();
            _out.Writer.TryComplete();
            try { await writer; } catch (Exception) { }
            if (heartbeat is not null) try { await heartbeat; } catch (Exception) { }
            CleanupPrepare();
        }
    }

    // ---------- handshake e messaggi ----------

    private AgentMsg MakeHello() => new()
    {
        Hello = new Hello
        {
            AgentId = _agentId,
            Name = _opt.Name,
            ProtocolVersion = ProtocolInfo.ProtocolVersion,
            SdkVersion = PluginLoader.HostSdkVersion.ToString(),
            Rid = PlatformInfo.CurrentRid(),
            Os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Cores = Environment.ProcessorCount,
            RamMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024),
            MaxUsers = _opt.MaxUsers,
            UtcNow = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        }
    };

    private void OnWelcome(Welcome w)
    {
        if (!w.Accepted) throw new AgentRejectedException(w.RejectReason);
        Welcomed = true;
        _clockOffsetMs = w.ClockOffsetMs;
        _log.LogInformation("Connesso al controller '{Controller}' (offset orologio {Offset} ms)", w.ControllerName, w.ClockOffsetMs);
    }

    private async Task HandleAsync(ControllerMsg msg, CancellationToken ct)
    {
        switch (msg.MCase)
        {
            case ControllerMsg.MOneofCase.Prepare: await OnPrepareAsync(msg.Prepare, ct); break;
            case ControllerMsg.MOneofCase.Chunk: await OnChunkAsync(msg.Chunk, ct); break;
            case ControllerMsg.MOneofCase.Start: OnStart(msg.Start); break;
            case ControllerMsg.MOneofCase.Update: OnUpdate(msg.Update); break;
            case ControllerMsg.MOneofCase.Stop: OnStop(msg.Stop); break;
            case ControllerMsg.MOneofCase.Welcome: break;   // duplicato: ignorato
            default: _log.LogWarning("Messaggio del controller non riconosciuto ({Case})", msg.MCase); break;
        }
    }

    // ---------- preparazione ----------

    private async Task OnPrepareAsync(PrepareRun p, CancellationToken ct)
    {
        if (_state != State.Idle)
        {
            Send(MakeReady(p.RunId, false, "agente occupato"));
            return;
        }
        try
        {
            var entries = RunWorkspace.ValidateManifest(p, _opt);
            var missing = entries.Where(e => !IsCached(e)).GroupBy(e => e.Sha256).Select(g => g.First()).ToList();
            _prep = new PrepState(p, entries, new TransferReceiver(missing, _caches.Files, _caches.Plugins, _caches.TempRoot));
            _state = State.Preparing;
            if (missing.Count == 0) await FinishPrepareAsync(ct);
            else
            {
                var need = new PackageRequest { RunId = p.RunId };
                need.Sha256.AddRange(missing.Select(e => e.Sha256));
                Send(new AgentMsg { Need = need });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { FailPrepare(p.RunId, ex); }
    }

    private bool IsCached(ManifestEntry e) => e.Kind == EntryKind.Plugin ? _caches.Plugins.Contains(e.Sha256) : _caches.Files.Contains(e.Sha256);

    private async Task OnChunkAsync(PackageChunk c, CancellationToken ct)
    {
        if (_state != State.Preparing || _prep is null || _prep.Plan.RunId != c.RunId)
            throw new InvalidDataException("PackageChunk fuori contesto");
        try
        {
            if (_prep.Receiver.Accept(c) && _prep.Receiver.Pending == 0) await FinishPrepareAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { FailPrepare(c.RunId, ex); }
    }

    private async Task FinishPrepareAsync(CancellationToken ct)
    {
        var prep = _prep!;
        var ws = await Task.Run(() => RunWorkspace.Materialize(prep.Plan, prep.Entries, _caches), ct);
        _prepared = await Task.Run(() => RunExecutor.Prepare(ws), ct);
        prep.Receiver.Dispose();
        _prep = null;
        _state = State.Prepared;
        Send(MakeReady(ws.RunId, true, ""));
        _log.LogInformation("Run {Run} pronta ({Scenarios} scenari)", ws.RunId, _prepared.Definition.Scenarios.Count);
    }

    private void FailPrepare(string runId, Exception ex)
    {
        _log.LogWarning("Preparazione della run {Run} fallita: {Message}", runId, ex.Message);
        CleanupPrepare();
        _state = State.Idle;
        Send(MakeReady(runId, false, ex.Message));
    }

    private void CleanupPrepare()
    {
        var runId = _prep?.Plan.RunId ?? _prepared?.Workspace.RunId;
        _prep?.Receiver.Dispose();
        _prep = null;
        _prepared?.Dispose();
        _prepared = null;
        if (runId is not null && !_opt.KeepRunFolders && _run is null) RunWorkspace.Delete(_caches, runId);
    }

    // ---------- avvio, aggiornamenti, stop ----------

    private void OnStart(StartRun s)
    {
        if (_state != State.Prepared || _prepared is null || _prepared.Workspace.RunId != s.RunId)
        {
            Send(MakeFinished(s.RunId, RunStatus.Failed, "StartRun senza una run preparata", 0, 0, 0, []));
            return;
        }
        if (!(s.UserShare > 0 && s.UserShare <= 1) || s.MaxUsers < 0)
        {
            CleanupPrepare();
            _state = State.Idle;
            Send(MakeFinished(s.RunId, RunStatus.Failed, $"StartRun non valido (user_share {s.UserShare}, max_users {s.MaxUsers})", 0, 0, 0, []));
            return;
        }

        var prepared = _prepared;
        _prepared = null;
        var run = new ActiveRun(prepared, prepared.CreateCoordinator(s.UserShare, s.MaxUsers), new CancellationTokenSource());
        _run = run;
        _hbRunId = run.RunId;
        _state = State.Running;
        run.Task = Task.Run(() => ExecuteRunAsync(run, s));
    }

    private void OnUpdate(UpdateLoad u)
    {
        var run = _run;
        if (run is null || run.RunId != u.RunId) return;
        foreach (var t in u.Targets)
            if (!run.Coordinator.SetTarget(t.Scenario, t.Users))
                _log.LogWarning("UpdateLoad: scenario '{Scenario}' sconosciuto", t.Scenario);
    }

    private void OnStop(Stop stop)
    {
        var run = _run;
        if (run is not null && run.RunId == stop.RunId)
        {
            if (stop.Mode == StopMode.Abort || !run.Started) run.Cts.Cancel();
            else
            {
                // Graceful (APPROSSIMAZIONE, G11): azzera i target (gli utenti si ritirano a fine test) e annulla dopo coolDownSec.
                run.Coordinator.ZeroAll();
                run.Cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(run.Prepared.Definition.RunSettings.CoolDownSec, 1)));
            }
            return;
        }
        if (_state is State.Preparing or State.Prepared)
        {
            var id = _prep?.Plan.RunId ?? _prepared?.Workspace.RunId;
            if (id != stop.RunId) return;
            CleanupPrepare();
            _state = State.Idle;
            Send(MakeFinished(id, RunStatus.Cancelled, stop.Reason, 0, 0, 0, []));
        }
    }

    private async Task ExecuteRunAsync(ActiveRun run, StartRun s)
    {
        var status = RunStatus.Failed;
        string reason = "";
        long completed = 0, failed = 0, errors = 0;
        var warnings = new List<string>();
        IReadOnlyList<RequestMeasurement> errorDetails = [];

        try
        {
            var startLocal = (s.StartAtUtc?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow) - TimeSpan.FromMilliseconds(_clockOffsetMs);
            var wait = startLocal - DateTimeOffset.UtcNow;
            if (wait > MaxStartWait) throw new InvalidOperationException($"start_at_utc troppo lontano ({wait.TotalMinutes:0} min)");
            if (wait > TimeSpan.Zero) await Task.Delay(wait, run.Cts.Token);
            else if (wait < TimeSpan.FromSeconds(-1)) warnings.Add($"avvio in ritardo di {-wait.TotalMilliseconds:0} ms rispetto a start_at_utc");
            if (s.UserIdBase != 0) warnings.Add("user_id_base non applicato: l'Engine non ha un id utente iniziale (G11)");
            run.Started = true;

            var result = await RunExecutor.RunAsync(new RunInputs
            {
                Run = run.Prepared, Coordinator = run.Coordinator, Start = s, AgentId = _agentId, AgentName = _opt.Name,
                HttpFactory = _http,
                OnInterval = snap => Send(new AgentMsg { Metrics = MetricsWire.ToBatch(run.RunId, snap) }),
                OnActiveUsers = n => _activeUsers = n,
                OnPluginError = ex => _log.LogWarning(ex, "Errore in un plugin della run {Run}", run.RunId)
            }, run.Cts.Token);

            status = result.Status switch
            {
                LoadTestStatus.Completed => RunStatus.Completed,
                LoadTestStatus.Aborted => RunStatus.Aborted,
                _ => RunStatus.Cancelled
            };
            reason = result.AbortReason ?? "";
            completed = result.TestsCompleted; failed = result.TestsFailed; errors = result.RequestErrors;
            warnings.AddRange(result.Warnings);
            errorDetails = result.ErrorDetails;
        }
        catch (OperationCanceledException) { status = RunStatus.Cancelled; }
        catch (Exception ex)
        {
            status = RunStatus.Failed;
            reason = ex.Message;
            _log.LogError(ex, "Run {Run} fallita", run.RunId);
        }

        foreach (var chunk in errorDetails.Chunk(500))
        {
            var batch = new ErrorBatch { RunId = run.RunId };
            foreach (var m in chunk) batch.Errors.Add(ToEntry(m));
            Send(new AgentMsg { Errors = batch });
        }

        // Prima di Finished l'agente deve essere di nuovo libero: il controller può mandare subito un nuovo PrepareRun.
        var runId = run.RunId;
        _hbRunId = "";
        _activeUsers = 0;
        _run = null;
        run.Prepared.Dispose();
        run.Cts.Dispose();
        if (!_opt.KeepRunFolders) RunWorkspace.Delete(_caches, runId);
        _state = State.Idle;
        Send(MakeFinished(runId, status, reason, completed, failed, errors, warnings));
    }

    private async Task AbortCurrentRunAsync()
    {
        var run = _run;
        if (run is null) return;
        run.Cts.Cancel();
        try { await run.Task; } catch (Exception) { }
    }

    // ---------- invio ----------

    private void Send(AgentMsg m) => _out.Writer.TryWrite(m);

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var m in _out.Reader.ReadAllAsync(ct)) await _link.WriteAsync(m, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Scrittura verso il controller fallita");
        }
        finally
        {
            try { await _link.CompleteWritesAsync(); } catch (Exception) { }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _opt.HeartbeatSec)));
        do
        {
            var (cpu, ram) = _sampler.Sample();
            Send(new AgentMsg
            {
                Hb = new Heartbeat
                {
                    RunId = _hbRunId, CpuPercent = cpu, RamUsedMb = ram, ActiveUsers = _activeUsers,
                    UtcNow = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow), ClockOffsetMs = _clockOffsetMs
                }
            });
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    private static AgentMsg MakeReady(string runId, bool ok, string error) =>
        new() { Ready = new Ready { RunId = runId, Ok = ok, Error = error } };

    private static AgentMsg MakeFinished(string runId, RunStatus status, string reason, long completed, long failed, long errors, IEnumerable<string> warnings)
    {
        var f = new Finished
        {
            RunId = runId, Status = status, AbortReason = reason,
            TestsCompleted = completed, TestsFailed = failed, RequestErrors = errors
        };
        f.Warnings.AddRange(warnings);
        return new AgentMsg { Finished = f };
    }

    // RequestMeasurement non porta l'istante dell'errore: at_utc resta non impostato (G11).
    private static ErrorEntry ToEntry(RequestMeasurement m)
    {
        var e = new ErrorEntry { Scenario = m.Scenario, Test = m.Test, StatusCode = m.StatusCode };
        e.Page = m.Page;
        e.Url = m.Url;
        if (m.Transaction is not null) e.Transaction = m.Transaction;
        if (m.ErrorType is not null) e.ErrorType = m.ErrorType;
        if (m.ErrorMessage is not null) e.Message = m.ErrorMessage;
        return e;
    }
}
