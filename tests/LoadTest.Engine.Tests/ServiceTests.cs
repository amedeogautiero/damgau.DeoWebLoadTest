using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using LoadTest.Agent;
using LoadTest.Agent.Service;
using LoadTest.Agent.Transfer;

namespace LoadTest.Engine.Tests;

internal sealed class FakeRunner(Func<string, IReadOnlyList<string>, (int, string)>? behavior = null) : ICommandRunner
{
    public List<string> Calls { get; } = [];

    public Task<(int ExitCode, string Output)> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct)
    {
        Calls.Add(file + " " + string.Join(" ", args));
        var (code, output) = behavior?.Invoke(file, args) ?? (0, "");
        return Task.FromResult((code, output));
    }
}

public class ServiceTests
{
    private static string TempDir() => Directory.CreateTempSubdirectory("svc").FullName;

    private static InstallOptions Opts() => new() { Controller = "https://ctrl.interno:5001", Token = "t0k\"en", Name = "pi-1", MaxUsers = 40 };

    private static T Step<T>(IEnumerable<PlanStep> plan, Func<T, bool>? where = null) where T : PlanStep =>
        plan.OfType<T>().Single(s => where?.Invoke(s) ?? true);

    private static bool Runs(IEnumerable<PlanStep> plan, string file, params string[] args) =>
        plan.OfType<RunStep>().Any(r => r.File == file && args.All(a => r.Args.Contains(a)));

    // ---------- Linux ----------

    [Fact]
    public void Linux_plan_creates_user_installs_binary_and_a_hardened_notify_unit()
    {
        var plan = ServicePlanner.PlanInstall(OsKind.Linux, Opts(), "/tmp/loadtest-agent");

        var user = Step<RunUnlessStep>(plan);
        Assert.Equal("id", user.CheckFile);
        Assert.Equal("useradd", user.File);
        Assert.Contains("loadtest", user.Args);

        Assert.Equal("/opt/loadtest-agent/loadtest-agent", Step<CopyFileStep>(plan).To);

        var settings = Step<WriteFileStep>(plan, w => w.Path.EndsWith("appsettings.json"));
        Assert.True(settings.Secret);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, settings.Mode);       // 600: il token non è leggibile da altri
        using var doc = JsonDocument.Parse(settings.Content);
        var agent = doc.RootElement.GetProperty("Agent");
        Assert.Equal("https://ctrl.interno:5001", agent.GetProperty("ControllerUrl").GetString());
        Assert.Equal("t0k\"en", agent.GetProperty("Token").GetString());                // caratteri speciali conservati
        Assert.Equal(40, agent.GetProperty("MaxUsers").GetInt32());
        Assert.Equal("/var/lib/loadtest-agent", agent.GetProperty("WorkDirectory").GetString());

        var unit = Step<WriteFileStep>(plan, w => w.Path == "/etc/systemd/system/loadtest-agent.service");
        Assert.False(unit.Secret);
        Assert.DoesNotContain("t0k", unit.Content);                                       // il token non finisce nell'unit (leggibile da tutti)
        foreach (var line in new[] { "Type=notify", "ExecStart=/opt/loadtest-agent/loadtest-agent", "User=loadtest", "Restart=always",
                                      "LimitNOFILE=1048576", "NoNewPrivileges=true", "ProtectSystem=strict", "ReadWritePaths=/var/lib/loadtest-agent" })
            Assert.Contains(line, unit.Content);

        // ordine: la configurazione esiste e ha il proprietario giusto prima di avviare
        var index = plan.ToList();
        var write = index.IndexOf(settings);
        var chown = index.FindIndex(s => s is RunStep { File: "chown" } r && r.Args.Contains("/opt/loadtest-agent/appsettings.json"));
        var restart = index.FindIndex(s => s is RunStep { File: "systemctl" } r && r.Args.Contains("restart"));
        Assert.True(write < chown && chown < restart);
        Assert.True(Runs(plan, "systemctl", "enable", "loadtest-agent"));
    }

    [Fact]
    public void No_start_skips_the_start_command_and_options_are_reflected()
    {
        var o = Opts();
        o.NoStart = true; o.Insecure = true; o.User = "svc-lt"; o.InstallDir = "/srv/lt"; o.WorkDir = "/srv/lt-data"; o.ClientCert = "/etc/lt/c.pfx";
        var plan = ServicePlanner.PlanInstall(OsKind.Linux, o, "/tmp/x");

        Assert.False(Runs(plan, "systemctl", "restart"));
        Assert.Equal("/srv/lt/loadtest-agent", Step<CopyFileStep>(plan).To);
        using var doc = JsonDocument.Parse(Step<WriteFileStep>(plan, w => w.Path.EndsWith("appsettings.json")).Content);
        var agent = doc.RootElement.GetProperty("Agent");
        Assert.True(agent.GetProperty("AllowInvalidServerCertificate").GetBoolean());
        Assert.Equal("/etc/lt/c.pfx", agent.GetProperty("ClientCertificatePath").GetString());
        Assert.Contains("User=svc-lt", Step<WriteFileStep>(plan, w => w.Path.EndsWith(".service")).Content);
    }

    // ---------- macOS ----------

    private static XElement Dict(XDocument d) => d.Root!.Element("dict")!;

    // il valore è l'elemento che segue <key> (in mezzo c'è solo testo con spazi)
    private static XElement ElementOf(XElement dict, string key) =>
        dict.Elements("key").Single(k => k.Value == key).ElementsAfterSelf().First();

    private static string ValueOf(XElement dict, string key) => ElementOf(dict, key).Value;

    private static XDocument ParsePlist(string xml) =>
        XDocument.Load(XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore }));

    [Fact]
    public void MacOS_plan_builds_a_valid_launchd_plist_and_service_user()
    {
        var o = Opts();
        o.WorkDir = "/var/lib/lt&co";                                   // '&' deve essere escapato nell'XML
        var plan = ServicePlanner.PlanInstall(OsKind.MacOS, o, "/tmp/loadtest-agent");

        var plist = ParsePlist(Step<WriteFileStep>(plan, w => w.Path.EndsWith(".plist")).Content);
        var dict = Dict(plist);
        Assert.Equal("com.loadtest.agent", ValueOf(dict, "Label"));
        Assert.Equal("_loadtest", ValueOf(dict, "UserName"));
        Assert.Equal("/var/lib/lt&co", ValueOf(dict, "WorkingDirectory"));
        Assert.Equal("true", ElementOf(dict, "KeepAlive").Name.LocalName);
        Assert.Equal("65536", ElementOf(dict, "SoftResourceLimits").Element("integer")!.Value);
        Assert.DoesNotContain("t0k", Step<WriteFileStep>(plan, w => w.Path.EndsWith(".plist")).Content);   // il token è in appsettings.json (600), non nel plist

        var script = Step<RunStep>(plan, r => r.File == "/bin/sh").Args[1];
        Assert.Contains("dscl . -create /Users/$U", script);
        Assert.Contains("U=_loadtest", script);
        Assert.True(Runs(plan, "launchctl", "bootstrap", "system"));
        Assert.True(Runs(plan, "codesign", "--force"));
        Assert.True(Runs(plan, "xattr", "com.apple.quarantine"));
    }

    // ---------- Windows ----------

    [Fact]
    public void Windows_plan_registers_a_self_restarting_service_and_locks_down_the_settings_file()
    {
        var o = Opts();
        o.InstallDir = @"C:\Agenti\LoadTest";
        o.WorkDir = @"D:\dati\lt";
        var plan = ServicePlanner.PlanInstall(OsKind.Windows, o, @"C:\temp\loadtest-agent.exe");

        Assert.Equal(@"C:\Agenti\LoadTest\loadtest-agent.exe", Step<CopyFileStep>(plan).To);
        Assert.All(plan.OfType<MkdirStep>().Cast<PlanStep>().Concat(plan.OfType<WriteFileStep>()), s =>
        {
            Assert.Null(s is MkdirStep m ? m.Mode : ((WriteFileStep)s).Mode);     // nessun permesso Unix su Windows
        });

        var create = plan.OfType<RunStep>().Single(r => r.File == "sc.exe" && r.Args[0] == "create");
        Assert.True(create.IgnoreFailure);                                         // esiste già in caso di aggiornamento
        Assert.Equal("LoadTestAgent", create.Args[1]);
        Assert.Equal("\"C:\\Agenti\\LoadTest\\loadtest-agent.exe\"", create.Args[create.Args.ToList().IndexOf("binPath=") + 1]);
        Assert.Equal(@"NT AUTHORITY\LocalService", create.Args[create.Args.ToList().IndexOf("obj=") + 1]);

        var config = plan.OfType<RunStep>().Single(r => r.File == "sc.exe" && r.Args[0] == "config");
        Assert.False(config.IgnoreFailure);                                        // la riallineamento deve riuscire
        Assert.True(Runs(plan, "sc.exe", "failure", "restart/5000/restart/5000/restart/30000"));

        var icacls = plan.OfType<RunStep>().First(r => r.File == "icacls");
        Assert.Contains("/inheritance:r", icacls.Args);
        Assert.Contains(@"BUILTIN\Administrators:(F)", icacls.Args);
        Assert.Equal(@"C:\Agenti\LoadTest\appsettings.json", icacls.Args[0]);
        Assert.Equal("sc.exe", ((RunStep)plan.Last()).File);
        Assert.Equal("start", ((RunStep)plan.Last()).Args[0]);
    }

    // ---------- rimozione ----------

    [Fact]
    public void Uninstall_plans_stop_remove_and_purge_only_on_request()
    {
        var linux = ServicePlanner.PlanUninstall(OsKind.Linux, new InstallOptions());
        Assert.True(Runs(linux, "systemctl", "disable"));
        Assert.Contains(linux.OfType<DeleteStep>(), d => d.Path == "/etc/systemd/system/loadtest-agent.service");
        Assert.Contains(linux.OfType<DeleteStep>(), d => d.Path == "/opt/loadtest-agent" && d.Recursive);
        Assert.DoesNotContain(linux.OfType<DeleteStep>(), d => d.Path == "/var/lib/loadtest-agent");

        var purged = ServicePlanner.PlanUninstall(OsKind.Linux, new InstallOptions { Purge = true });
        Assert.Contains(purged.OfType<DeleteStep>(), d => d.Path == "/var/lib/loadtest-agent" && d.Recursive);

        Assert.True(Runs(ServicePlanner.PlanUninstall(OsKind.Windows, new InstallOptions()), "sc.exe", "delete"));
        var mac = ServicePlanner.PlanUninstall(OsKind.MacOS, new InstallOptions());
        Assert.True(Runs(mac, "launchctl", "bootout"));
        Assert.Contains(mac.OfType<DeleteStep>(), d => d.Path.EndsWith("com.loadtest.agent.plist"));
    }

    // ---------- validazione e comandi ----------

    [Fact]
    public void Validation_rejects_bad_urls_and_names_that_could_inject_commands()
    {
        Assert.Empty(ServicePlanner.Validate(OsKind.Linux, Opts(), install: true));
        Assert.NotEmpty(ServicePlanner.Validate(OsKind.Linux, new InstallOptions(), install: true));                                  // manca il controller
        Assert.NotEmpty(ServicePlanner.Validate(OsKind.Linux, new InstallOptions { Controller = "ftp://x" }, install: true));
        Assert.NotEmpty(ServicePlanner.Validate(OsKind.Linux, new InstallOptions { Controller = "https://x", User = "a; rm -rf /" }, install: true));
        Assert.NotEmpty(ServicePlanner.Validate(OsKind.MacOS, new InstallOptions { Controller = "https://x", User = "x$(id)" }, install: true));
        Assert.Empty(ServicePlanner.Validate(OsKind.Windows, new InstallOptions { Controller = "https://x", User = @"NT AUTHORITY\NetworkService" }, install: true));
        Assert.Empty(ServicePlanner.Validate(OsKind.Linux, new InstallOptions(), install: false));                                     // uninstall non vuole il controller
    }

    private static async Task<(int Code, string Out, string Err, FakeRunner Runner)> Cmd(string[] args, OsKind os, Func<string, IReadOnlyList<string>, (int, string)>? behavior = null)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var runner = new FakeRunner(behavior);
        var code = await ServiceCommands.RunAsync(args, o, e, runner, os, "/tmp/loadtest-agent");
        return (code, o.ToString(), e.ToString(), runner);
    }

    [Fact]
    public async Task Commands_dry_run_shows_the_plan_without_secrets_and_touches_nothing()
    {
        var (code, o, _, runner) = await Cmd(["install", "--controller", "https://c:5001", "--token", "segretissimo", "--dry-run"], OsKind.Linux);
        Assert.Equal(0, code);
        Assert.Contains("useradd", o);
        Assert.Contains("systemctl", o);
        Assert.DoesNotContain("segretissimo", o);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Commands_report_usage_errors_and_missing_privileges()
    {
        Assert.Equal(2, (await Cmd(["install"], OsKind.Linux)).Code);                                         // manca --controller
        Assert.Equal(2, (await Cmd(["install", "--controller", "https://c", "--boh"], OsKind.Linux)).Code);   // opzione sconosciuta
        Assert.Equal(2, (await Cmd(["install", "--controller", "https://c", "--max-users", "x"], OsKind.Linux)).Code);

        var (code, _, err, runner) = await Cmd(["install", "--controller", "https://c:5001"], OsKind.Linux, (file, _) => file == "id" ? (0, "1000") : (0, ""));
        Assert.Equal(3, code);
        Assert.Contains("root", err);
        Assert.Equal(["id -u"], runner.Calls);                                                                  // nessun'altra azione senza privilegi
    }

    // ---------- esecuzione del piano ----------

    [Fact]
    public async Task Executor_writes_files_with_restrictive_modes_copies_runs_and_stops_on_failure()
    {
        var dir = TempDir();
        var src = Path.Combine(dir, "src.bin");
        File.WriteAllText(src, "binario");
        var settings = Path.Combine(dir, "inst", "appsettings.json");
        var runner = new FakeRunner((file, _) => file == "check-ok" ? (0, "") : file == "check-fail" ? (1, "") : file == "boom" ? (5, "esploso") : (0, ""));

        var plan = new PlanStep[]
        {
            new MkdirStep(Path.Combine(dir, "inst"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute),
            new WriteFileStep(settings, "{\"a\":1}", UnixFileMode.UserRead | UnixFileMode.UserWrite, Secret: true),
            new CopyFileStep(src, Path.Combine(dir, "inst", "copy.bin")),
            new RunStep("ok", ["1"]),
            new RunStep("boom", [], IgnoreFailure: true),
            new RunUnlessStep("check-ok", [], "non-eseguito", []),
            new RunUnlessStep("check-fail", [], "eseguito", []),
            new DeleteStep(Path.Combine(dir, "inst", "copy.bin")),
            new DeleteStep(Path.Combine(dir, "non-esiste"), Recursive: true)
        };
        await new PlanExecutor(runner, new StringWriter()).ExecuteAsync(plan, default);

        Assert.Equal("{\"a\":1}", File.ReadAllText(settings));
        Assert.False(File.Exists(Path.Combine(dir, "inst", "copy.bin")));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(settings));
        Assert.Contains("eseguito ", string.Join("|", runner.Calls) + " ");
        Assert.DoesNotContain("non-eseguito", string.Join("|", runner.Calls));

        var ex = await Assert.ThrowsAsync<InstallException>(() =>
            new PlanExecutor(runner, new StringWriter()).ExecuteAsync([new RunStep("boom", ["x"]), new RunStep("mai", [])], default));
        Assert.Contains("esploso", ex.Message);
        Assert.DoesNotContain("mai ", string.Join("|", runner.Calls) + " ");
    }

    // ---------- cache per hash: scadenza e dimensione ----------

    private static string AddFile(AgentCaches c, char fill, int bytes, double ageDays)
    {
        var sha = new string(fill, 64);
        var path = c.Files.PathFor(sha);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));
        return sha;
    }

    private static string AddPlugin(AgentCaches c, char fill, double ageDays)
    {
        var sha = new string(fill, 64);
        var dir = Path.Combine(c.Plugins.Root, sha);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.yaml"), "x");
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddDays(-ageDays));
        return sha;
    }

    [Fact]
    public void Cache_eviction_drops_old_entries_but_never_protected_ones()
    {
        var c = new AgentCaches(TempDir());
        var oldFile = AddFile(c, 'a', 10, 30);
        var freshFile = AddFile(c, 'b', 10, 1);
        var oldButProtected = AddFile(c, 'c', 10, 40);
        var oldPlugin = AddPlugin(c, 'd', 30);
        var freshPlugin = AddPlugin(c, 'e', 2);

        var r = c.Evict(new HashSet<string> { oldButProtected }, maxBytes: 0, TimeSpan.FromDays(14));

        Assert.Equal(2, r.Removed);
        Assert.False(c.Files.Contains(oldFile));
        Assert.False(Directory.Exists(Path.Combine(c.Plugins.Root, oldPlugin)));
        Assert.True(c.Files.Contains(freshFile));
        Assert.True(c.Files.Contains(oldButProtected));
        Assert.True(Directory.Exists(Path.Combine(c.Plugins.Root, freshPlugin)));
    }

    [Fact]
    public void Cache_eviction_by_size_removes_least_recent_first_and_stops_under_the_limit()
    {
        var c = new AgentCaches(TempDir());
        var d3 = AddFile(c, 'a', 100, 3);
        var d2 = AddFile(c, 'b', 100, 2);
        var d1 = AddFile(c, 'c', 100, 1);

        var r = c.Evict(new HashSet<string>(), maxBytes: 150, TimeSpan.FromDays(14));

        Assert.Equal(2, r.Removed);
        Assert.Equal(200, r.FreedBytes);
        Assert.False(c.Files.Contains(d3));
        Assert.False(c.Files.Contains(d2));
        Assert.True(c.Files.Contains(d1));

        // la voce più vecchia protetta resta, anche se è la prima candidata
        var c2 = new AgentCaches(TempDir());
        var protectedOldest = AddFile(c2, 'a', 100, 3);
        AddFile(c2, 'b', 100, 2);
        AddFile(c2, 'c', 100, 1);
        c2.Evict(new HashSet<string> { protectedOldest }, maxBytes: 150, TimeSpan.FromDays(14));
        Assert.True(c2.Files.Contains(protectedOldest));
        Assert.False(c2.Files.Contains(new string('b', 64)));
    }

    [Fact]
    public void Touch_marks_entries_as_recently_used_so_they_survive_age_eviction()
    {
        var c = new AgentCaches(TempDir());
        var file = AddFile(c, 'a', 10, 30);
        var plugin = AddPlugin(c, 'b', 30);
        c.Touch(file, isPlugin: false);
        c.Touch(plugin, isPlugin: true);

        Assert.Equal(0, c.Evict(new HashSet<string>(), 0, TimeSpan.FromDays(14)).Removed);
        Task.Delay(50).Wait();
        Assert.Equal(2, c.Evict(new HashSet<string>(), 0, TimeSpan.FromMilliseconds(1)).Removed);   // con un'età massima minuscola ora sì
    }

    [Fact]
    public void Agent_options_have_bounded_cache_defaults()
    {
        var o = new AgentOptions();
        Assert.Equal(2L << 30, o.CacheMaxBytes);
        Assert.Equal(14, o.CacheMaxAgeDays);
    }
}
