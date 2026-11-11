using System.Globalization;
using LoadTest.Agent;
using LoadTest.Contracts;
using LoadTest.Controller.Api;
using LoadTest.Engine.LoadTesting;
using LoadTest.Engine.Runtime;
using LoadTest.Metrics;
using LoadTest.Plugins;
using LoadTest.Reports;
using LoadTest.Reports.Analysis;
using LoadTest.Reports.Export;
using LoadTest.Reports.Storage;
using LoadTest.Sdk;

namespace LoadTest.Cli.Local;

/// <summary>
/// Esecuzione locale (senza controller né agenti): un LoadTestRunner nel processo, risultati in SQLite.
/// La sezione `rig` è ignorata. Plugin `loadTest` e profili `custom` si cercano in plugins/ accanto al load test.
/// Ctrl+C = stop (la run si chiude, i risultati fin lì misurati restano salvati).
/// </summary>
public static class LocalRunner
{
    private sealed class UnusedHttpExecutor : IHttpExecutor
    {
        public Task<ResponseContext> SendAsync(RequestContext request, CancellationToken ct) =>
            throw new NotSupportedException("l'esecutore HTTP si crea per utente con HttpFactory");
    }

    public static async Task<(RunSummaryDto Summary, Func<string> Csv)> RunAsync(
        string loadTestFile, string dbPath, string? runName, int? seed, bool quiet, CliContext ctx,
        Action<RunProgressSnapshot>? onProgress = null)
    {
        var path = Path.GetFullPath(loadTestFile);
        if (!File.Exists(path)) throw new CliException(ExitCodes.Usage, $"file non trovato: {path}");

        LoadTestDefinition def;
        try { def = LoadTestLoader.LoadFile(path); }
        catch (LoadTestLoadException ex) { throw new CliException(ExitCodes.Usage, "load test non valido:\n  - " + string.Join("\n  - ", ex.Errors)); }

        var baseDir = Path.GetDirectoryName(path)!;
        var pluginRoot = Path.Combine(baseDir, "plugins");
        var pluginDirs = Directory.Exists(pluginRoot)
            ? Directory.EnumerateDirectories(pluginRoot).Where(d => File.Exists(Path.Combine(d, "plugin.yaml"))).OrderBy(d => d, StringComparer.Ordinal).ToList()
            : [];

        AgentPlugins? plugins = null;
        try
        {
            try { if (pluginDirs.Count > 0) plugins = new AgentPlugins(pluginDirs); }
            catch (PluginManifestException ex) { throw new CliException(ExitCodes.Usage, "plugin non valido: " + ex.Message); }

            var loadPlugins = new List<(ILoadTestPlugin Plugin, IReadOnlyDictionary<string, string> Args)>();
            try
            {
                foreach (var pr in def.LoadTestPlugins)
                {
                    if (plugins is null) throw new InvalidOperationException($"plugin '{pr.Name}' non trovato in plugins/");
                    foreach (var pl in plugins.CreateLoadTestPlugins(pr.Name, pr.Params)) loadPlugins.Add((pl, pr.Params));
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or PluginLoadException)
            {
                throw new CliException(ExitCodes.Usage, ex.Message);
            }

            var runId = Guid.NewGuid().ToString("N");
            var name = !string.IsNullOrWhiteSpace(runName) ? runName! : !string.IsNullOrWhiteSpace(def.Name) ? def.Name : Path.GetFileNameWithoutExtension(path);
            var fullDb = Path.GetFullPath(dbPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullDb)!);

            using var repo = new SqliteResultsRepository(fullDb);
            var started = DateTimeOffset.UtcNow;
            var progress = new RunProgressAggregator(runId);
            void PublishProgress(RunProgressStatus status, DateTimeOffset? capturedUtc = null)
            {
                if (onProgress is null) return;
                try { onProgress(progress.CreateSnapshot(status, started, capturedUtc)); }
                catch (Exception ex) { ctx.Err.WriteLine("avviso: consumer del progresso non disponibile: " + ex.Message); }
            }
            PublishProgress(RunProgressStatus.Preparing, started);
            repo.BeginRun(new RunRecord(runId, name, started, File.ReadAllText(path)));

            var sampler = new SystemSampler();
            var activeUsers = 0;
            var lockObj = new object();
            long runRequests = 0, runErrors = 0;

            var options = new LoadTestRunnerOptions
            {
                Services = new EngineServices { Http = new UnusedHttpExecutor() },
                HttpFactory = ctx.Services.HttpFactory,
                BaseDirectory = baseDir,
                Plugins = loadPlugins,
                CustomProfileResolver = n => plugins?.CreateLoadProfile(n),
                // stessi nomi dei contatori del controller (G20)
                CounterProvider = n => n switch
                {
                    "users.active" => Volatile.Read(ref activeUsers),
                    "agents.cpu.avg" or "agents.cpu.max" => sampler.Sample().CpuPercent,
                    _ => null
                },
                Seed = seed ?? 12345, RunId = runId, AgentId = "local", AgentName = "local", ControllerName = "local",
                OnInterval = snap =>
                {
                    progress.AddInterval(snap);
                    PublishProgress(RunProgressStatus.Running, snap.EndUtc);
                    repo.WriteInterval(runId, "local", snap);
                    long req = 0, failed = 0;
                    foreach (var (key, s) in snap.Series)
                        if (key.Kind == MetricKind.Request) { req += s.Count; failed += s.Failed; }
                    lock (lockObj)
                    {
                        runRequests += req; runErrors += failed;
                        if (!quiet && onProgress is null)
                            ctx.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                                $"[{(snap.EndUtc - started).TotalSeconds,5:0}s] utenti {snap.ActiveUsers} · richieste {runRequests} · errori {runErrors}"));
                    }
                },
                OnPluginError = ex => ctx.Err.WriteLine("avviso: errore in un plugin: " + ex.Message)
            };

            LoadTestResult result;
            try
            {
                var runner = new LoadTestRunner(def, options);
                runner.Events.Heartbeat += a => Volatile.Write(ref activeUsers, a.ActiveUsers);
                PublishProgress(RunProgressStatus.Running, DateTimeOffset.UtcNow);
                result = await runner.RunAsync(ctx.Stop);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PublishProgress(RunProgressStatus.Failed, DateTimeOffset.UtcNow);
                var status = "Failed";
                repo.CompleteRun(runId, new RunCompletion(DateTimeOffset.UtcNow, status, ex.Message, 0, 0, 0, []));
                throw new CliException(ExitCodes.Usage, "impossibile eseguire il load test: " + ex.Message);
            }

            var progressStatus = result.Status switch
            {
                LoadTestStatus.Completed => RunProgressStatus.Completed,
                LoadTestStatus.Cancelled => RunProgressStatus.Cancelled,
                _ => RunProgressStatus.Failed
            };
            PublishProgress(progressStatus, result.EndedUtc);

            repo.WriteErrorDetails(runId, result.ErrorDetails.Select(m => new ErrorDetail(
                m.Scenario, m.Test, m.Page, m.Url, m.Transaction, m.StatusCode, m.ErrorType, m.ErrorMessage)));
            repo.CompleteRun(runId, new RunCompletion(result.EndedUtc, result.Status.ToString(),
                result.AbortReason, result.TestsCompleted, result.TestsFailed, result.RequestErrors, result.Warnings));

            var summary = RunAnalyzer.Summarize(repo.ReadSamples(runId));
            var dto = SummaryMapper.From(repo.GetRun(runId)!, summary);
            var csv = new StringWriter();
            CsvExporter.WriteSummary(csv, summary);
            var csvText = csv.ToString();
            return (dto, () => csvText);
        }
        finally { plugins?.Dispose(); }
    }
}
