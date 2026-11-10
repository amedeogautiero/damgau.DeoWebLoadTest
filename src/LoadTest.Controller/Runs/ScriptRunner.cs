using System.Diagnostics;

namespace LoadTest.Controller.Runs;

/// <summary>Esegue gli script setup/cleanup del rig SUL CONTROLLER (il protocollo non ha un campo per eseguirli sugli agenti: G17).</summary>
public static class ScriptRunner
{
    public static async Task<(int ExitCode, string Output)> RunAsync(string scriptPath, string workingDirectory, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        switch (Path.GetExtension(scriptPath).ToLowerInvariant())
        {
            case ".sh": psi.FileName = "/bin/sh"; psi.ArgumentList.Add(scriptPath); break;
            case ".ps1":
                psi.FileName = OperatingSystem.IsWindows() ? "powershell" : "pwsh";
                foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath }) psi.ArgumentList.Add(a);
                break;
            case ".cmd" or ".bat": psi.FileName = "cmd.exe"; psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(scriptPath); break;
            default: psi.FileName = scriptPath; break;
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"impossibile avviare '{scriptPath}'");
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"script '{Path.GetFileName(scriptPath)}' oltre il tempo massimo ({timeout.TotalSeconds:0} s)");
        }
        var text = (await stdout + await stderr).Trim();
        return (proc.ExitCode, text.Length > 2000 ? text[..2000] + "…" : text);
    }
}
