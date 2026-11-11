using System.IO.Compression;
using System.Threading.Channels;
using Grpc.Core;
using LoadTest.Agent;
using LoadTest.Controller;
using LoadTest.Controller.Agents;
using LoadTest.Controller.Grpc;
using LoadTest.Controller.Packaging;
using LoadTest.Controller.Rig;
using LoadTest.Controller.Runs;
using LoadTest.Engine.LoadTesting;
using LoadTest.Metrics;
using LoadTest.Plugins;
using LoadTest.Protocol;
using LoadTest.Reports.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LoadTest.Engine.Tests;

internal sealed class ChannelStreamReader<T>(Channel<T> channel) : IAsyncStreamReader<T>
{
    public T Current { get; private set; } = default!;

    public async Task<bool> MoveNext(CancellationToken ct)
    {
        try
        {
            if (await channel.Reader.WaitToReadAsync(ct) && channel.Reader.TryRead(out var v))
            {
                Current = v;
                return true;
            }
        }
        catch (OperationCanceledException) { }
        return false;
    }
}

internal sealed class ChannelStreamWriter<T>(Channel<T> channel) : IServerStreamWriter<T>
{
    public WriteOptions? WriteOptions { get; set; }
    public Task WriteAsync(T message) => channel.Writer.WriteAsync(message).AsTask();
}

public class ControllerTests
{
    private static string TempDir() => Directory.CreateTempSubdirectory("ctl").FullName;

    private static AgentConnection Conn(string id, string name, string rid = "linux-x64", string os = "Linux 6.1", int maxUsers = 0) =>
        new(new Hello { AgentId = id, Name = name, Rid = rid, Os = os, MaxUsers = maxUsers, ProtocolVersion = ProtocolInfo.ProtocolVersion, SdkVersion = "1.0.0.0" });

    private static LoadTestDefinition Def(string runSettings, string pattern = "{ type: constant, users: 2 }") =>
        LoadTestLoader.Load(new StringReader($$"""
            version: 1
            name: t
            runSettings: {{runSettings}}
            scenarios:
              - name: S
                thinkProfile: { mode: off }
                testMix: { model: users, tests: [ { test: a.webtest.yaml, percent: 100 } ] }
                loadPattern: {{pattern}}
            """));

    // ---------- ripartizione ----------

    [Fact]
    public void IntegerSplit_is_exact_and_respects_caps()
    {
        Assert.Equal([5, 3, 2], IntegerSplit.LargestRemainder(10, [0.5, 0.3, 0.2]));
        var third = IntegerSplit.LargestRemainder(10, [1.0 / 3, 1.0 / 3, 1.0 / 3]);
        Assert.Equal(10, third.Sum());
        Assert.Equal([4, 3, 3], third);
        Assert.Equal([2, 4, 4], IntegerSplit.Allocate(10, [1.0 / 3, 1.0 / 3, 1.0 / 3], [2, 0, 0]));
        Assert.Equal(4, IntegerSplit.Allocate(10, [0.5, 0.5], [2, 2]).Sum());   // capacità insufficiente: somma sotto il totale
    }

    [Fact]
    public void Csv_partition_keeps_header_and_splits_rows_by_share()
    {
        var data = "id,nome\n1,a\n2,b\n3,c\n4,d\n"u8.ToArray();
        var s = CsvPartitioner.Slice(data, [0.5, 0.5]).Select(System.Text.Encoding.UTF8.GetString).ToArray();
        Assert.Equal("id,nome\n1,a\n3,c\n", s[0]);
        Assert.Equal("id,nome\n2,b\n4,d\n", s[1]);

        var quoted = "id,nota\n1,\"riga1\nriga2\"\n2,x\n"u8.ToArray();
        var q = CsvPartitioner.Slice(quoted, [0.5, 0.5]).Select(System.Text.Encoding.UTF8.GetString).ToArray();
        Assert.Contains("riga1\nriga2", q[0]);     // il record con a capo tra virgolette non si spezza
        Assert.Contains("2,x", q[1]);
    }

    [Fact]
    public void Rig_loader_and_planner_assign_shares_caps_and_iterations()
    {
        var rig = RigLoader.Load(new StringReader("""
            version: 1
            rig:
              agents:
                - { name: ag1, weight: 2, maxUsers: 5, attributes: { os: linux } }
                - { name: ag2, weight: 1 }
              deployment: [ { path: data/utenti.csv, partition: true } ]
              setupScript: scripts/setup.sh
            """));
        Assert.Equal(2, rig.Agents.Count);
        Assert.True(rig.Deployment[0].Partition);
        Assert.Equal("scripts/setup.sh", rig.SetupScript);

        var def = Def("{ warmupSec: 0, iterations: 6, coolDownSec: 1 }");
        var plan = RigPlanner.Plan(def, rig, [Conn("i1", "ag1"), Conn("i2", "ag2", maxUsers: 10)], allowPartial: false);

        Assert.Equal(2.0 / 3, plan.Assignments[0].Share, 6);
        Assert.Equal(5, plan.Assignments[0].MaxUsers);
        Assert.Equal(10, plan.Assignments[1].MaxUsers);
        Assert.Equal([4, 2], plan.Assignments.Select(a => a.Iterations).ToArray());
        Assert.NotEqual(plan.Assignments[0].UserIdBase, plan.Assignments[1].UserIdBase);
    }

    [Fact]
    public void Planner_reports_missing_agents_attribute_mismatch_and_too_few_iterations()
    {
        var rig = RigLoader.Load(new StringReader("version: 1\nrig:\n  agents:\n    - { name: ag1 }\n    - { name: nope }\n"));
        var def = Def("{ durationMin: 1 }");
        var ex = Assert.Throws<RunPlanException>(() => RigPlanner.Plan(def, rig, [Conn("i1", "ag1")], false));
        Assert.Contains("nope", ex.Message);
        Assert.Single(RigPlanner.Plan(def, rig, [Conn("i1", "ag1")], allowPartial: true).Assignments);

        var win = RigLoader.Load(new StringReader("version: 1\nrig:\n  agents:\n    - { name: ag1, attributes: { os: windows } }\n"));
        Assert.Throws<RunPlanException>(() => RigPlanner.Plan(def, win, [Conn("i1", "ag1")], false));

        var few = Def("{ iterations: 1 }");
        var two = RigLoader.Load(new StringReader("version: 1\nrig:\n  agents:\n    - { name: a }\n    - { name: b }\n"));
        Assert.Throws<RunPlanException>(() => RigPlanner.Plan(few, two, [Conn("1", "a"), Conn("2", "b")], false));
    }

    [Fact]
    public void Auth_accepts_valid_token_or_client_certificate_only()
    {
        var o = new ControllerOptions { AgentTokens = ["segreto"] };
        Assert.True(ControllerAuth.IsAuthorized(o, "Bearer segreto", false));
        Assert.False(ControllerAuth.IsAuthorized(o, "Bearer sbagliato", false));
        Assert.False(ControllerAuth.IsAuthorized(o, null, false));
        Assert.False(ControllerAuth.IsAuthorized(o, null, hasClientCertificate: true));   // certificato senza mTLS richiesto: non basta
        o.RequireClientCertificate = true;
        Assert.True(ControllerAuth.IsAuthorized(o, null, hasClientCertificate: true));
        Assert.True(ControllerAuth.IsAuthorized(new ControllerOptions { AllowAnonymousAgents = true }, null, false));
        Assert.False(ControllerAuth.IsAuthorized(new ControllerOptions(), null, false));
    }

    // ---------- profili di carico ----------

    [Fact]
    public void LoadDriver_sends_updates_only_when_targets_change()
    {
        var def = Def("{ durationMin: 1 }", "{ type: step, start: 10, max: 50, step: 20, stepSec: 60 }");
        var d = new LoadDriver("r1", def, [0.5, 0.5], [0, 0], _ => null);

        var first = d.Tick(TimeSpan.Zero, 0);
        Assert.Equal(2, first.Count);
        Assert.All(first, u => Assert.Equal(5, Assert.Single(u.Update.Targets).Users));
        Assert.Empty(d.Tick(TimeSpan.FromSeconds(30), 10));
        var next = d.Tick(TimeSpan.FromSeconds(60), 10);
        Assert.All(next, u => Assert.Equal(15, Assert.Single(u.Update.Targets).Users));
    }

    [Fact]
    public void LoadDriver_goal_profile_follows_the_counter()
    {
        var def = Def("{ durationMin: 1 }",
            "{ type: goal, counter: cpu, target: 70, tolerance: 5, minUsers: 10, maxUsers: 30, adjustment: 10, intervalSec: 30 }");
        double? cpu = 40;
        var d = new LoadDriver("r1", def, [1.0], [0], _ => cpu);
        Assert.Equal(10, d.Tick(TimeSpan.Zero, 0).Single().Update.Targets.Single().Users);
        Assert.Equal(20, d.Tick(TimeSpan.FromSeconds(30), 10).Single().Update.Targets.Single().Users);
        cpu = 95;
        Assert.Equal(10, d.Tick(TimeSpan.FromSeconds(60), 20).Single().Update.Targets.Single().Users);
    }

    // ---------- pacchetti ----------

    private static string WriteProject(string loadTestYamlTail = "", bool withPlugin = false)
    {
        var dir = TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "data"));
        Directory.CreateDirectory(Path.Combine(dir, "files"));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "data", "utenti.csv"), "id,nome\n1,a\n2,b\n3,c\n4,d\n");
        File.WriteAllText(Path.Combine(dir, "files", "up.txt"), "contenuto");
        File.WriteAllText(Path.Combine(dir, "sub", "altro.webtest.yaml"),
            "version: 1\nbaseUrl: https://x.it\nsteps:\n  - { name: B, request: { method: GET, url: /b } }\n");
        File.WriteAllText(Path.Combine(dir, "ricerca.webtest.yaml"), """
            version: 1
            name: Ricerca
            baseUrl: https://x.it
            data:
              utenti: { type: csv, path: data/utenti.csv }
            steps:
              - name: Upload
                request: { method: POST, url: /u, body: { file: files/up.txt } }
              - call: sub/altro.webtest.yaml
            """);
        if (withPlugin)
        {
            var p = Path.Combine(dir, "plugins", "Mio");
            Directory.CreateDirectory(p);
            File.WriteAllText(Path.Combine(p, "plugin.yaml"), "name: MioPlugin\nversion: 1.0.0\nsdk: \">=1.0 <2.0\"\nkinds: [ loadTest ]\nentry: MioPlugin.dll\nfiles: [ ]\n");
            File.WriteAllText(Path.Combine(p, "MioPlugin.dll"), "contenuto");
        }
        File.WriteAllText(Path.Combine(dir, "run.loadtest.yaml"), $$"""
            version: 1
            name: t
            runSettings: { warmupSec: 0, durationMin: 1, coolDownSec: 1 }
            scenarios:
              - name: S
                thinkProfile: { mode: off }
                testMix: { model: users, tests: [ { test: ricerca.webtest.yaml, percent: 100 } ] }
                loadPattern: { type: constant, users: 2 }
            rig:
              agents:
                - { name: ag1 }
                - { name: ag2 }
              deployment: [ { path: data/utenti.csv, partition: true } ]
            {{loadTestYamlTail}}
            """);
        return dir;
    }

    private static PackageSet Build(string dir, IReadOnlyList<double>? shares = null)
    {
        var path = Path.Combine(dir, "run.loadtest.yaml");
        return RunPackager.Build(path, LoadTestLoader.LoadFile(path), RigLoader.LoadFile(path), shares ?? [0.5, 0.5], Path.Combine(TempDir(), "w"), null);
    }

    private static string ReadGz(string path)
    {
        using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var r = new StreamReader(gz);
        return r.ReadToEnd();
    }

    [Fact]
    public void Packager_collects_tests_calls_files_and_partitions_data()
    {
        var set = Build(WriteProject());

        var common = set.Common.ToDictionary(e => e.Path);
        Assert.Equal(EntryKind.LoadTest, common["run.loadtest.yaml"].Kind);
        Assert.Equal(EntryKind.WebTest, common["ricerca.webtest.yaml"].Kind);
        Assert.Equal(EntryKind.WebTest, common["sub/altro.webtest.yaml"].Kind);     // raggiunto da `call:`
        Assert.Equal(EntryKind.Deployment, common["files/up.txt"].Kind);           // body.file
        Assert.DoesNotContain("data/utenti.csv", common.Keys);                      // partizionato: per agente

        var a0 = Assert.Single(set.PerAgent[0]);
        var a1 = Assert.Single(set.PerAgent[1]);
        Assert.True(a0.Partitioned && a1.Partitioned);
        Assert.NotEqual(a0.Sha256, a1.Sha256);
        Assert.Equal("id,nome\n1,a\n3,c\n", ReadGz(set.Items[a0.Sha256].Path));
        Assert.Equal("id,nome\n2,b\n4,d\n", ReadGz(set.Items[a1.Sha256].Path));
        Assert.Equal(set.Common.Count + 1, set.ManifestFor(0).Count);
        Assert.All(set.Common, e => Assert.Equal(ContentEncoding.FileGzip, e.Encoding));
    }

    [Fact]
    public void Packager_zips_plugin_packages_by_package_hash_and_reports_missing_ones()
    {
        var dir = WriteProject("plugins:\n  loadTest: [ { name: MioPlugin } ]", withPlugin: true);
        var set = Build(dir);
        var plugin = Assert.Single(set.Common, e => e.Kind == EntryKind.Plugin);
        Assert.Equal(PluginPackage.Open(Path.Combine(dir, "plugins", "Mio")).Sha256, plugin.Sha256);
        Assert.Equal(ContentEncoding.PackageZip, plugin.Encoding);
        using var zip = ZipFile.OpenRead(set.Items[plugin.Sha256].Path);
        Assert.Contains(zip.Entries, e => e.FullName == "plugin.yaml");
        Assert.Contains(zip.Entries, e => e.FullName == "MioPlugin.dll");

        var missing = WriteProject("plugins:\n  loadTest: [ { name: Nope } ]");
        var ex = Assert.Throws<RunPlanException>(() => Build(missing));
        Assert.Contains("Nope", ex.Message);
    }

    [Fact]
    public void Packager_rejects_files_outside_the_load_test_folder_and_missing_data()
    {
        var dir = WriteProject();
        File.WriteAllText(Path.Combine(dir, "ricerca.webtest.yaml"), """
            version: 1
            baseUrl: https://x.it
            data:
              fuori: { type: csv, path: ../fuori.csv }
              manca: { type: csv, path: data/manca.csv }
            steps:
              - { name: A, request: { method: GET, url: / } }
            """);
        File.WriteAllText(Path.Combine(Directory.GetParent(dir)!.FullName, "fuori.csv"), "x");
        var ex = Assert.Throws<RunPlanException>(() => Build(dir));
        Assert.True(ex.Errors.Count >= 2, ex.Message);
    }

    // ---------- run distribuita in memoria: controller + 2 agenti reali ----------

    private static async Task WaitFor(Func<bool> condition, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condizione non raggiunta");
            await Task.Delay(50);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public AgentRegistry Registry { get; } = new();
        public SqliteResultsRepository Repo { get; } = new(":memory:");
        public ControllerOptions Options { get; } = new() { Name = "ctrl", AllowAnonymousAgents = true, StartDelaySec = 1, ReadyTimeoutSec = 60 };
        public CancellationTokenSource Cts { get; } = new(TimeSpan.FromMinutes(3));
        private readonly List<(MemoryLink Link, Task Agent, Task Hub)> _agents = [];

        public Harness() { Options.WorkDirectory = TempDir(); }

        public async Task AddAgentAsync(string id, string name)
        {
            var hub = new ControllerHub(Registry, Microsoft.Extensions.Options.Options.Create(Options), NullLogger<ControllerHub>.Instance);
            var link = new MemoryLink();
            var session = new AgentSession(new AgentOptions { WorkDirectory = TempDir(), HeartbeatSec = 1, Name = name }, id, link, () => new FakeHttp(), NullLogger.Instance);
            var hubTask = hub.HandleAsync(new ChannelStreamReader<AgentMsg>(link.FromAgent), new ChannelStreamWriter<ControllerMsg>(link.ToAgent), Cts.Token);
            var agentTask = session.RunAsync(Cts.Token);
            _agents.Add((link, agentTask, hubTask));
            await WaitFor(() => Registry.Snapshot().Any(a => a.AgentId == id));
        }

        public RunOrchestrator Orchestrator() =>
            new(Microsoft.Extensions.Options.Options.Create(Options), Registry, Repo, NullLogger<RunOrchestrator>.Instance);

        public async ValueTask DisposeAsync()
        {
            foreach (var (link, _, _) in _agents) link.ToAgent.Writer.TryComplete();
            foreach (var (_, agent, hub) in _agents)
            {
                try { await Task.WhenAll(agent, hub).WaitAsync(TimeSpan.FromSeconds(20)); } catch (Exception) { }
            }
            Repo.Dispose();
        }
    }

    [Fact]
    public async Task Two_agents_run_a_distributed_load_test_and_results_reach_the_repository()
    {
        var dir = WriteProject();
        File.WriteAllText(Path.Combine(dir, "a.webtest.yaml"), "version: 1\nname: A\nbaseUrl: https://x.it\nsteps:\n  - { name: Home, request: { method: GET, url: / } }\n");
        File.WriteAllText(Path.Combine(dir, "run.loadtest.yaml"), """
            version: 1
            name: distribuito
            runSettings: { warmupSec: 0, iterations: 6, coolDownSec: 2, samplingSec: 1 }
            scenarios:
              - name: S
                thinkProfile: { mode: off }
                testMix: { model: users, tests: [ { test: a.webtest.yaml, percent: 100 } ] }
                loadPattern: { type: constant, users: 2 }
            rig:
              agents:
                - { name: ag1 }
                - { name: ag2 }
              deployment: [ { path: data/utenti.csv, partition: true } ]
            """);

        await using var h = new Harness();
        await h.AddAgentAsync("id1", "ag1");
        await h.AddAgentAsync("id2", "ag2");

        var orch = h.Orchestrator();
        var planned = await orch.PlanAsync("run1", new RunRequest { LoadTestPath = Path.Combine(dir, "run.loadtest.yaml") });
        Assert.Equal(2, planned.Assignments.Count);
        Assert.All(h.Registry.Snapshot(), a => Assert.Equal("run1", a.ReservedBy));

        var outcome = await orch.ExecuteAsync(planned, new RunControl());

        Assert.True(outcome.Status == "Completed", outcome.Status + ": " + outcome.Reason + " | " + string.Join(" | ", outcome.Warnings));
        Assert.Equal(6, outcome.TestsCompleted);
        Assert.Equal("Completed", h.Repo.GetRun("run1")!.Status);

        var samples = h.Repo.ReadSamples("run1");
        Assert.Equal(["id1", "id2"], samples.Select(s => s.AgentId).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal(6, samples.Where(s => s.Key.Kind == MetricKind.Request).Sum(s => s.Series.Count));
        Assert.All(h.Registry.Snapshot(), a => Assert.Null(a.ReservedBy));          // agenti liberati
        Assert.False(Directory.Exists(planned.WorkDir));                            // pacchetti temporanei rimossi
        Assert.Equal("Finished", planned.Progress.State);
    }

    [Fact]
    public async Task Controller_progress_exposes_test_counters_while_the_run_is_active()
    {
        var dir = WriteProject();
        File.WriteAllText(Path.Combine(dir, "a.webtest.yaml"), "version: 1\\nname: A\\nbaseUrl: https://x.it\\nsteps:\\n  - { name: Home, request: { method: GET, url: / } }\\n");
        File.WriteAllText(Path.Combine(dir, "run.loadtest.yaml"), """
            version: 1
            name: progress
            runSettings: { warmupSec: 0, durationMin: 1, coolDownSec: 1, samplingSec: 1 }
            scenarios:
              - name: S
                thinkProfile: { mode: off }
                testMix: { model: users, tests: [ { test: a.webtest.yaml, percent: 100 } ] }
                loadPattern: { type: constant, users: 1 }
            rig:
              agents:
                - { name: ag1 }
            """);

        await using var h = new Harness();
        await h.AddAgentAsync("id1", "ag1");
        var orch = h.Orchestrator();
        var planned = await orch.PlanAsync("run-progress", new RunRequest { LoadTestPath = Path.Combine(dir, "run.loadtest.yaml") });
        var control = new RunControl();
        var task = orch.ExecuteAsync(planned, control);

        await WaitFor(() => planned.Progress.CompletedTests > 0);
        Assert.Equal("Running", planned.Progress.State);
        Assert.True(planned.Progress.CompletedTests > 0);
        Assert.Equal(0, planned.Progress.FailedTests);

        control.Stop(StopMode.Abort, "test progress");
        var outcome = await task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("Cancelled", outcome.Status);
    }

    [Fact]
    public async Task Abort_request_cancels_the_run_and_frees_the_agents()
    {
        var dir = WriteProject();
        File.WriteAllText(Path.Combine(dir, "a.webtest.yaml"), "version: 1\nname: A\nbaseUrl: https://x.it\nsteps:\n  - { name: Home, request: { method: GET, url: / } }\n");
        File.WriteAllText(Path.Combine(dir, "run.loadtest.yaml"), """
            version: 1
            name: lungo
            runSettings: { warmupSec: 0, durationMin: 5, coolDownSec: 1, samplingSec: 1 }
            scenarios:
              - name: S
                thinkProfile: { mode: off }
                testMix: { model: users, tests: [ { test: a.webtest.yaml, percent: 100 } ] }
                loadPattern: { type: constant, users: 2 }
            """);

        await using var h = new Harness();
        await h.AddAgentAsync("id1", "ag1");
        var orch = h.Orchestrator();
        var planned = await orch.PlanAsync("run2", new RunRequest { LoadTestPath = Path.Combine(dir, "run.loadtest.yaml") });
        var control = new RunControl();
        var task = orch.ExecuteAsync(planned, control);

        await WaitFor(() => planned.Progress.State == "Running");
        await Task.Delay(3000);
        control.Stop(StopMode.Abort, "prova");
        var outcome = await task.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal("Cancelled", outcome.Status);
        Assert.Equal("Cancelled", h.Repo.GetRun("run2")!.Status);
        Assert.All(h.Registry.Snapshot(), a => Assert.Null(a.ReservedBy));
    }

    [Fact]
    public async Task Planning_fails_clearly_without_agents_and_releases_reservations()
    {
        var dir = WriteProject();
        await using var h = new Harness();
        var orch = h.Orchestrator();
        var ex = await Assert.ThrowsAsync<RunPlanException>(() => orch.PlanAsync("run3", new RunRequest { LoadTestPath = Path.Combine(dir, "run.loadtest.yaml") }));
        Assert.Contains("ag1", ex.Message);
    }
}
