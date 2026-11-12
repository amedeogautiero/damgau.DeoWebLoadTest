using System.Globalization;
using LoadTest.Cli.Ci;
using LoadTest.Contracts;
using LoadTest.Controller.Api;
using LoadTest.Engine.LoadTesting;
using LoadTest.Execution;
using LoadTest.Metrics;
using LoadTest.Plugins;

namespace LoadTest.Cli.Local;

/// <summary>
/// Adattatore CLI per l'esecuzione locale condivisa. La logica applicativa risiede in LoadTest.Execution.
/// Qui restano soltanto la traduzione degli errori e la presentazione specifica della console.
/// </summary>
public static class LocalRunner
{
    public static async Task<(RunSummaryDto Summary, Func<string> Csv)> RunAsync(
        string loadTestFile,
        string dbPath,
        string? runName,
        int? seed,
        bool quiet,
        CliContext ctx,
        Action<RunProgressSnapshot>? onProgress = null)
    {
        long runRequests = 0;
        long runErrors = 0;
        var startedUtc = DateTimeOffset.UtcNow;
        var gate = new object();

        try
        {
            var result = await LocalLoadTestExecutor.RunAsync(new LocalLoadTestOptions
            {
                LoadTestFile = loadTestFile,
                ResultsDatabasePath = dbPath,
                RunName = runName,
                Seed = seed,
                HttpFactory = ctx.Services.HttpFactory,
                OnProgress = onProgress,
                OnObserverError = ex => ctx.Err.WriteLine("avviso: " + ex.Message),
                OnInterval = snapshot =>
                {
                    long requests = 0;
                    long errors = 0;

                    foreach (var (key, series) in snapshot.Series)
                    {
                        if (key.Kind != MetricKind.Request)
                            continue;

                        requests += series.Count;
                        errors += series.Failed;
                    }

                    lock (gate)
                    {
                        runRequests += requests;
                        runErrors += errors;

                        if (!quiet && onProgress is null)
                        {
                            ctx.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                                $"[{(snapshot.EndUtc - startedUtc).TotalSeconds,5:0}s] " +
                                $"utenti {snapshot.ActiveUsers} · richieste {runRequests} · errori {runErrors}"));
                        }
                    }
                }
            }, ctx.Stop);

            return (SummaryMapper.From(result.Run, result.Summary), () => result.Csv);
        }
        catch (FileNotFoundException ex)
        {
            throw new CliException(ExitCodes.Usage, ex.Message);
        }
        catch (LoadTestLoadException ex)
        {
            throw new CliException(ExitCodes.Usage,
                "load test non valido:\n  - " + string.Join("\n  - ", ex.Errors));
        }
        catch (PluginManifestException ex)
        {
            throw new CliException(ExitCodes.Usage, "plugin non valido: " + ex.Message);
        }
        catch (Exception ex) when (ex is PluginLoadException or InvalidOperationException)
        {
            throw new CliException(ExitCodes.Usage, ex.Message);
        }
        catch (LocalLoadTestExecutionException ex)
        {
            throw new CliException(ExitCodes.Usage, ex.Message);
        }
    }
}
