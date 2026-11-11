using System.Globalization;
using LoadTest.Cli.Args;
using LoadTest.Contracts;
using LoadTest.Controller.Api;
using LoadTest.Controller.Runs;

namespace LoadTest.Cli.Remote;

/// <summary>Run su un controller: invio (opzionale) del progetto, avvio, polling del progresso, stop con Ctrl+C, riassunto finale.</summary>
public static class RemoteRunner
{
    public static async Task<(RunSummaryDto Summary, Func<string> Csv)> RunAsync(
        string loadTestFile, string controllerUrl, ParsedArgs p, int? seed, bool needCsv, CliContext ctx,
        Action<RunProgressSnapshot>? onProgress = null)
    {
        var full = Path.GetFullPath(loadTestFile);
        if (!File.Exists(full)) throw new CliException(ExitCodes.Usage, $"file non trovato: {full}");
        var client = ctx.Services.ClientFactory(controllerUrl, Commands.CommonOptions.Token(p));

        var upload = p.Flag("upload") || (!p.Flag("no-upload") && !IsLoopback(controllerUrl) && p.Get("remote-path") is null);
        StartRunBody body;
        if (upload)
        {
            var maxMb = p.GetLong("max-upload-mb") ?? 256;
            var zip = ProjectZipper.Create(Path.GetDirectoryName(full)!, maxMb * 1024 * 1024);
            try
            {
                var projectId = await client.UploadProjectAsync(zip, CancellationToken.None);
                body = new StartRunBody(Path.GetFileName(full), p.Get("name"), seed, projectId);
            }
            finally { File.Delete(zip); }
        }
        else body = new StartRunBody(p.Get("remote-path") ?? full, p.Get("name"), seed);

        var runId = await client.StartRunAsync(body, CancellationToken.None);
        var quiet = p.Flag("quiet") || p.Get("json") == "-";
        var live = onProgress is not null;
        if (!quiet) ctx.Out.WriteLine($"run {runId} avviata su {controllerUrl}");

        string last = "";
        bool stopSent = false, abortSent = false;
        var failures = 0;
        long sequence = 0, previousRequests = 0;
        DateTimeOffset? previousCapture = null;
        RunSnapshot? snap;
        while (true)
        {
            if (ctx.Abort.IsCancellationRequested && !abortSent)
            {
                abortSent = true;
                await client.StopRunAsync(runId, abort: true, "interrotta dalla CLI (Ctrl+C due volte)", CancellationToken.None);
            }
            else if (ctx.Stop.IsCancellationRequested && !stopSent)
            {
                stopSent = true;
                ctx.Err.WriteLine("stop ordinato richiesto (Ctrl+C di nuovo per interrompere subito)");
                await client.StopRunAsync(runId, abort: false, "interrotta dalla CLI (Ctrl+C)", CancellationToken.None);
            }

            try
            {
                snap = await client.GetRunAsync(runId, CancellationToken.None);
                failures = 0;
            }
            catch (CliException) when (failures < 2)
            {
                failures++;
                await Task.Delay(ctx.Services.PollInterval);
                continue;
            }
            if (snap is null) throw new CliException(ExitCodes.Infrastructure, $"il controller non conosce la run {runId}");

            var captured = DateTimeOffset.UtcNow;
            var rps = previousCapture is { } previous && captured > previous
                ? Math.Max(0, snap.Requests - previousRequests) / (captured - previous).TotalSeconds
                : 0;
            var progress = CreateProgress(snap, captured, ++sequence, rps);
            TryPublish(onProgress, progress);
            previousCapture = captured;
            previousRequests = snap.Requests;

            var line = string.Create(CultureInfo.InvariantCulture, $"[{snap.State}] utenti {snap.ActiveUsers} · richieste {snap.Requests} · errori {snap.Errors}");
            if (!quiet && !live && line != last) ctx.Out.WriteLine(line);
            last = line;
            if (snap.Status is not null) break;
            await Task.Delay(ctx.Services.PollInterval);
        }

        var summary = await client.GetSummaryAsync(runId, CancellationToken.None)
            ?? throw new CliException(ExitCodes.Infrastructure, $"il controller non ha i risultati della run {runId}");
        var csv = needCsv ? await client.GetSummaryCsvAsync(runId, CancellationToken.None) : "";

        // Lo snapshot terminale arricchisce quello di polling con i conteggi dei test e le serie complete.
        TryPublish(onProgress, CreateFinalProgress(snap!, summary, DateTimeOffset.UtcNow, ++sequence));
        return (summary, () => csv);
    }

    private static RunProgressSnapshot CreateProgress(
        RunSnapshot run, DateTimeOffset capturedUtc, long sequence, double requestsPerSecond) =>
        new()
        {
            RunId = run.RunId,
            Sequence = sequence,
            Status = MapStatus(run.State, run.Status),
            StartedUtc = run.StartedUtc,
            CapturedUtc = capturedUtc,
            ElapsedSeconds = run.StartedUtc is { } started
                ? Math.Max(0, (capturedUtc - started).TotalSeconds) : 0,
            ActiveUsers = run.ActiveUsers,
            CompletedTests = run.CompletedTests,
            FailedTests = run.FailedTests,
            CompletedRequests = run.Requests,
            FailedRequests = run.Errors,
            RequestsPerSecond = requestsPerSecond
        };

    private static RunProgressSnapshot CreateFinalProgress(
        RunSnapshot run, RunSummaryDto summary, DateTimeOffset capturedUtc, long sequence)
    {
        var elapsed = run.StartedUtc is { } started
            ? Math.Max(0, (capturedUtc - started).TotalSeconds)
            : Math.Max(0, summary.MeasuredSeconds);
        var series = summary.Series.Select(s =>
        {
            if (!Enum.TryParse<RunProgressSeriesKind>(s.Kind, ignoreCase: true, out var kind))
                return null;
            return new RunProgressSeriesSnapshot
            {
                Kind = kind,
                ScenarioName = s.Scenario,
                TestName = s.Test,
                PageName = s.Page,
                RequestName = s.Request,
                TransactionName = s.Transaction,
                Count = s.Count,
                Failed = s.Failed,
                ErrorRatePercent = s.ErrorRatePercent,
                OperationsPerSecond = s.PerSecond,
                P50Milliseconds = s.P50Ms ?? 0,
                P95Milliseconds = s.P95Ms ?? 0,
                P99Milliseconds = s.P99Ms ?? 0,
                MaxMilliseconds = s.MaxMs ?? 0,
                BytesSent = s.BytesSent,
                BytesReceived = s.BytesReceived
            };
        }).Where(s => s is not null).Cast<RunProgressSeriesSnapshot>().ToArray();

        return new RunProgressSnapshot
        {
            RunId = run.RunId,
            Sequence = sequence,
            Status = MapStatus(run.State, summary.Status),
            StartedUtc = summary.StartedUtc,
            CapturedUtc = capturedUtc,
            ElapsedSeconds = elapsed,
            ActiveUsers = run.ActiveUsers,
            CompletedTests = summary.TestsCompleted,
            FailedTests = summary.TestsFailed,
            CompletedRequests = run.Requests,
            FailedRequests = run.Errors,
            RequestsPerSecond = series.Where(s => s.Kind == RunProgressSeriesKind.Request).Sum(s => s.OperationsPerSecond),
            Series = series
        };
    }

    private static RunProgressStatus MapStatus(string state, string? terminalStatus)
    {
        if (terminalStatus is not null)
            return terminalStatus.Equals("Completed", StringComparison.OrdinalIgnoreCase)
                ? RunProgressStatus.Completed
                : terminalStatus.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)
                    ? RunProgressStatus.Cancelled
                    : terminalStatus.Equals("Aborted", StringComparison.OrdinalIgnoreCase)
                        ? RunProgressStatus.Failed
                        : RunProgressStatus.Failed;

        if (state.Contains("stop", StringComparison.OrdinalIgnoreCase)) return RunProgressStatus.Stopping;
        if (state.Equals("Planned", StringComparison.OrdinalIgnoreCase)) return RunProgressStatus.Queued;
        if (state.Equals("Setup", StringComparison.OrdinalIgnoreCase)
            || state.Contains("prepar", StringComparison.OrdinalIgnoreCase)) return RunProgressStatus.Preparing;
        return RunProgressStatus.Running;
    }

    private static void TryPublish(Action<RunProgressSnapshot>? callback, RunProgressSnapshot snapshot)
    {
        if (callback is null) return;
        try { callback(snapshot); }
        catch { /* Un consumer del progresso non deve interrompere il test remoto. */ }
    }

    private static bool IsLoopback(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.IsLoopback || string.Equals(u.Host, "localhost", StringComparison.OrdinalIgnoreCase));
}
