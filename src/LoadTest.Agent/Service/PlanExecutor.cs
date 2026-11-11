using System.ComponentModel;
using System.Diagnostics;

namespace LoadTest.Agent.Service;

public interface ICommandRunner
{
    Task<(int ExitCode, string Output)> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct);
}

public sealed class ProcessCommandRunner : ICommandRunner
{
    public async Task<(int ExitCode, string Output)> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"impossibile avviare {file}");
            var stdout = proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            var text = (await stdout + await stderr).Trim();
            return (proc.ExitCode, text.Length > 1000 ? text[..1000] + "…" : text);
        }
        catch (Win32Exception ex) { return (127, $"comando non trovato: {file} ({ex.Message})"); }
    }
}

public sealed class InstallException(string message) : Exception(message);

/// <summary>Esegue un piano di <see cref="ServicePlanner"/>. Un comando che fallisce ferma tutto (salvo quelli marcati IgnoreFailure); le eliminazioni non riuscite sono avvisi.</summary>
public sealed class PlanExecutor(ICommandRunner runner, TextWriter log)
{
    public async Task ExecuteAsync(IReadOnlyList<PlanStep> plan, CancellationToken ct)
    {
        foreach (var step in plan)
        {
            ct.ThrowIfCancellationRequested();
            log.WriteLine("  - " + step.Describe());
            switch (step)
            {
                case MkdirStep m:
                    Directory.CreateDirectory(m.Path);
                    SetMode(m.Path, m.Mode);
                    break;
                case CopyFileStep c:
                    await CopyAsync(c, ct);
                    break;
                case WriteFileStep w:
                    Write(w);
                    break;
                case RunStep r:
                    await RunAsync(r.File, r.Args, r.IgnoreFailure, ct);
                    break;
                case RunUnlessStep u:
                    var (check, _) = await runner.RunAsync(u.CheckFile, u.CheckArgs, ct);
                    if (check != 0) await RunAsync(u.File, u.Args, false, ct);
                    break;
                case DeleteStep d:
                    Delete(d);
                    break;
            }
        }
    }

    private async Task RunAsync(string file, IReadOnlyList<string> args, bool ignoreFailure, CancellationToken ct)
    {
        var (code, output) = await runner.RunAsync(file, args, ct);
        if (code != 0 && !ignoreFailure)
            throw new InstallException($"{file} {string.Join(" ", args)}: codice di uscita {code}" + (output.Length > 0 ? $". {output}" : ""));
    }

    private static void SetMode(string path, UnixFileMode? mode)
    {
        if (mode is { } m && !OperatingSystem.IsWindows()) File.SetUnixFileMode(path, m);
    }

    private static async Task CopyAsync(CopyFileStep c, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(c.To)!);
        if (string.Equals(Path.GetFullPath(c.From), Path.GetFullPath(c.To), StringComparison.Ordinal)) return;
        // su Windows il servizio appena fermato può tenere il file ancora per un istante
        for (var attempt = 1; ; attempt++)
        {
            try { File.Copy(c.From, c.To, overwrite: true); break; }
            catch (IOException) when (attempt < 10) { await Task.Delay(500, ct); }
        }
        SetMode(c.To, c.Mode);
    }

    private static void Write(WriteFileStep w)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(w.Path)!);
        if (File.Exists(w.Path)) File.Delete(w.Path);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (w.Mode is { } m && !OperatingSystem.IsWindows()) options.UnixCreateMode = m;   // permessi alla creazione: mai un istante a 644
        using (var fs = new FileStream(w.Path, options))
        using (var sw = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
            sw.Write(w.Content);
        SetMode(w.Path, w.Mode);
    }

    private void Delete(DeleteStep d)
    {
        try
        {
            if (d.Recursive) { if (Directory.Exists(d.Path)) Directory.Delete(d.Path, true); }
            else if (File.Exists(d.Path)) File.Delete(d.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.WriteLine($"    avviso: impossibile eliminare {d.Path} ({ex.Message}); se è l'eseguibile in uso, rimuovilo a mano dopo l'arresto");
        }
    }
}
