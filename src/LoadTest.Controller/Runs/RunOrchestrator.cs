using System.Collections.Concurrent;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using LoadTest.Contracts;
using LoadTest.Controller.Agents;
using LoadTest.Controller.Packaging;
using LoadTest.Controller.Rig;
using LoadTest.Engine.LoadTesting;
using LoadTest.Metrics;
using LoadTest.Protocol;
using LoadTest.Reports;
using Microsoft.Extensions.Options;

namespace LoadTest.Controller.Runs;

/// <summary>
/// Una run distribuita: pianificazione (rig, pacchetti), preparazione degli agenti, avvio sincronizzato, carico, raccolta delle metriche
/// nel repository, stop (graceful/abort/stopOnErrors) e chiusura. PlanAsync lancia <see cref="RunPlanException"/>; ExecuteAsync non lancia mai
/// (ogni fallimento diventa un <see cref="RunOutcome"/> con stato Failed/Aborted/Cancelled) e libera sempre gli agenti.
/// </summary>
public sealed class RunOrchestrator(
    IOptions<ControllerOptions> options, AgentRegistry registry, IResultsRepository repo,
    ILogger<RunOrchestrator> log, IPluginCompiler? compiler = null)
{
    private sealed class Participant(AgentAssignment a, IReadOnlyList<ManifestEntry> manifest)
    {
        public AgentAssignment A { get; } = a;
        public AgentConnection Conn => A.Connection;
        public IReadOnlyList<ManifestEntry> Manifest { get; } = manifest;
        public HashSet<string> Allowed { get; } = manifest.Select(m => m.Sha256).ToHashSet(StringComparer.Ordinal);
        public TaskCompletionSource<string?> ReadyTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);   // null = ok, altrimenti l'errore
        public TaskCompletionSource FinishedTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Finished? Info { get; set; }
        public volatile bool Lost;
    }

    private sealed class RunRuntime(PlannedRun run, int maxErrorDetails, int? stopOnErrors)
    {
        public PlannedRun Run { get; } = run;
        public string RunId => Run.RunId;
        public int MaxErrorDetails { get; } = maxErrorDetails;
        public int? StopOnErrors { get; } = stopOnErrors;
        public long Requests, Errors, CompletedTests, FailedTests, ErrorDetails;
        public RunProgressAggregator ProgressAggregator { get; } = new(run.RunId);
        public volatile bool StopOnErrorsHit;
        public CancellationTokenSource Cts { get; } = new();
        public CancellationToken Cancel => Cts.Token;
        public ConcurrentQueue<string> Warnings { get; } = new();
        public void Warn(string message) => Warnings.Enqueue(message);
    }

    // ---------- pianificazione ----------

    public async Task<PlannedRun> PlanAsync(string runId, RunRequest req)
    {
        var o = options.Value;
        var path = Path.GetFullPath(req.LoadTestPath);
        if (!File.Exists(path)) throw new RunPlanException([$"file non trovato: {path}"]);

        LoadTestDefinition def;
        try { def = LoadTestLoader.LoadFile(path); }
        catch (LoadTestLoadException ex) { throw new RunPlanException(ex.Errors); }
        RigDefinition rig;
        try { rig = RigLoader.LoadFile(path); }
        catch (RigLoadException ex) { throw new RunPlanException(ex.Errors); }

        var free = registry.Snapshot().Where(a => a.ReservedBy is null).ToList();
        var plan = RigPlanner.Plan(def, rig, free, o.AllowPartialRig);

        var reserved = new List<AgentConnection>();
        foreach (var a in plan.Assignments)
        {
            if (!a.Connection.TryReserve(runId))
            {
                foreach (var r in reserved) r.Release(runId);
                throw new RunPlanException([$"agente '{a.Connection.Name}' occupato o disconnesso"]);
            }
            reserved.Add(a.Connection);
        }

        var workDir = Path.Combine(Path.GetFullPath(o.WorkDirectory), "runs", runId);
        try
        {
            Directory.CreateDirectory(workDir);
            var shares = plan.Assignments.Select(a => a.Share).ToList();
            var package = await Task.Run(() => RunPackager.Build(path, def, rig, shares, workDir, compiler));
            var warnings = plan.Warnings.Concat(package.Warnings).ToList();
            if (def.RunSettings.IpSwitching.Enabled) warnings.Add("ipSwitching non ancora supportato dal controller: ignorato");
            if (def.Scenarios.Any(s => s.LoadPattern.Type == LoadPatternType.Custom))
                warnings.Add("scenari con loadPattern custom: guidati dall'agente (quota applicata, nessun UpdateLoad dal controller)");

            return new PlannedRun
            {
                RunId = runId,
                Name = !string.IsNullOrWhiteSpace(req.RunName) ? req.RunName! : !string.IsNullOrWhiteSpace(def.Name) ? def.Name : Path.GetFileNameWithoutExtension(path),
                LoadTestPath = path,
                LoadTestEntryPath = package.Common.First(e => e.Kind == EntryKind.LoadTest).Path,
                DefinitionYaml = File.ReadAllText(path),
                WorkDir = workDir, Definition = def, Rig = rig, Assignments = plan.Assignments, Package = package,
                Warnings = warnings, Seed = req.Seed ?? 12345
            };
        }
        catch
        {
            foreach (var r in reserved) r.Release(runId);
            TryDelete(workDir);
            throw;
        }
    }

    // ---------- esecuzione ----------

    public async Task<RunOutcome> ExecuteAsync(PlannedRun run, RunControl control)
    {
        var o = options.Value;
        var def = run.Definition;
        var rs = def.RunSettings;
        var rt = new RunRuntime(run, rs.MaxErrorDetails, rs.StopOnErrors);
        foreach (var w in run.Warnings) rt.Warn(w);
        var ps = run.Assignments.Select(a => new Participant(a, run.Package.ManifestFor(a.Index))).ToList();
        var progress = run.Progress;
        foreach (var p in ps) progress.AgentStates[p.Conn.Name] = "Preparing";

        var status = "Failed";
        var reason = "";
        var begun = false;
        (StopMode Mode, string Reason)? userStop = null;

        try
        {
            var startedUtc = DateTimeOffset.UtcNow;
            repo.BeginRun(new RunRecord(run.RunId, run.Name, startedUtc, run.DefinitionYaml));
            begun = true;
            progress.StartedUtc = startedUtc;

            if (run.Rig.SetupScript is { } setup)
            {
                progress.State = "Setup";
                await RunScriptAsync("setup", setup, run, rt, mustSucceed: true);
            }

            foreach (var p in ps)
            {
                var part = p;
                part.Conn.Attach(m => OnAgentMessage(rt, part, m));
                _ = part.Conn.Closed.ContinueWith(_ => MarkLost(rt, part, progress), TaskScheduler.Default);
            }

            progress.State = "Preparing";
            foreach (var p in ps)
                if (!await p.Conn.SendAsync(MakePrepare(run, p, o), rt.Cancel)) throw new InvalidOperationException($"agente '{p.Conn.Name}' disconnesso");
            await WaitReadyAsync(ps, control, o);
            foreach (var p in ps) progress.AgentStates[p.Conn.Name] = "Ready";

            progress.State = "Running";
            var startAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Max(1, o.StartDelaySec));
            foreach (var p in ps)
            {
                await p.Conn.SendAsync(new ControllerMsg
                {
                    Start = new StartRun
                    {
                        RunId = run.RunId, StartAtUtc = Timestamp.FromDateTimeOffset(startAt), UserShare = p.A.Share,
                        MaxUsers = p.A.MaxUsers, Seed = run.Seed, UserIdBase = p.A.UserIdBase, Iterations = p.A.Iterations
                    }
                }, rt.Cancel);
                progress.AgentStates[p.Conn.Name] = "Running";
            }

            double? Counter(string name) => name switch
            {
                "users.active" => ps.Sum(p => p.Conn.ActiveUsers),
                "agents.cpu.avg" => ps.Count == 0 ? null : ps.Average(p => p.Conn.CpuPercent),
                "agents.cpu.max" => ps.Count == 0 ? null : ps.Max(p => p.Conn.CpuPercent),
                _ => null
            };
            var driver = new LoadDriver(run.RunId, def, ps.Select(p => p.A.Share).ToList(), ps.Select(p => p.A.MaxUsers).ToList(), Counter);
            userStop = await DriveAsync(rt, run, ps, control, driver, startAt);
            (status, reason) = Decide(rt, ps, userStop);
        }
        catch (OperationCanceledException)
        {
            status = "Cancelled";
            reason = control.Requested?.Reason ?? "run annullata";
        }
        catch (Exception ex)
        {
            status = "Failed";
            reason = ex.Message;
            log.LogError(ex, "Run {Run} fallita", run.RunId);
        }

        // ---- chiusura: i fallimenti non devono lasciare agenti impegnati ----
        progress.State = "Finishing";
        try
        {
            var pending = ps.Where(p => !p.FinishedTcs.Task.IsCompleted && !p.Lost).ToList();
            if (pending.Count > 0 && status != "Completed")
            {
                foreach (var p in pending)
                    await p.Conn.SendAsync(new ControllerMsg { Stop = new Stop { RunId = run.RunId, Mode = StopMode.Abort, Reason = reason } });
                await Task.WhenAny(Task.WhenAll(pending.Select(p => p.FinishedTcs.Task)), Task.Delay(TimeSpan.FromSeconds(10)));
            }
        }
        catch (Exception ex) { log.LogWarning(ex, "Errore durante lo stop degli agenti della run {Run}", run.RunId); }

        foreach (var p in ps) p.Conn.Detach();
        long tests = 0, failed = 0, errors = 0;
        foreach (var p in ps.Where(p => p.Info is not null))
        {
            tests += p.Info!.TestsCompleted; failed += p.Info.TestsFailed; errors += p.Info.RequestErrors;
            foreach (var w in p.Info.Warnings) rt.Warn($"[{p.Conn.Name}] {w}");
        }

        if (run.Rig.CleanupScript is { } cleanup)
        {
            try { await RunScriptAsync("cleanup", cleanup, run, rt, mustSucceed: false); }
            catch (Exception ex) { rt.Warn($"cleanup: {ex.Message}"); }
        }

        var warnings = rt.Warnings.ToList();
        var outcome = new RunOutcome(status, reason, tests, failed, errors, warnings);
        if (begun)
        {
            try
            {
                repo.CompleteRun(run.RunId, new RunCompletion(DateTimeOffset.UtcNow, status,
                    string.IsNullOrEmpty(reason) ? null : reason, tests, failed, errors, warnings));
            }
            catch (Exception ex) { log.LogError(ex, "Impossibile chiudere la run {Run} nel repository", run.RunId); }
        }

        foreach (var p in ps) p.Conn.Release(run.RunId);
        rt.Cts.Cancel();
        TryDelete(run.WorkDir);
        progress.Requests = Interlocked.Read(ref rt.Requests);
        progress.Errors = Interlocked.Read(ref rt.Errors);
        progress.CompletedTests = tests;
        progress.FailedTests = failed;
        progress.Outcome = outcome;
        progress.State = "Finished";
        log.LogInformation("Run {Run} conclusa: {Status} {Reason}", run.RunId, status, reason);
        return outcome;
    }

    private async Task WaitReadyAsync(List<Participant> ps, RunControl control, ControllerOptions o)
    {
        var all = Task.WhenAll(ps.Select(p => p.ReadyTcs.Task));
        var done = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(o.ReadyTimeoutSec)), control.StopRequestedTask);
        if (done == control.StopRequestedTask) throw new OperationCanceledException();
        if (done != all)
        {
            var late = string.Join(", ", ps.Where(p => !p.ReadyTcs.Task.IsCompleted).Select(p => p.Conn.Name));
            throw new InvalidOperationException($"agenti non pronti entro {o.ReadyTimeoutSec} s: {late}");
        }
        var problems = ps.Where(p => p.ReadyTcs.Task.Result is not null).Select(p => $"{p.Conn.Name}: {p.ReadyTcs.Task.Result}").ToList();
        if (problems.Count > 0) throw new InvalidOperationException("preparazione fallita: " + string.Join("; ", problems));
    }

    private async Task<(StopMode Mode, string Reason)?> DriveAsync(
        RunRuntime rt, PlannedRun run, List<Participant> ps, RunControl control, LoadDriver driver, DateTimeOffset startAt)
    {
        var rs = run.Definition.RunSettings;
        DateTimeOffset? deadline = rs.DurationMin is { } dm
            ? startAt + TimeSpan.FromSeconds(rs.WarmupSec) + TimeSpan.FromMinutes(dm) + TimeSpan.FromSeconds(rs.CoolDownSec) + TimeSpan.FromSeconds(120)
            : null;
        (StopMode Mode, string Reason)? userStop = null;
        DateTimeOffset? forceEnd = null;
        var stopSent = false;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            var now = DateTimeOffset.UtcNow;

            if (!stopSent)
            {
                if (control.Requested is { } req)
                {
                    userStop = req; stopSent = true;
                    await StopAllAsync(rt, ps, req.Mode, req.Reason);
                    forceEnd = now + (req.Mode == StopMode.Abort ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(rs.CoolDownSec + 60));
                }
                else if (rt.StopOnErrorsHit)
                {
                    stopSent = true;
                    await StopAllAsync(rt, ps, StopMode.Abort, "stopOnErrors");
                    forceEnd = now + TimeSpan.FromSeconds(30);
                }
                else if (deadline is { } d && now > d)
                {
                    stopSent = true;
                    rt.Warn("tempo massimo della run superato: stop forzato");
                    await StopAllAsync(rt, ps, StopMode.Abort, "tempo massimo superato");
                    forceEnd = now + TimeSpan.FromSeconds(30);
                }
            }
            else if (userStop is { Mode: StopMode.Graceful } && control.Requested is { Mode: StopMode.Abort } escalated)
            {
                userStop = escalated;
                await StopAllAsync(rt, ps, StopMode.Abort, escalated.Reason);
                forceEnd = now + TimeSpan.FromSeconds(30);
            }

            if (!stopSent)
            {
                var elapsed = now > startAt ? now - startAt : TimeSpan.Zero;
                foreach (var (agent, update) in driver.Tick(elapsed, ps.Sum(p => p.Conn.ActiveUsers)))
                    await ps[agent].Conn.SendAsync(new ControllerMsg { Update = update }, rt.Cancel);
            }

            run.Progress.ActiveUsers = ps.Sum(p => p.Conn.ActiveUsers);
            run.Progress.Requests = Interlocked.Read(ref rt.Requests);
            run.Progress.Errors = Interlocked.Read(ref rt.Errors);
            run.Progress.CompletedTests = Interlocked.Read(ref rt.CompletedTests);
            run.Progress.FailedTests = Interlocked.Read(ref rt.FailedTests);

            if (ps.All(p => p.FinishedTcs.Task.IsCompleted)) break;
            if (forceEnd is { } fe && now > fe)
            {
                foreach (var p in ps.Where(p => !p.FinishedTcs.Task.IsCompleted))
                {
                    rt.Warn($"agente '{p.Conn.Name}' non ha concluso entro il tempo di stop");
                    p.Lost = true;
                }
                break;
            }
        }
        while (await timer.WaitForNextTickAsync(rt.Cancel));
        return userStop;
    }

    private static async Task StopAllAsync(RunRuntime rt, List<Participant> ps, StopMode mode, string reason)
    {
        foreach (var p in ps.Where(p => !p.FinishedTcs.Task.IsCompleted))
            await p.Conn.SendAsync(new ControllerMsg { Stop = new Stop { RunId = rt.RunId, Mode = mode, Reason = reason } });
    }

    private static (string Status, string Reason) Decide(RunRuntime rt, List<Participant> ps, (StopMode Mode, string Reason)? userStop)
    {
        if (rt.StopOnErrorsHit)
            return ("Aborted", $"stopOnErrors: raggiunti {Interlocked.Read(ref rt.Errors)} errori (soglia {rt.StopOnErrors})");
        if (userStop is { } s) return ("Cancelled", string.IsNullOrEmpty(s.Reason) ? "stop richiesto" : s.Reason);

        var infos = ps.Where(p => p.Info is not null).Select(p => (p.Conn.Name, Info: p.Info!)).ToList();
        if (infos.Count == 0) return ("Failed", "nessun agente ha concluso la run");
        if (infos.All(i => i.Info.Status == RunStatus.Failed))
            return ("Failed", string.Join("; ", infos.Select(i => $"{i.Name}: {i.Info.AbortReason}")));
        var bad = infos.Where(i => i.Info.Status != RunStatus.Completed).ToList();
        var lost = ps.Where(p => p.Info is null).Select(p => p.Conn.Name).ToList();
        if (bad.Count > 0 || lost.Count > 0)
        {
            var parts = bad.Select(i => $"{i.Name}: {i.Info.Status} {i.Info.AbortReason}".Trim()).Concat(lost.Select(n => $"{n}: persa"));
            return ("Aborted", "run non completata da tutti gli agenti: " + string.Join("; ", parts));
        }
        return ("Completed", "");
    }

    // ---------- messaggi degli agenti ----------

    private void OnAgentMessage(RunRuntime rt, Participant p, AgentMsg m)
    {
        switch (m.MCase)
        {
            case AgentMsg.MOneofCase.Need when m.Need.RunId == rt.RunId:
                _ = SendItemsAsync(rt, p, m.Need.Sha256);
                break;
            case AgentMsg.MOneofCase.Ready when m.Ready.RunId == rt.RunId:
                p.ReadyTcs.TrySetResult(m.Ready.Ok ? null : (m.Ready.Error.Length > 0 ? m.Ready.Error : "preparazione fallita"));
                break;
            case AgentMsg.MOneofCase.Metrics when m.Metrics.RunId == rt.RunId:
                HandleMetrics(rt, p, m.Metrics);
                break;
            case AgentMsg.MOneofCase.Errors when m.Errors.RunId == rt.RunId:
                HandleErrors(rt, m.Errors);
                break;
            case AgentMsg.MOneofCase.Finished when m.Finished.RunId == rt.RunId:
                p.Info = m.Finished;
                rt.Run.Progress.AgentStates[p.Conn.Name] = "Finished";
                p.FinishedTcs.TrySetResult();
                break;
        }
    }

    private void HandleMetrics(RunRuntime rt, Participant p, MetricsBatch b)
    {
        IntervalSnapshot snap;
        try { snap = MetricsWire.FromBatch(b); }
        catch (InvalidDataException ex)
        {
            rt.Warn($"agente '{p.Conn.Name}': batch di metriche scartato ({ex.Message})");
            return;
        }

        var saturated = p.Conn.TakeMaxCpu() > ProtocolInfo.SaturationCpuPercent;
        try { repo.WriteInterval(rt.RunId, p.Conn.AgentId, snap, saturated); }
        catch (Exception ex)
        {
            rt.Warn($"agente '{p.Conn.Name}': scrittura dell'intervallo {b.IntervalIndex} fallita ({ex.Message})");
            return;
        }

        long requests = 0, failedRequests = 0, completedTests = 0, failedTests = 0;
        foreach (var s in b.Series)
        {
            if (s.Key.HasPage || s.Key.HasRequest)
            {
                requests += s.Count;
                failedRequests += s.Failed;
            }
            else if (!s.Key.HasTransaction)
            {
                // Una serie senza page/request/transaction rappresenta il completamento del test.
                completedTests += s.Count;
                failedTests += s.Failed;
            }
        }

        Interlocked.Add(ref rt.Requests, requests);
        Interlocked.Add(ref rt.CompletedTests, completedTests);
        Interlocked.Add(ref rt.FailedTests, failedTests);
        var progressSnapshot = rt.ProgressAggregator.CreateSnapshot(
            RunProgressStatus.Running, rt.Run.Progress.StartedUtc, snap.EndUtc);
        rt.Run.Progress.Series = progressSnapshot.Series;
        Interlocked.Exchange(ref rt.CompletedTests, progressSnapshot.CompletedTests);
        Interlocked.Exchange(ref rt.FailedTests, progressSnapshot.FailedTests);
        var total = Interlocked.Add(ref rt.Errors, failedRequests);
        if (rt.StopOnErrors is { } n && total >= n) rt.StopOnErrorsHit = true;
    }

    private void HandleErrors(RunRuntime rt, ErrorBatch b)
    {
        var details = new List<ErrorDetail>();
        foreach (var e in b.Errors)
        {
            if (Interlocked.Increment(ref rt.ErrorDetails) > rt.MaxErrorDetails) break;
            details.Add(new ErrorDetail(e.Scenario, e.Test, e.HasPage ? e.Page : null, e.HasUrl ? e.Url : null,
                e.HasTransaction ? e.Transaction : null, e.StatusCode, e.HasErrorType ? e.ErrorType : null, e.HasMessage ? e.Message : null));
        }
        if (details.Count == 0) return;
        try { repo.WriteErrorDetails(rt.RunId, details); }
        catch (Exception ex) { rt.Warn($"scrittura del dettaglio errori fallita ({ex.Message})"); }
    }

    private async Task SendItemsAsync(RunRuntime rt, Participant p, IEnumerable<string> requested)
    {
        try
        {
            foreach (var sha in requested.Select(s => s.ToLowerInvariant()).Distinct())
            {
                if (!p.Allowed.Contains(sha) || !rt.Run.Package.Items.TryGetValue(sha, out var item))
                {
                    rt.Warn($"agente '{p.Conn.Name}': richiesto un hash che non è nel suo manifest ({sha})");
                    continue;
                }
                await using var fs = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
                var buffer = new byte[ProtocolInfo.ChunkSizeBytes];
                long seq = 0;
                while (true)
                {
                    var n = await fs.ReadAsync(buffer, rt.Cancel);
                    var last = fs.Position >= fs.Length;
                    var sent = await p.Conn.SendAsync(new ControllerMsg
                    {
                        Chunk = new PackageChunk { RunId = rt.RunId, Sha256 = sha, Sequence = seq++, Last = last, Data = ByteString.CopyFrom(buffer, 0, n) }
                    }, rt.Cancel);
                    if (!sent || last) break;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            rt.Warn($"agente '{p.Conn.Name}': invio dei file interrotto ({ex.Message})");
        }
    }

    private static void MarkLost(RunRuntime rt, Participant p, RunProgress progress)
    {
        if (p.FinishedTcs.Task.IsCompleted) return;
        p.Lost = true;
        progress.AgentStates[p.Conn.Name] = "Lost";
        rt.Warn($"agente '{p.Conn.Name}' disconnesso durante la run");
        p.ReadyTcs.TrySetResult("connessione persa");
        p.FinishedTcs.TrySetResult();
    }

    private static ControllerMsg MakePrepare(PlannedRun run, Participant p, ControllerOptions o)
    {
        var prepare = new PrepareRun
        {
            RunId = run.RunId, RunName = run.Name, ControllerName = o.Name, LoadTestPath = run.LoadTestEntryPath,
            PartitionIndex = p.A.Index, PartitionCount = p.A.Count
        };
        prepare.Manifest.AddRange(p.Manifest.Select(e => e.Clone()));
        return new ControllerMsg { Prepare = prepare };
    }

    // ---------- script e pulizia ----------

    private async Task RunScriptAsync(string what, string relativePath, PlannedRun run, RunRuntime rt, bool mustSucceed)
    {
        var baseDir = Path.GetDirectoryName(run.LoadTestPath)!;
        var full = Path.GetFullPath(Path.Combine(baseDir, relativePath));
        var prefix = baseDir.EndsWith(Path.DirectorySeparatorChar) ? baseDir : baseDir + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal) || !File.Exists(full))
            throw new InvalidOperationException($"{what}: script '{relativePath}' non trovato nella cartella del load test");

        var (code, output) = await ScriptRunner.RunAsync(full, baseDir, TimeSpan.FromSeconds(options.Value.ScriptTimeoutSec), rt.Cancel);
        if (code == 0) return;
        var message = $"{what}: lo script '{relativePath}' è terminato con codice {code}. {output}".Trim();
        if (mustSucceed) throw new InvalidOperationException(message);
        rt.Warn(message);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
