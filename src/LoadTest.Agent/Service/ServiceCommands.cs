using System.Security.Principal;

namespace LoadTest.Agent.Service;

/// <summary>`loadtest-agent install | uninstall` (e `loadtest agent install | uninstall` dalla CLI). Codici: 0 ok, 2 argomenti, 3 privilegi o errore di installazione.</summary>
public static class ServiceCommands
{
    public const string Usage = """
        loadtest-agent install --controller <url> [opzioni]    installa e avvia l'agente come servizio del sistema operativo
          --token <t>               token del controller (meglio la variabile LOADTEST_AGENT_TOKEN: non resta nella cronologia della shell)
          --name <nome>             nome dell'agente (default: nome della macchina)     --agent-id <id>   --max-users <n>
          --install-dir <dir>       Linux /opt/loadtest-agent · macOS /usr/local/lib/loadtest-agent · Windows %ProgramFiles%\LoadTest Agent
          --work-dir <dir>          Linux/macOS /var/lib/loadtest-agent · Windows %ProgramData%\LoadTest Agent
          --user <utente>           Linux `loadtest` · macOS `_loadtest` (creati se mancano) · Windows NT AUTHORITY\LocalService
          --client-cert <pfx> --client-cert-password <p>    mTLS      --insecure   solo sviluppo
          --no-start                installa senza avviare            --dry-run   mostra cosa farebbe, senza farlo
        loadtest-agent uninstall [--purge] [--install-dir <dir>] [--work-dir <dir>] [--dry-run]
          --purge                   elimina anche la cartella di lavoro (cache, dati)
        Servono privilegi di root/amministratore. Rilanciare `install` aggiorna binario e configurazione.
        """;

    private static readonly string[] ValueOptions =
        ["controller", "token", "name", "agent-id", "max-users", "install-dir", "work-dir", "user", "client-cert", "client-cert-password"];
    private static readonly string[] Flags = ["insecure", "no-start", "purge", "dry-run"];

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr,
        ICommandRunner? runner = null, OsKind? os = null, string? exePath = null, CancellationToken ct = default)
    {
        runner ??= new ProcessCommandRunner();
        var kind = os ?? (OperatingSystem.IsWindows() ? OsKind.Windows : OperatingSystem.IsMacOS() ? OsKind.MacOS : OsKind.Linux);
        var install = args[0] == "install";

        if (!TryParse(args[1..], out var o, out var dryRun, out var error))
        {
            stderr.WriteLine("errore: " + error);
            stderr.WriteLine(Usage);
            return 2;
        }
        if (install && string.IsNullOrEmpty(o.Token)) o.Token = Environment.GetEnvironmentVariable("LOADTEST_AGENT_TOKEN") ?? "";

        var problems = ServicePlanner.Validate(kind, o, install);
        if (problems.Count > 0)
        {
            foreach (var p in problems) stderr.WriteLine("errore: " + p);
            return 2;
        }

        var source = exePath ?? Environment.ProcessPath ?? throw new InvalidOperationException("percorso dell'eseguibile sconosciuto");
        var plan = install ? ServicePlanner.PlanInstall(kind, o, source) : ServicePlanner.PlanUninstall(kind, o);
        var paths = ServicePlanner.Resolve(kind, o);

        if (dryRun)
        {
            stdout.WriteLine($"[dry-run] {(install ? "installazione" : "rimozione")} del servizio '{paths.ServiceName}' ({kind}):");
            foreach (var s in plan) stdout.WriteLine("  - " + s.Describe());
            return 0;
        }

        if (!await IsElevatedAsync(kind, runner, ct))
        {
            stderr.WriteLine(kind == OsKind.Windows ? "errore: servono privilegi di amministratore (apri un terminale come amministratore)" : "errore: servono privilegi di root (usa sudo)");
            return 3;
        }

        stdout.WriteLine($"{(install ? "Installazione" : "Rimozione")} del servizio '{paths.ServiceName}' ({kind}):");
        try { await new PlanExecutor(runner, stdout).ExecuteAsync(plan, ct); }
        catch (InstallException ex)
        {
            stderr.WriteLine("errore: " + ex.Message);
            return 3;
        }

        stdout.WriteLine(install ? $"Fatto. {Hint(kind, paths)}" : "Fatto.");
        return 0;
    }

    private static string Hint(OsKind os, ServicePaths p) => os switch
    {
        OsKind.Windows => $"Stato: sc.exe query {p.ServiceName} · log: Visualizzatore eventi → Registri di Windows → Applicazione.",
        OsKind.MacOS => $"Stato: sudo launchctl print system/{p.ServiceName} · log: {p.LogDir}/agent.log",
        _ => $"Stato: systemctl status {p.ServiceName} · log: journalctl -u {p.ServiceName} -f"
    };

    private static async Task<bool> IsElevatedAsync(OsKind os, ICommandRunner runner, CancellationToken ct)
    {
        if (os == OsKind.Windows)
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        var (code, output) = await runner.RunAsync("id", ["-u"], ct);
        return code == 0 && output.Trim() == "0";
    }

    private static bool TryParse(string[] args, out InstallOptions o, out bool dryRun, out string error)
    {
        o = new InstallOptions();
        dryRun = false;
        error = "";
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal)) { error = $"argomento inatteso '{token}'"; return false; }
            var name = token[2..];
            string? inline = null;
            var eq = name.IndexOf('=');
            if (eq >= 0) { inline = name[(eq + 1)..]; name = name[..eq]; }

            if (Flags.Contains(name))
            {
                if (inline is not null) { error = $"--{name} non vuole un valore"; return false; }
                switch (name)
                {
                    case "insecure": o.Insecure = true; break;
                    case "no-start": o.NoStart = true; break;
                    case "purge": o.Purge = true; break;
                    case "dry-run": dryRun = true; break;
                }
                continue;
            }
            if (!ValueOptions.Contains(name)) { error = $"opzione sconosciuta '--{name}'"; return false; }
            string value;
            if (inline is not null) value = inline;
            else if (i + 1 < args.Length) value = args[++i];
            else { error = $"--{name}: manca il valore"; return false; }

            switch (name)
            {
                case "controller": o.Controller = value; break;
                case "token": o.Token = value; break;
                case "name": o.Name = value; break;
                case "agent-id": o.AgentId = value; break;
                case "max-users":
                    if (!int.TryParse(value, out var mu)) { error = $"--max-users: '{value}' non è un intero"; return false; }
                    o.MaxUsers = mu;
                    break;
                case "install-dir": o.InstallDir = value; break;
                case "work-dir": o.WorkDir = value; break;
                case "user": o.User = value; break;
                case "client-cert": o.ClientCert = value; break;
                case "client-cert-password": o.ClientCertPassword = value; break;
            }
        }
        return true;
    }
}
