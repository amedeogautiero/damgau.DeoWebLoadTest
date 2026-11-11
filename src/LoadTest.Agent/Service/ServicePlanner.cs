using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LoadTest.Agent.Service;

/// <summary>
/// Piano di installazione/rimozione del servizio dell'agente, come elenco di passi puri (nessun effetto): si può mostrare con --dry-run
/// e provare nei test. L'esecuzione sta in <see cref="PlanExecutor"/>.
/// Linux: unit systemd (Type=notify). macOS: LaunchDaemon. Windows: servizio SCM con ripartenza automatica.
/// La configurazione (token compreso) sta in appsettings.json accanto all'eseguibile, leggibile solo dall'utente del servizio.
/// </summary>
public static partial class ServicePlanner
{
    public const string LinuxServiceName = "loadtest-agent";
    public const string MacLabel = "com.loadtest.agent";
    public const string WindowsServiceName = "LoadTestAgent";

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_-]{0,31}$")]
    private static partial Regex UnixUserRx();

    [GeneratedRegex(@"^[\w .\\\-$]{1,100}$")]
    private static partial Regex WindowsAccountRx();

    // ---------- percorsi ----------

    public static ServicePaths Resolve(OsKind os, InstallOptions o, string? exeName = null)
    {
        switch (os)
        {
            case OsKind.Windows:
            {
                var pf = Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files";
                var pd = Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData";
                var install = o.InstallDir ?? Join(os, pf, "LoadTest Agent");
                return new ServicePaths(os, WindowsServiceName, install, o.WorkDir ?? Join(os, pd, "LoadTest Agent"),
                    o.User ?? @"NT AUTHORITY\LocalService", Join(os, install, "loadtest-agent.exe"), Join(os, install, "appsettings.json"), null, null);
            }
            case OsKind.MacOS:
            {
                var install = o.InstallDir ?? "/usr/local/lib/loadtest-agent";
                return new ServicePaths(os, MacLabel, install, o.WorkDir ?? "/var/lib/loadtest-agent", o.User ?? "_loadtest",
                    Join(os, install, "loadtest-agent"), Join(os, install, "appsettings.json"), $"/Library/LaunchDaemons/{MacLabel}.plist", "/var/log/loadtest-agent");
            }
            default:
            {
                var install = o.InstallDir ?? "/opt/loadtest-agent";
                return new ServicePaths(os, LinuxServiceName, install, o.WorkDir ?? "/var/lib/loadtest-agent", o.User ?? "loadtest",
                    Join(os, install, "loadtest-agent"), Join(os, install, "appsettings.json"), $"/etc/systemd/system/{LinuxServiceName}.service", null);
            }
        }
    }

    private static string Join(OsKind os, string dir, string name)
    {
        var sep = os == OsKind.Windows ? '\\' : '/';
        return dir.TrimEnd('/', '\\') + sep + name;
    }

    // ---------- validazione ----------

    public static IReadOnlyList<string> Validate(OsKind os, InstallOptions o, bool install)
    {
        var errors = new List<string>();
        if (install)
        {
            if (string.IsNullOrWhiteSpace(o.Controller)) errors.Add("manca --controller <url>");
            else if (!Uri.TryCreate(o.Controller, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
                errors.Add($"--controller: '{o.Controller}' non è un URL http(s) valido");
            if (o.MaxUsers < 0) errors.Add("--max-users non può essere negativo");
        }
        if (o.User is { } user)
        {
            var ok = os == OsKind.Windows ? WindowsAccountRx().IsMatch(user) : UnixUserRx().IsMatch(user);
            if (!ok) errors.Add($"--user: nome '{user}' non valido");
        }
        foreach (var (opt, dir) in new[] { ("--install-dir", o.InstallDir), ("--work-dir", o.WorkDir) })
            if (dir is not null && (dir.Contains('\0') || dir.Contains('\n') || dir.Trim().Length < 2 || dir.Trim() is "/" or @"\"))
                errors.Add($"{opt}: percorso non valido");
        return errors;
    }

    // ---------- installazione ----------

    public static IReadOnlyList<PlanStep> PlanInstall(OsKind os, InstallOptions o, string sourceExe)
    {
        var p = Resolve(os, o);
        var json = RenderSettingsJson(o, p.WorkDir);
        return os switch
        {
            OsKind.Windows => PlanInstallWindows(p, o, sourceExe, json),
            OsKind.MacOS => PlanInstallMac(p, o, sourceExe, json),
            _ => PlanInstallLinux(p, o, sourceExe, json)
        };
    }

    private static List<PlanStep> PlanInstallLinux(ServicePaths p, InstallOptions o, string src, string json)
    {
        var steps = new List<PlanStep>
        {
            new RunUnlessStep("id", ["-u", p.User], "useradd",
                ["--system", "--no-create-home", "--home-dir", p.WorkDir, "--shell", "/usr/sbin/nologin", p.User]),
            new MkdirStep(p.InstallDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute),
            new MkdirStep(p.WorkDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute),
            new RunStep("systemctl", ["stop", p.ServiceName], IgnoreFailure: true),
            new CopyFileStep(src, p.ExePath, ExecutableMode),
            new WriteFileStep(p.SettingsPath, json, UnixFileMode.UserRead | UnixFileMode.UserWrite, Secret: true),
            new RunStep("chown", [p.User + ":", p.SettingsPath]),
            new RunStep("chown", ["-R", p.User + ":", p.WorkDir]),
            new WriteFileStep(p.DefinitionPath!, RenderSystemdUnit(p), PublicFileMode),
            new RunStep("systemctl", ["daemon-reload"]),
            new RunStep("systemctl", ["enable", p.ServiceName])
        };
        if (!o.NoStart) steps.Add(new RunStep("systemctl", ["restart", p.ServiceName]));
        return steps;
    }

    private static List<PlanStep> PlanInstallMac(ServicePaths p, InstallOptions o, string src, string json)
    {
        var steps = new List<PlanStep>
        {
            new RunStep("/bin/sh", ["-c", RenderMacUserScript(p.User)]),
            new MkdirStep(p.InstallDir, ExecutableMode),
            new MkdirStep(p.WorkDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute),
            new MkdirStep(p.LogDir!, ExecutableMode),
            new RunStep("launchctl", ["bootout", "system/" + p.ServiceName], IgnoreFailure: true),
            new CopyFileStep(src, p.ExePath, ExecutableMode),
            // binari scaricati: via la quarantena e firma ad hoc (obbligatoria su Apple Silicon)
            new RunStep("xattr", ["-dr", "com.apple.quarantine", p.InstallDir], IgnoreFailure: true),
            new RunStep("codesign", ["--force", "--sign", "-", p.ExePath], IgnoreFailure: true),
            new WriteFileStep(p.SettingsPath, json, UnixFileMode.UserRead | UnixFileMode.UserWrite, Secret: true),
            new RunStep("chown", [p.User, p.SettingsPath]),
            new RunStep("chown", ["-R", p.User, p.WorkDir]),
            new RunStep("chown", ["-R", p.User, p.LogDir!]),
            new WriteFileStep(p.DefinitionPath!, RenderLaunchdPlist(p), PublicFileMode),
            new RunStep("chown", ["root:wheel", p.DefinitionPath!]),
            new RunStep("launchctl", ["enable", "system/" + p.ServiceName], IgnoreFailure: true)
        };
        if (!o.NoStart)
        {
            steps.Add(new RunStep("launchctl", ["bootstrap", "system", p.DefinitionPath!]));
            steps.Add(new RunStep("launchctl", ["kickstart", "-k", "system/" + p.ServiceName], IgnoreFailure: true));
        }
        return steps;
    }

    private static List<PlanStep> PlanInstallWindows(ServicePaths p, InstallOptions o, string src, string json)
    {
        var binPath = "\"" + p.ExePath + "\"";
        var steps = new List<PlanStep>
        {
            new MkdirStep(p.InstallDir),
            new MkdirStep(p.WorkDir),
            new RunStep("sc.exe", ["stop", p.ServiceName], IgnoreFailure: true),
            new CopyFileStep(src, p.ExePath),
            new WriteFileStep(p.SettingsPath, json, Secret: true),
            // il token sta in appsettings.json: solo SYSTEM, amministratori e l'account del servizio possono leggerlo
            new RunStep("icacls", [p.SettingsPath, "/inheritance:r", "/grant:r", "SYSTEM:(R)", @"BUILTIN\Administrators:(F)", $"{p.User}:(R)"]),
            new RunStep("icacls", [p.WorkDir, "/grant", $"{p.User}:(OI)(CI)M"]),
            // create fallisce se esiste già (aggiornamento): config la riallinea comunque
            new RunStep("sc.exe", ["create", p.ServiceName, "binPath=", binPath, "start=", "auto", "DisplayName=", "LoadTest agent", "obj=", p.User], IgnoreFailure: true),
            new RunStep("sc.exe", ["config", p.ServiceName, "binPath=", binPath, "start=", "auto", "obj=", p.User]),
            new RunStep("sc.exe", ["description", p.ServiceName, "Agente di carico: si connette al controller LoadTest ed esegue i test"]),
            new RunStep("sc.exe", ["failure", p.ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/30000"])
        };
        if (!o.NoStart) steps.Add(new RunStep("sc.exe", ["start", p.ServiceName]));
        return steps;
    }

    // ---------- rimozione ----------

    public static IReadOnlyList<PlanStep> PlanUninstall(OsKind os, InstallOptions o)
    {
        var p = Resolve(os, o);
        var steps = new List<PlanStep>();
        switch (os)
        {
            case OsKind.Windows:
                steps.Add(new RunStep("sc.exe", ["stop", p.ServiceName], IgnoreFailure: true));
                steps.Add(new RunStep("sc.exe", ["delete", p.ServiceName], IgnoreFailure: true));
                break;
            case OsKind.MacOS:
                steps.Add(new RunStep("launchctl", ["bootout", "system/" + p.ServiceName], IgnoreFailure: true));
                steps.Add(new DeleteStep(p.DefinitionPath!));
                break;
            default:
                steps.Add(new RunStep("systemctl", ["stop", p.ServiceName], IgnoreFailure: true));
                steps.Add(new RunStep("systemctl", ["disable", p.ServiceName], IgnoreFailure: true));
                steps.Add(new DeleteStep(p.DefinitionPath!));
                steps.Add(new RunStep("systemctl", ["daemon-reload"], IgnoreFailure: true));
                break;
        }
        steps.Add(new DeleteStep(p.InstallDir, Recursive: true));
        if (o.Purge)
        {
            steps.Add(new DeleteStep(p.WorkDir, Recursive: true));
            if (p.LogDir is not null) steps.Add(new DeleteStep(p.LogDir, Recursive: true));
        }
        return steps;
    }

    // ---------- contenuti ----------

    private const UnixFileMode ExecutableMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    private const UnixFileMode PublicFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    public static string RenderSettingsJson(InstallOptions o, string workDir)
    {
        var agent = new Dictionary<string, object?> { ["ControllerUrl"] = o.Controller, ["WorkDirectory"] = workDir };
        if (!string.IsNullOrEmpty(o.Token)) agent["Token"] = o.Token;
        if (!string.IsNullOrEmpty(o.Name)) agent["Name"] = o.Name;
        if (!string.IsNullOrEmpty(o.AgentId)) agent["AgentId"] = o.AgentId;
        if (o.MaxUsers > 0) agent["MaxUsers"] = o.MaxUsers;
        if (o.Insecure) agent["AllowInvalidServerCertificate"] = true;
        if (!string.IsNullOrEmpty(o.ClientCert)) agent["ClientCertificatePath"] = o.ClientCert;
        if (!string.IsNullOrEmpty(o.ClientCertPassword)) agent["ClientCertificatePassword"] = o.ClientCertPassword;
        return JsonSerializer.Serialize(new Dictionary<string, object?> { ["Agent"] = agent }, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string RenderSystemdUnit(ServicePaths p) => $"""
        [Unit]
        Description=LoadTest agent
        Wants=network-online.target
        After=network-online.target

        [Service]
        Type=notify
        ExecStart={p.ExePath}
        WorkingDirectory={p.WorkDir}
        User={p.User}
        Restart=always
        RestartSec=5
        # un generatore di carico apre molte connessioni e molti thread
        LimitNOFILE=1048576
        TasksMax=infinity
        # il file singolo estrae le librerie native in una cartella scrivibile dall'utente del servizio
        Environment=HOME={p.WorkDir}
        Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR={p.WorkDir}/.net
        NoNewPrivileges=true
        PrivateTmp=true
        ProtectSystem=strict
        ProtectHome=true
        ReadWritePaths={p.WorkDir}

        [Install]
        WantedBy=multi-user.target

        """;

    public static string RenderLaunchdPlist(ServicePaths p)
    {
        static string E(string s) => SecurityElement.Escape(s) ?? "";
        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        sb.AppendLine("""<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">""");
        sb.AppendLine("""<plist version="1.0">""");
        sb.AppendLine("<dict>");
        void Key(string k, string v) { sb.AppendLine($"  <key>{E(k)}</key>"); sb.AppendLine($"  {v}"); }
        Key("Label", $"<string>{E(p.ServiceName)}</string>");
        Key("ProgramArguments", $"<array><string>{E(p.ExePath)}</string></array>");
        Key("WorkingDirectory", $"<string>{E(p.WorkDir)}</string>");
        Key("UserName", $"<string>{E(p.User)}</string>");
        Key("EnvironmentVariables",
            $"<dict><key>HOME</key><string>{E(p.WorkDir)}</string><key>DOTNET_BUNDLE_EXTRACT_BASE_DIR</key><string>{E(p.WorkDir)}/.net</string></dict>");
        Key("RunAtLoad", "<true/>");
        Key("KeepAlive", "<true/>");
        Key("ThrottleInterval", "<integer>5</integer>");
        Key("SoftResourceLimits", "<dict><key>NumberOfFiles</key><integer>65536</integer></dict>");
        Key("HardResourceLimits", "<dict><key>NumberOfFiles</key><integer>65536</integer></dict>");
        Key("StandardOutPath", $"<string>{E(p.LogDir ?? "/var/log")}/agent.log</string>");
        Key("StandardErrorPath", $"<string>{E(p.LogDir ?? "/var/log")}/agent.err.log</string>");
        sb.AppendLine("</dict>");
        sb.AppendLine("</plist>");
        return sb.ToString();
    }

    /// <summary>Crea l'utente e il gruppo di servizio nascosti con dscl (UID/GID liberi da 300). Il nome è già validato dal chiamante.</summary>
    public static string RenderMacUserScript(string user) => $$"""
        set -e
        U={{user}}
        if dscl . -read /Users/$U >/dev/null 2>&1; then exit 0; fi
        id=300
        while dscl . -list /Users UniqueID | awk '{print $2}' | grep -qx "$id" || dscl . -list /Groups PrimaryGroupID | awk '{print $2}' | grep -qx "$id"; do id=$((id+1)); done
        dscl . -create /Groups/$U
        dscl . -create /Groups/$U PrimaryGroupID $id
        dscl . -create /Users/$U
        dscl . -create /Users/$U UserShell /usr/bin/false
        dscl . -create /Users/$U RealName "LoadTest agent"
        dscl . -create /Users/$U UniqueID $id
        dscl . -create /Users/$U PrimaryGroupID $id
        dscl . -create /Users/$U NFSHomeDirectory /var/empty
        dscl . -create /Users/$U IsHidden 1
        """;
}
