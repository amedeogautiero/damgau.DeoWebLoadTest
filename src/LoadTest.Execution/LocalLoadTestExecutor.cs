using LoadTest.Contracts;
using LoadTest.Engine.Http;
using LoadTest.Engine.LoadTesting;
using LoadTest.Engine.Runtime;
using LoadTest.Metrics;
using LoadTest.Plugins;
using LoadTest.Reports;
using LoadTest.Reports.Analysis;
using LoadTest.Reports.Export;
using LoadTest.Reports.Storage;
using LoadTest.Sdk;

namespace LoadTest.Execution;

/// <summary>
/// Esecuzione locale di un LoadTest, senza Controller e senza Agent.
/// Contiene l'orchestrazione applicativa condivisa dai client (CLI, VS Code Host, ...).
/// </summary>
public static class LocalLoadTestExecutor
{
    private sealed class UnusedHttpExecutor : IHttpExecutor
    {
        public Task<ResponseContext> SendAsync(RequestContext request, CancellationToken ct) =>
            throw new NotSupportedException("l'esecutore HTTP si crea per utente con HttpFactory");
    }

    public static async Task<LocalLoadTestResult> RunAsync(
        LocalLoadTestOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.LoadTestFile))
            throw new ArgumentException("Il percorso del LoadTest è obbligatorio.", nameof(options));
        ArgumentNullException.ThrowIfNull(options.HttpFactory);

        var path = Path.GetFullPath(options.LoadTestFile);
        if (!File.Exists(path))
            throw new FileNotFoundException($"file non trovato: {path}", path);

        var def = LoadTestLoader.LoadFile(path);
        var baseDir = Path.GetDirectoryName(path)!;
        var pluginRoot = Path.Combine(baseDir, "plugins");
        var pluginDirs = Directory.Exists(pluginRoot)
            ? Directory.EnumerateDirectories(pluginRoot)
                .Where(d => File.Exists(Path.Combine(d, "plugin.yaml")))
                .OrderBy(d => d, StringComparer.Ordinal)
                .ToList()
            : [];

        AgentPlugins? plugins = null;
        try
        {
            if (pluginDirs.Count > 0)
                plugins = new AgentPlugins(pluginDirs);

            var loadPlugins = new List<(ILoadTestPlugin Plugin, IReadOnlyDictionary<string, string> Args)>();
            foreach (var pluginReference in def.LoadTestPlugins)
            {
                if (plugins is null)
                    throw new InvalidOperationException($"plugin '{pluginReference.Name}' non trovato in plugins/");

                foreach (var plugin in plugins.CreateLoadTestPlugins(pluginReference.Name, pluginReference.Params))
                    loadPlugins.Add((plugin, pluginReference.Params));
            }

            var runId = options.RunId ?? Guid.NewGuid().ToString("N");
            var name = !string.IsNullOrWhiteSpace(options.RunName)
                ? options.RunName!
                : !string.IsNullOrWhiteSpace(def.Name)
                    ? def.Name
                    : Path.GetFileNameWithoutExtension(path);

            var fullDb = Path.GetFullPath(options.ResultsDatabasePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullDb)!);

            using var repository = new SqliteResultsRepository(fullDb);
            var started = DateTimeOffset.UtcNow;
            var progress = new RunProgressAggregator(runId);

            void NotifyObserverError(Exception exception)
            {
                try { options.OnObserverError?.Invoke(exception); }
                catch { /* Un consumer non deve interrompere il motore. */ }
            }

            void PublishProgress(RunProgressStatus status, DateTimeOffset? capturedUtc = null)
            {
                if (options.OnProgress is null)
                    return;

                try
                {
                    options.OnProgress(progress.CreateSnapshot(status, started, capturedUtc));
                }
                catch (Exception ex)
                {
                    NotifyObserverError(ex);
                }
            }

            PublishProgress(RunProgressStatus.Preparing, started);
            repository.BeginRun(new RunRecord(runId, name, started, File.ReadAllText(path)));

            var sampler = new SystemSampler();
            var activeUsers = 0;

            var runnerOptions = new LoadTestRunnerOptions
            {
                Services = new EngineServices
                {
                    Http = new UnusedHttpExecutor(),
                    OnExecutionEvent = options.OnExecutionEvent is null ? null : executionEvent =>
                    {
                        try { options.OnExecutionEvent.Invoke(executionEvent); }
                        catch (Exception ex) { NotifyObserverError(ex); }
                    }
                },
                HttpFactory = options.HttpFactory,
                BaseDirectory = baseDir,
                Plugins = loadPlugins,
                CustomProfileResolver = n => plugins?.CreateLoadProfile(n),
                CounterProvider = n => n switch
                {
                    "users.active" => Volatile.Read(ref activeUsers),
                    "agents.cpu.avg" or "agents.cpu.max" => sampler.Sample().CpuPercent,
                    _ => null
                },
                Seed = options.Seed ?? 12345,
                RunId = runId,
                AgentId = "local",
                AgentName = "local",
                ControllerName = "local",
                OnInterval = snapshot =>
                {
                    progress.AddInterval(snapshot);
                    PublishProgress(RunProgressStatus.Running, snapshot.EndUtc);
                    repository.WriteInterval(runId, "local", snapshot);

                    try { options.OnInterval?.Invoke(snapshot); }
                    catch (Exception ex) { NotifyObserverError(ex); }
                },
                OnPluginError = NotifyObserverError
            };

            LoadTestResult result;
            try
            {
                var runner = new LoadTestRunner(def, runnerOptions);
                runner.Events.Heartbeat += heartbeat =>
                    Volatile.Write(ref activeUsers, heartbeat.ActiveUsers);

                PublishProgress(RunProgressStatus.Running, DateTimeOffset.UtcNow);
                result = await runner.RunAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PublishProgress(RunProgressStatus.Failed, DateTimeOffset.UtcNow);
                repository.CompleteRun(runId, new RunCompletion(
                    DateTimeOffset.UtcNow, "Failed", ex.Message, 0, 0, 0, []));
                throw new LocalLoadTestExecutionException(
                    "impossibile eseguire il load test: " + ex.Message, ex);
            }

            var progressStatus = result.Status switch
            {
                LoadTestStatus.Completed => RunProgressStatus.Completed,
                LoadTestStatus.Cancelled => RunProgressStatus.Cancelled,
                _ => RunProgressStatus.Failed
            };
            PublishProgress(progressStatus, result.EndedUtc);

            repository.WriteErrorDetails(runId, result.ErrorDetails.Select(m => new ErrorDetail(
                m.Scenario, m.Test, m.Page, m.Url, m.Transaction, m.StatusCode, m.ErrorType, m.ErrorMessage)));

            repository.CompleteRun(runId, new RunCompletion(
                result.EndedUtc,
                result.Status.ToString(),
                result.AbortReason,
                result.TestsCompleted,
                result.TestsFailed,
                result.RequestErrors,
                result.Warnings));

            var run = repository.GetRun(runId)
                ?? throw new InvalidOperationException($"run '{runId}' non trovata nel repository dopo il completamento");
            var summary = RunAnalyzer.Summarize(repository.ReadSamples(runId));

            using var csv = new StringWriter();
            CsvExporter.WriteSummary(csv, summary);

            return new LocalLoadTestResult(run, summary, csv.ToString());
        }
        finally
        {
            plugins?.Dispose();
        }
    }
}

/// <summary>Configurazione indipendente dal client per una run locale.</summary>
public sealed class LocalLoadTestOptions
{
    public required string LoadTestFile { get; init; }
    public string ResultsDatabasePath { get; init; } = "loadtest-results.db";
    public string? RunName { get; init; }
    public int? Seed { get; init; }
    public string? RunId { get; init; }
    public Func<IHttpExecutor> HttpFactory { get; init; } = () => new HttpClientExecutor();
    public Action<RunProgressSnapshot>? OnProgress { get; init; }
    public Action<ExecutionEvent>? OnExecutionEvent { get; init; }
    public Action<IntervalSnapshot>? OnInterval { get; init; }
    public Action<Exception>? OnObserverError { get; init; }
}

/// <summary>Risultato applicativo indipendente dai DTO della CLI e del Controller.</summary>
public sealed record LocalLoadTestResult(RunInfo Run, RunSummary Summary, string Csv);

public sealed class LocalLoadTestExecutionException(string message, Exception innerException)
    : Exception(message, innerException);
