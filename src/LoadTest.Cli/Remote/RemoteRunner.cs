using System.Globalization;
using LoadTest.Cli.Args;
using LoadTest.Controller.Api;

namespace LoadTest.Cli.Remote;

/// <summary>Run su un controller: invio (opzionale) del progetto, avvio, avanzamento a polling, stop con Ctrl+C, riassunto finale.</summary>
public static class RemoteRunner
{
    public static async Task<(RunSummaryDto Summary, Func<string> Csv)> RunAsync(
        string loadTestFile, string controllerUrl, ParsedArgs p, int? seed, bool needCsv, CliContext ctx)
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
        if (!quiet) ctx.Out.WriteLine($"run {runId} avviata su {controllerUrl}");

        string last = "";
        bool stopSent = false, abortSent = false;
        var failures = 0;
        Controller.Runs.RunSnapshot? snap;
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

            var line = string.Create(CultureInfo.InvariantCulture, $"[{snap.State}] utenti {snap.ActiveUsers} · richieste {snap.Requests} · errori {snap.Errors}");
            if (!quiet && line != last) ctx.Out.WriteLine(line);
            last = line;
            if (snap.Status is not null) break;
            await Task.Delay(ctx.Services.PollInterval);
        }

        var summary = await client.GetSummaryAsync(runId, CancellationToken.None)
            ?? throw new CliException(ExitCodes.Infrastructure, $"il controller non ha i risultati della run {runId}");
        var csv = needCsv ? await client.GetSummaryCsvAsync(runId, CancellationToken.None) : "";
        return (summary, () => csv);
    }


    private static bool IsLoopback(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.IsLoopback || string.Equals(u.Host, "localhost", StringComparison.OrdinalIgnoreCase));
}
