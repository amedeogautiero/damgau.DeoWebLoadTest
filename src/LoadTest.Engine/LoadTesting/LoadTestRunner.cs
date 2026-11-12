using System.Collections.Concurrent;
using System.Diagnostics;
using LoadTest.Engine.Model;
using LoadTest.Engine.Runtime;
using LoadTest.Metrics;
using LoadTest.Sdk;

namespace LoadTest.Engine.LoadTesting;

public enum LoadTestStatus { Completed, Aborted, Cancelled }

public sealed class LoadTestResult
{
    public LoadTestStatus Status { get; init; }
    public string? AbortReason { get; init; }
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset EndedUtc { get; init; }
    /// <summary>Test completati e falliti DOPO il warmup (e, in modalità durata, prima del cooldown).</summary>
    public long TestsCompleted { get; init; }
    public long TestsFailed { get; init; }
    public long RequestErrors { get; init; }
    public IReadOnlyList<IntervalSnapshot> Intervals { get; init; } = [];
    public IReadOnlyList<RequestMeasurement> ErrorDetails { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed class LoadTestRunnerOptions
{
    /// <summary>Servizi di base (regole, plugin, host dei passi code, ecc.). Il campo Http è ignorato: ogni utente usa un executor creato da <see cref="HttpFactory"/>.</summary>
    public required EngineServices Services { get; init; }
    /// <summary>Crea un executor (= una sessione con cookie propri). Chiamato per ogni "nuovo utente".</summary>
    public required Func<IHttpExecutor> HttpFactory { get; init; }
    /// <summary>Caricamento dei web test (default: WebTestLoader.LoadFile). Usato anche per i passi `call:`.</summary>
    public Func<string, WebTestDefinition>? TestLoader { get; init; }
    public string BaseDirectory { get; init; } = Directory.GetCurrentDirectory();
    public IReadOnlyList<(ILoadTestPlugin Plugin, IReadOnlyDictionary<string, string> Args)> Plugins { get; init; } = [];
    /// <summary>Risolve i profili `custom` (chat Plugin).</summary>
    public Func<string, ILoadProfile?>? CustomProfileResolver { get; init; }
    /// <summary>Valore corrente di un contatore, per il profilo goal-based (chat Monitoraggio).</summary>
    public Func<string, double?>? CounterProvider { get; init; }
    public int Seed { get; init; } = 12345;
    public string RunId { get; init; } = Guid.NewGuid().ToString("N");
    public string AgentId { get; init; } = "local";
    public string AgentName { get; init; } = "local";
    public string ControllerName { get; init; } = "local";
    public Action<IntervalSnapshot>? OnInterval { get; init; }
    public Action<Exception>? OnPluginError { get; init; }
}

internal sealed class RunGate { public volatile bool Recording; }

internal sealed class RunMeasurementSink(MetricsRecorder recorder, RunGate gate, int maxErrorDetails, Action<long> onError) : IMeasurementSink
{
    private int _detailCount;
    private long _errorsAll, _errorsRecorded, _tests, _testsFailed;

    public ConcurrentQueue<RequestMeasurement> ErrorDetails { get; } = new();
    public long TestsCompleted => Interlocked.Read(ref _tests);
    public long TestsFailed => Interlocked.Read(ref _testsFailed);
    public long RequestErrors => Interlocked.Read(ref _errorsRecorded);

    public void RequestCompleted(RequestMeasurement m)
    {
        if (!m.Success)
        {
            var all = Interlocked.Increment(ref _errorsAll);
            if (gate.Recording)
            {
                Interlocked.Increment(ref _errorsRecorded);
                if (Interlocked.Increment(ref _detailCount) <= maxErrorDetails) ErrorDetails.Enqueue(m);
            }
            onError(all);
        }
        if (!gate.Recording) return;
        recorder.Record(new MetricKey(m.Scenario, m.Test, m.Page, m.Url, m.Transaction),
            m.ElapsedMicros, m.Success, m.ErrorType, m.BytesSent, m.BytesReceived);
    }

    public void TransactionCompleted(TransactionMeasurement m)
    {
        if (!gate.Recording) return;
        recorder.Record(new MetricKey(m.Scenario, m.Test, Transaction: m.Transaction), m.ElapsedMicros, m.Success, m.Success ? null : "Failed");
    }

    public void TestCompleted(TestMeasurement m)
    {
        if (!gate.Recording) return;
        Interlocked.Increment(ref _tests);
        if (!m.Success) Interlocked.Increment(ref _testsFailed);
        recorder.Record(new MetricKey(m.Scenario, m.Test), m.ElapsedMicros, m.Success, m.Success ? null : "Failed");
    }
}

/// <summary>
/// Scheduler locale di un load test: utenti virtuali per scenario, profilo di carico, mix, think profile, campionamento.
/// Monouso. Fasi (ASSUNZIONE, il contratto non le definisce): warmup (misure scartate) -> durata (misurata) -> cooldown
/// (nessun nuovo test, attesa dei test in corso fino a coolDownSec, misure scartate). Durata totale = warmup + durationMin.
/// Con `iterations: N` si avviano in tutto N test e poi si attende la fine di quelli in corso (misurati).
/// </summary>
public sealed class LoadTestRunner
{
    private sealed class VirtualUser
    {
        public int Id { get; init; }
        public CancellationTokenSource RetireCts { get; } = new();
        public Task Task { get; set; } = Task.CompletedTask;
        public bool Retiring => RetireCts.IsCancellationRequested;
    }

    private sealed class ScenarioRuntime
    {
        public required int Index { get; init; }
        public required ScenarioDef Def { get; init; }
        public required ILoadProfile Profile { get; init; }
        public required TestMixSelector Mix { get; init; }
        public List<VirtualUser> Users { get; } = [];
        public int ActiveCount => Users.Count(u => !u.Retiring && !u.Task.IsCompleted);
    }

    private readonly LoadTestDefinition _def;
    private readonly LoadTestRunnerOptions _opts;
    private readonly Func<string, WebTestDefinition> _loadFn;
    private readonly ConcurrentDictionary<string, WebTestDefinition> _defs = new();
    private readonly CancellationTokenSource _hardCts = new();
    private readonly CancellationTokenSource _stopCts = new();
    private readonly RunGate _gate = new();
    private readonly ConcurrentBag<Task> _allTasks = [];
    private readonly ConcurrentQueue<string> _warnings = new();
    private readonly LoadTestEvents _events = new();
    private readonly MetricsRecorder _recorder;
    private readonly RunMeasurementSink _sink;
    private readonly ThinkProfileProvider _think;
    private readonly IReadOnlyList<IRequestPlugin> _requestPlugins;

    private int _used, _abortFlag, _nextUserId;
    private long _started;
    private volatile bool _iterationsDone;
    private string? _abortReason;

    public LoadTestEvents Events => _events;

    public LoadTestRunner(LoadTestDefinition def, LoadTestRunnerOptions options)
    {
        _def = def;
        _opts = options;
        _loadFn = options.TestLoader ?? WebTestLoader.LoadFile;
        _recorder = new MetricsRecorder(def.RunSettings.TimingDetail != TimingDetail.None);
        _sink = new RunMeasurementSink(_recorder, _gate, def.RunSettings.MaxErrorDetails, OnRequestError);
        _think = new ThinkProfileProvider(def.Scenarios.ToDictionary(s => s.Name, s => s.ThinkProfile, StringComparer.Ordinal));
        _requestPlugins = [.. options.Services.RequestPlugins, new BrowserUserAgentPlugin()];
        _events.OnHandlerError = options.OnPluginError ?? (ex => _warnings.Enqueue($"errore in un handler di evento: {ex.Message}"));
    }

    public async Task<LoadTestResult> RunAsync(CancellationToken external = default)
    {
        if (Interlocked.Exchange(ref _used, 1) == 1) throw new InvalidOperationException("LoadTestRunner è monouso");
        var rs = _def.RunSettings;
        using var reg = external.Register(() => _hardCts.Cancel());

        AddUnsupportedWarnings(rs);

        // Preparazione: carica i test e costruisce gli scenari prima di avviare qualsiasi utente.
        var scenarios = new List<ScenarioRuntime>();
        for (var i = 0; i < _def.Scenarios.Count; i++)
        {
            var sd = _def.Scenarios[i];
            foreach (var e in sd.TestMix.Tests) GetDefinition(e.Test);
            scenarios.Add(new ScenarioRuntime
            {
                Index = i, Def = sd,
                Profile = LoadProfileFactory.Create(sd.LoadPattern, _opts.CustomProfileResolver),
                Mix = new TestMixSelector(sd.TestMix.Model, sd.TestMix.Tests.Select(t => t.Percent).ToList(),
                    new Random(unchecked(_opts.Seed * 31 + i)))
            });
        }
        foreach (var (plugin, args) in _opts.Plugins) plugin.Initialize(_events, args);

        var info = new LoadTestInfo(_def.Name, _opts.RunId);
        var sw = Stopwatch.StartNew();
        var startedUtc = DateTimeOffset.UtcNow;
        _events.RaiseLoadTestStarting(info);

        var intervals = new List<IntervalSnapshot>();
        var warmup = TimeSpan.FromSeconds(rs.WarmupSec);
        var samplingSpan = TimeSpan.FromSeconds(rs.SamplingSec);
        TimeSpan? duration = rs.DurationMin is { } dm ? TimeSpan.FromMinutes(dm) : null;
        var warmDone = false;
        var durationEnded = false;
        var sampleIndex = 0;
        var lastSampleUtc = startedUtc;
        var nextSample = TimeSpan.Zero;

        void TakeInterval()
        {
            var now = DateTimeOffset.UtcNow;
            var snap = _recorder.Snapshot(sampleIndex++, lastSampleUtc, now, scenarios.Sum(s => s.ActiveCount));
            lastSampleUtc = now;
            intervals.Add(snap);
            _opts.OnInterval?.Invoke(snap);
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                var elapsed = sw.Elapsed;
                if (!warmDone && elapsed >= warmup)
                {
                    warmDone = true;
                    _gate.Recording = true;
                    lastSampleUtc = DateTimeOffset.UtcNow;
                    nextSample = elapsed + samplingSpan;
                    _events.RaiseWarmupComplete();
                }
                if (duration is { } d && warmDone && elapsed >= warmup + d) { durationEnded = true; break; }
                if (_iterationsDone) break;

                foreach (var sc in scenarios) Adjust(sc, elapsed);
                _events.RaiseHeartbeat(new HeartbeatArgs(DateTimeOffset.UtcNow, scenarios.Sum(s => s.ActiveCount)));

                if (warmDone && elapsed >= nextSample) { TakeInterval(); nextSample += samplingSpan; }
            }
            while (await timer.WaitForNextTickAsync(_hardCts.Token));
        }
        catch (OperationCanceledException) { /* abort o annullamento esterno */ }

        var interrupted = Volatile.Read(ref _abortFlag) == 1 || external.IsCancellationRequested;
        if (durationEnded || interrupted)
        {
            if (warmDone) TakeInterval();      // ultimo intervallo parziale
            _gate.Recording = false;           // cooldown: misure scartate
        }

        _stopCts.Cancel();                     // nessun nuovo test; i test in corso finiscono
        var all = Task.WhenAll(_allTasks.ToArray());
        if (interrupted) _hardCts.Cancel();
        else
        {
            var first = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(rs.CoolDownSec)));
            if (first != all) _hardCts.Cancel();
        }
        try { await all; } catch { /* i task utente non lanciano; difesa */ }

        if (_gate.Recording)                   // modalità iterazioni: il drain è misurato
        {
            if (warmDone) TakeInterval();
            _gate.Recording = false;
        }

        _events.RaiseLoadTestFinished(info);
        var status = Volatile.Read(ref _abortFlag) == 1 ? LoadTestStatus.Aborted
            : external.IsCancellationRequested ? LoadTestStatus.Cancelled : LoadTestStatus.Completed;
        return new LoadTestResult
        {
            Status = status, AbortReason = _abortReason, StartedUtc = startedUtc, EndedUtc = DateTimeOffset.UtcNow,
            TestsCompleted = _sink.TestsCompleted, TestsFailed = _sink.TestsFailed, RequestErrors = _sink.RequestErrors,
            Intervals = intervals, ErrorDetails = _sink.ErrorDetails.ToList(), Warnings = _warnings.ToList()
        };
    }

    // ---------- gestione utenti ----------

    private void Adjust(ScenarioRuntime sc, TimeSpan elapsed)
    {
        sc.Users.RemoveAll(u => u.Task.IsCompleted);
        var active = sc.Users.Count(u => !u.Retiring);
        double? goal = sc.Def.LoadPattern is { Type: LoadPatternType.Goal, Goal: { } g }
            ? _opts.CounterProvider?.Invoke(g.Counter) : null;
        var target = Math.Max(0, sc.Profile.GetUserCount(elapsed, new LoadState(active, goal)));

        while (active < target)
        {
            var vu = new VirtualUser { Id = Interlocked.Increment(ref _nextUserId) };
            sc.Users.Add(vu);
            vu.Task = Task.Run(() => RunUserAsync(sc, vu, _hardCts.Token));
            _allTasks.Add(vu.Task);
            active++;
        }
        for (var i = sc.Users.Count - 1; i >= 0 && active > target; i--)
            if (!sc.Users[i].Retiring) { sc.Users[i].RetireCts.Cancel(); active--; }
    }

    private async Task RunUserAsync(ScenarioRuntime sc, VirtualUser vu, CancellationToken ct)
    {
        var rng = new Random(unchecked((_opts.Seed * 31 + sc.Index) * 31 + vu.Id));
        var mixState = new MixUserState();
        sc.Mix.AssignUser(mixState);
        using var soft = CancellationTokenSource.CreateLinkedTokenSource(_stopCts.Token, vu.RetireCts.Token);
        IHttpExecutor? http = null;
        UserContext? uctx = null;
        WebTestRunner? runner = null;
        var iteration = 0;

        try
        {
            while (!soft.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                if (_def.RunSettings.Iterations is { } max && Interlocked.Increment(ref _started) > max)
                {
                    _iterationsDone = true;
                    break;
                }

                // newUsersPercent: a ogni iterazione l'utente è "nuovo" (sessione e stato puliti) con quella probabilità.
                var isNew = uctx is null || rng.NextDouble() * 100 < sc.Def.NewUsersPercent;
                var previous = uctx;
                if (isNew)
                {
                    (http as IDisposable)?.Dispose();
                    http = _opts.HttpFactory();
                    uctx = NewContext(sc, vu, Guid.NewGuid().ToString("N"), isNew: true);
                    uctx.Items["rng"] = rng;
                    if (PickBrowser(sc.Def, rng) is { } browser) uctx.Items["browser"] = browser;
                    if (PickNetwork(sc.Def, rng) is { } net) uctx.Items["network"] = net;
                    runner = new WebTestRunner(MakeServices(http));
                }
                else
                {
                    uctx = NewContext(sc, vu, previous!.UserContextId, isNew: false);
                    foreach (var kv in previous.Items) uctx.Items[kv.Key] = kv.Value;
                }

                var testIndex = sc.Mix.NextTest(mixState);
                var selected = new TestSelectedArgs
                {
                    ScenarioName = sc.Def.Name, UserId = vu.Id, SelectedTest = sc.Def.TestMix.Tests[testIndex].Test
                };
                _events.RaiseTestSelected(selected);
                var testDef = GetDefinition(selected.SelectedTest);
                var testName = !string.IsNullOrWhiteSpace(testDef.Name) ? testDef.Name : StemOf(selected.SelectedTest);

                var ctx = new WebTestContext { TestName = testName, User = uctx, Iteration = iteration++, CancellationToken = ct };
                var evt = new TestEventArgs(testName, sc.Def.Name, vu.Id);
                var testStart = Stopwatch.GetTimestamp();
                _events.RaiseTestStarting(evt);
                try { await runner!.RunAsync(testDef, ctx, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                _events.RaiseTestFinished(evt);

                if (sc.Def.TestMix.Model == MixModel.Pace && sc.Def.TestMix.TestsPerUserPerHour is > 0 and var perHour)
                {
                    var wait = TimeSpan.FromHours(1.0 / perHour) - Stopwatch.GetElapsedTime(testStart);
                    if (wait > TimeSpan.Zero)
                        try { await Task.Delay(wait, soft.Token); } catch (OperationCanceledException) { }
                }
                await Task.Yield();
            }
        }
        catch (Exception ex)
        {
            _warnings.Enqueue($"utente {vu.Id} ({sc.Def.Name}) terminato da un'eccezione: {ex.Message}");
        }
        finally
        {
            sc.Mix.ReleaseUser(mixState);
            (http as IDisposable)?.Dispose();
        }
    }

    private UserContext NewContext(ScenarioRuntime sc, VirtualUser vu, string contextId, bool isNew) => new()
    {
        UserId = vu.Id, UserContextId = contextId, AgentId = _opts.AgentId, AgentName = _opts.AgentName,
        ControllerName = _opts.ControllerName, ScenarioName = sc.Def.Name, IsNewUser = isNew
    };

    private EngineServices MakeServices(IHttpExecutor http)
    {
        var t = _opts.Services;
        return new EngineServices
        {
            Http = http, Sink = _sink, OnExecutionEvent = t.OnExecutionEvent, ThinkTime = _think, Rules = t.Rules,
            WebTestPlugins = t.WebTestPlugins, RequestPlugins = _requestPlugins, CodeSteps = t.CodeSteps,
            ValidationLevel = _def.RunSettings.ValidationLevel, LoadTest = _loadFn, MaxCallDepth = t.MaxCallDepth
        };
    }

    // ---------- utilità ----------

    private WebTestDefinition GetDefinition(string reference)
    {
        var full = Path.IsPathRooted(reference) ? reference : Path.GetFullPath(Path.Combine(_opts.BaseDirectory, reference));
        return _defs.GetOrAdd(full, p => _loadFn(p));
    }

    private static string StemOf(string reference)
    {
        var f = Path.GetFileName(reference);
        return f.EndsWith(".webtest.yaml", StringComparison.OrdinalIgnoreCase) ? f[..^".webtest.yaml".Length] : Path.GetFileNameWithoutExtension(f);
    }

    private static string? PickBrowser(ScenarioDef s, Random rng) =>
        s.BrowserMix.Count == 0 ? null : s.BrowserMix[Weighted.Pick(s.BrowserMix.Select(b => b.Percent).ToList(), rng)].Browser;

    private static NetworkEntryDef? PickNetwork(ScenarioDef s, Random rng) =>
        s.NetworkMix.Count == 0 ? null : s.NetworkMix[Weighted.Pick(s.NetworkMix.Select(n => n.Percent).ToList(), rng)];

    private void OnRequestError(long totalErrors)
    {
        if (_def.RunSettings.StopOnErrors is { } n && totalErrors >= n
            && Interlocked.CompareExchange(ref _abortFlag, 1, 0) == 0)
        {
            _abortReason = $"stopOnErrors: raggiunti {totalErrors} errori (soglia {n})";
            _events.RaiseAborted(_abortReason);
            _hardCts.Cancel();
        }
    }

    private void AddUnsupportedWarnings(RunSettingsDef rs)
    {
        if (rs.ConnectionModel == ConnectionModel.Pool)
            _warnings.Enqueue("connectionModel 'pool' non ancora supportato: l'executor HTTP incapsula anche i cookie, quindi si usa 'perUser'.");
        if (rs.SaveLogOnFailure) _warnings.Enqueue("saveLogOnFailure non ancora supportato: l'engine non espone richiesta/risposta dei passi falliti.");
        if (rs.TimingDetail == TimingDetail.All) _warnings.Enqueue("timingDetail 'all' trattato come 'statistics'.");
        if (rs.SqlTracing) _warnings.Enqueue("sqlTracing è di competenza della chat Monitoraggio: ignorato.");
        if (rs.IpSwitching.Enabled) _warnings.Enqueue("ipSwitching è di competenza della chat Controller/agenti: ignorato.");
        if (_def.Scenarios.Any(s => s.NetworkMix.Count > 0)) _warnings.Enqueue("networkMix: profilo assegnato agli utenti (Items[\"network\"]) ma non applicato: manca l'hook di traffic shaping nell'engine.");
    }
}
