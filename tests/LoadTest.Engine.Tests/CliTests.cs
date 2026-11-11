using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LoadTest.Cli;
using LoadTest.Cli.Args;
using LoadTest.Cli.Ci;
using LoadTest.Cli.Remote;
using LoadTest.Controller;
using LoadTest.Controller.Api;
using LoadTest.Controller.Runs;
using Microsoft.Extensions.Logging.Abstractions;

namespace LoadTest.Engine.Tests;

public class CliTests
{
    private static string TempDir() => Directory.CreateTempSubdirectory("cli").FullName;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string Project(string? loadYaml = null)
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "a.webtest.yaml"),
            "version: 1\nname: A\nbaseUrl: https://x.it\nsteps:\n  - { name: Home, request: { method: GET, url: / } }\n");
        File.WriteAllText(Path.Combine(dir, "run.loadtest.yaml"), loadYaml ?? """
            version: 1
            name: ci
            runSettings: { warmupSec: 0, iterations: 6, coolDownSec: 1, samplingSec: 1 }
            scenarios:
              - name: S
                thinkProfile: { mode: off }
                testMix: { model: users, tests: [ { test: a.webtest.yaml, percent: 100 } ] }
                loadPattern: { type: constant, users: 2 }
            """);
        return dir;
    }

    private static async Task<(int Code, string Out, string Err)> Run(CliServices? services, CancellationToken stop, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await CliApp.RunAsync(args, new CliContext
        {
            Out = o, Err = e, Stop = stop, Services = services ?? new CliServices { HttpFactory = () => new FakeHttp() }
        });
        return (code, o.ToString(), e.ToString());
    }

    // ---------- argomenti ----------

    [Fact]
    public void ArgParser_reads_values_flags_positionals_and_rejects_typos()
    {
        OptionSpec[] spec = [new("db"), new("quiet", false), new("max-error-rate")];
        var p = ArgParser.Parse(["run", "x.yaml", "--db", "r.db", "--quiet", "--max-error-rate=1.5", "--db=altro.db"], spec);
        Assert.Equal(["run", "x.yaml"], p.Positional);
        Assert.Equal("altro.db", p.Get("db"));
        Assert.True(p.Flag("quiet"));
        Assert.Equal(1.5, p.GetDouble("max-error-rate"));

        var ex = Assert.Throws<CliException>(() => ArgParser.Parse(["--max-eror-rate", "1"], spec));
        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
        Assert.Throws<CliException>(() => ArgParser.Parse(["--db"], spec));                    // manca il valore
        Assert.Throws<CliException>(() => ArgParser.Parse(["--max-error-rate", "x"], spec).GetDouble("max-error-rate"));

        var raw = ArgParser.Parse(["--Agent:Name=a1"], spec, allowRaw: true);
        Assert.Equal(["--Agent:Name=a1"], raw.Raw);
    }

    // ---------- soglie e uscite ----------

    private static RunSummaryDto Summary(string status = "Completed", long count = 100, long failed = 0, double? p95 = 120, double? p99 = 300, long testsFailed = 0) => new(
        "r1", "prova", status, status == "Completed" ? null : "stopOnErrors", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 10, 50, testsFailed, failed,
        [],
        [
            new SeriesDto("S", "t", "Test", null, null, null, 50, 0, 0, 5, 10, 10, 10, 10, 10, 10, 0, 0, false, new()),
            new SeriesDto("S", "t", "Request", "Home", "https://x.it/", null, count, failed, count == 0 ? 0 : 100.0 * failed / count, count / 10.0, 50, 40, 90, p95, p99, 500, 1000, 5000, false, new())
        ]);

    [Fact]
    public void Thresholds_pass_and_fail_with_clear_details()
    {
        var gate = new GateOptions { MaxErrorRatePercent = 1, MaxP95Ms = 200, MaxP99Ms = 250, MinRequestsPerSecond = 5, MinRequests = 50 };
        var ok = ThresholdEvaluator.Evaluate(Summary(), gate);
        Assert.Equal(["run.completed", "requests.measured", "requests.min", "errors.rate", "throughput.rps", "latency.p95", "latency.p99"], ok.Select(c => c.Name));
        Assert.Contains(ok, c => c.Name == "latency.p99" && !c.Passed && c.Detail.Contains("Home"));     // 300 > 250
        Assert.All(ok.Where(c => c.Name != "latency.p99"), c => Assert.True(c.Passed, c.Name + ": " + c.Detail));

        Assert.False(ThresholdEvaluator.Evaluate(Summary("Aborted"), new GateOptions()).First().Passed);
        Assert.False(ThresholdEvaluator.Evaluate(Summary(count: 0), new GateOptions()).Single(c => c.Name == "requests.measured").Passed);   // run vuota non è verde
        Assert.False(ThresholdEvaluator.Evaluate(Summary(count: 100, failed: 5), new GateOptions { MaxErrorRatePercent = 1 }).Single(c => c.Name == "errors.rate").Passed);
        Assert.False(ThresholdEvaluator.Evaluate(Summary(p95: null), new GateOptions { MaxP95Ms = 1000 }).Single(c => c.Name == "latency.p95").Passed);   // niente tempi = rosso
        Assert.False(ThresholdEvaluator.Evaluate(Summary(testsFailed: 3), new GateOptions { MaxFailedTests = 2 }).Single(c => c.Name == "tests.failed").Passed);
    }

    [Fact]
    public void JUnit_lists_every_check_and_marks_failures()
    {
        var checks = ThresholdEvaluator.Evaluate(Summary(), new GateOptions { MaxP99Ms = 250 });
        var xml = System.Xml.Linq.XDocument.Parse(ResultOutput.JUnit(Summary(), checks));
        var suite = xml.Root!.Element("testsuite")!;
        Assert.Equal(checks.Count.ToString(), suite.Attribute("tests")!.Value);
        Assert.Equal("1", suite.Attribute("failures")!.Value);
        Assert.Single(suite.Descendants("failure"));
    }

    // ---------- validate ----------

    [Fact]
    public async Task Validate_accepts_good_projects_and_reports_every_problem_with_exit_2()
    {
        var good = Project();
        var (code, o, _) = await Run(null, default, "validate", Path.Combine(good, "run.loadtest.yaml"));
        Assert.Equal(0, code);
        Assert.StartsWith("OK ", o);

        File.Delete(Path.Combine(good, "a.webtest.yaml"));
        var (bad, _, err) = await Run(null, default, "validate", Path.Combine(good, "run.loadtest.yaml"), Path.Combine(good, "manca.loadtest.yaml"));
        Assert.Equal(ExitCodes.Usage, bad);
        Assert.Contains("a.webtest.yaml", err);
        Assert.Contains("manca.loadtest.yaml", err);
    }

    // ---------- run locale, report, CI ----------

    [Fact]
    public async Task Local_run_saves_results_and_writes_json_csv_junit_with_exit_0()
    {
        var dir = Project();
        var db = Path.Combine(dir, "out", "r.db");
        var json = Path.Combine(dir, "out", "r.json");
        var csv = Path.Combine(dir, "out", "r.csv");
        var junit = Path.Combine(dir, "out", "r.xml");

        var (code, _, err) = await Run(null, default, "run", Path.Combine(dir, "run.loadtest.yaml"), "--db", db, "--quiet",
            "--json", json, "--csv", csv, "--junit", junit, "--max-error-rate", "0", "--min-requests", "6");

        Assert.True(code == 0, err);
        using var doc = JsonDocument.Parse(File.ReadAllText(json));
        Assert.True(doc.RootElement.GetProperty("passed").GetBoolean());
        var summary = doc.RootElement.GetProperty("summary");
        Assert.Equal("Completed", summary.GetProperty("status").GetString());
        Assert.Equal(6, summary.GetProperty("testsCompleted").GetInt64());
        Assert.StartsWith("scenario,test,kind,", File.ReadAllText(csv));
        Assert.Contains("<testsuite", File.ReadAllText(junit));
        Assert.True(File.Exists(db));

        // lo stesso database si rilegge con `report`, anche con soglie diverse
        var (rc, ro, _) = await Run(null, default, "report", "--db", db, "--json", "-");
        Assert.Equal(0, rc);
        using var rdoc = JsonDocument.Parse(ro);
        Assert.Equal(6, rdoc.RootElement.GetProperty("summary").GetProperty("testsCompleted").GetInt64());
        var (rf, _, rerr) = await Run(null, default, "report", "--db", db, "--quiet", "--min-requests", "1000");
        Assert.Equal(ExitCodes.Failed, rf);
        Assert.Contains("requests.min", rerr);
        Assert.Equal(ExitCodes.Usage, (await Run(null, default, "report", "--db", db, "--run", "nope")).Code);
    }

    [Fact]
    public async Task Local_run_fails_the_gate_and_rejects_bad_input()
    {
        var dir = Project();
        var path = Path.Combine(dir, "run.loadtest.yaml");
        var db = Path.Combine(dir, "r.db");

        var (code, _, err) = await Run(null, default, "run", path, "--db", db, "--quiet", "--min-requests", "100");
        Assert.Equal(ExitCodes.Failed, code);
        Assert.Contains("requests.min", err);

        Assert.Equal(ExitCodes.Usage, (await Run(null, default, "run", Path.Combine(dir, "manca.yaml"))).Code);
        Assert.Equal(ExitCodes.Usage, (await Run(null, default, "run", path, "--max-eror-rate", "1")).Code);
        File.Delete(Path.Combine(dir, "a.webtest.yaml"));
        Assert.Equal(ExitCodes.Usage, (await Run(null, default, "run", path, "--db", db, "--quiet")).Code);   // web test mancante
        Assert.Equal(ExitCodes.Usage, (await Run(null, default, "boh")).Code);
    }

    // ---------- run su un controller (HTTP simulato) ----------

    private sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : request.Content.Headers.ContentType?.MediaType == "application/zip" ? "<zip>" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            Bodies.Add(body);
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(object o, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = JsonContent.Create(o, mediaType: null, options: Web) };

    private static CliServices Remote(StubHandler h) => new()
    {
        PollInterval = TimeSpan.FromMilliseconds(10),
        ClientFactory = (url, _) => new ControllerClient(new HttpClient(h) { BaseAddress = new Uri(url.TrimEnd('/') + "/") })
    };

    private static StubHandler Controller(string finalStatus = "Completed", int polls = 2)
    {
        var seen = 0;
        return new StubHandler((req, _) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Post && path == "/api/projects") return Json(new { projectId = "0123456789abcdef0123456789abcdef" });
            if (req.Method == HttpMethod.Post && path == "/api/runs") return Json(new { runId = "abc" }, HttpStatusCode.Accepted);
            if (path == "/api/runs/abc/stop") return new HttpResponseMessage(HttpStatusCode.Accepted);
            if (path == "/api/runs/abc/summary") return Json(Summary(finalStatus));
            if (path == "/api/runs/abc")
            {
                var done = ++seen >= polls;
                return Json(new RunSnapshot("abc", "ci", done ? "Finished" : "Running", DateTimeOffset.UtcNow, done ? finalStatus : null, null, 2, 10, 0, 0, 0, [], [], []));
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
    }

    [Fact]
    public async Task Remote_run_polls_until_done_and_applies_the_gate()
    {
        var dir = Project();
        var path = Path.Combine(dir, "run.loadtest.yaml");

        var ok = Controller();
        var (code, o, err) = await Run(Remote(ok), default, "run", path, "--controller", "http://localhost:5000", "--json", "-");
        Assert.True(code == 0, err);
        Assert.True(JsonDocument.Parse(o).RootElement.GetProperty("passed").GetBoolean());
        Assert.Contains("POST /api/runs", ok.Calls);
        Assert.DoesNotContain(ok.Calls, c => c.Contains("/api/projects"));          // host locale: nessun upload
        Assert.Contains(ok.Bodies, b => b.Contains(path.Replace("\\", "\\\\")) || b.Contains("run.loadtest.yaml"));

        var aborted = Controller("Aborted");
        Assert.Equal(ExitCodes.Failed, (await Run(Remote(aborted), default, "run", path, "--controller", "http://localhost:5000", "--quiet")).Code);
    }

    [Fact]
    public async Task Remote_run_uploads_the_project_for_non_local_controllers_and_maps_errors_to_exit_3()
    {
        var dir = Project();
        var path = Path.Combine(dir, "run.loadtest.yaml");

        var h = Controller();
        var (code, _, err) = await Run(Remote(h), default, "run", path, "--controller", "http://ci-controller:5000", "--quiet");
        Assert.True(code == 0, err);
        Assert.Contains("POST /api/projects", h.Calls);
        var start = h.Bodies[h.Calls.IndexOf("POST /api/runs")];
        Assert.Contains("\"projectId\":\"0123456789abcdef0123456789abcdef\"", start);
        Assert.Contains("\"loadTestPath\":\"run.loadtest.yaml\"", start);

        var rejected = new StubHandler((_, _) => Json(new { errors = new[] { "agente 'ag1' non connesso o occupato" } }, HttpStatusCode.BadRequest));
        var (c3, _, e3) = await Run(Remote(rejected), default, "run", path, "--controller", "http://localhost:5000", "--quiet");
        Assert.Equal(ExitCodes.Infrastructure, c3);
        Assert.Contains("ag1", e3);

        var unauthorized = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var (c4, _, e4) = await Run(Remote(unauthorized), default, "run", path, "--controller", "http://localhost:5000", "--quiet");
        Assert.Equal(ExitCodes.Infrastructure, c4);
        Assert.Contains("token", e4);
    }

    [Fact]
    public async Task Remote_run_sends_a_graceful_stop_on_ctrl_c_and_exits_130()
    {
        var path = Path.Combine(Project(), "run.loadtest.yaml");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var h = Controller();
        var (code, _, _) = await Run(Remote(h), cts.Token, "run", path, "--controller", "http://localhost:5000", "--quiet");
        Assert.Equal(ExitCodes.Interrupted, code);
        Assert.Contains(h.Calls, c => c.StartsWith("POST /api/runs/abc/stop?mode=graceful"));
    }

    [Fact]
    public async Task Agents_status_and_stop_commands_talk_to_the_controller()
    {
        var h = new StubHandler((req, _) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/agents") return Json(new[] { new AgentDto("id1", "ag1", "linux-x64", "Linux", 4, 8192, 0, false, 12, 0, null) });
            if (path == "/api/runs/abc") return Json(new RunSnapshot("abc", "ci", "Running", null, null, null, 3, 7, 1, 0, 0, [], ["w"], [new AgentSnapshot("ag1", "Running")]));
            if (path == "/api/runs/abc/stop") return new HttpResponseMessage(HttpStatusCode.Accepted);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var svc = Remote(h);

        var (c1, o1, _) = await Run(svc, default, "agents", "--controller", "http://c:5000");
        Assert.Equal(0, c1);
        Assert.Contains("ag1", o1);
        Assert.Contains("libero", o1);

        var (c2, o2, _) = await Run(svc, default, "status", "abc", "--controller", "http://c:5000");
        Assert.Equal(0, c2);
        Assert.Contains("richieste 7", o2);

        var (c3, _, _) = await Run(svc, default, "stop", "abc", "--abort", "--controller", "http://c:5000");
        Assert.Equal(0, c3);
        Assert.Contains(h.Calls, c => c.Contains("mode=abort"));
        Assert.Equal(ExitCodes.Usage, (await Run(svc, default, "stop", "zzz", "--controller", "http://c:5000")).Code);
        Assert.Equal(ExitCodes.Usage, (await Run(svc, default, "agents")).Code);     // manca il controller
    }

    // ---------- progetto inviato al controller ----------

    [Fact]
    public void ProjectZipper_skips_build_output_and_result_databases_and_enforces_the_limit()
    {
        var dir = Project();
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        Directory.CreateDirectory(Path.Combine(dir, "data"));
        File.WriteAllText(Path.Combine(dir, "bin", "x.dll"), "x");
        File.WriteAllText(Path.Combine(dir, "data", "u.csv"), "id\n1\n");
        File.WriteAllText(Path.Combine(dir, "r.db"), "x");

        var zip = ProjectZipper.Create(dir, 1 << 20);
        try
        {
            using var z = ZipFile.OpenRead(zip);
            var names = z.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("run.loadtest.yaml", names);
            Assert.Contains("data/u.csv", names);
            Assert.DoesNotContain(names, n => n.StartsWith("bin/") || n.EndsWith(".db"));
        }
        finally { File.Delete(zip); }

        Assert.Equal(ExitCodes.Usage, Assert.Throws<CliException>(() => ProjectZipper.Create(dir, 3)).ExitCode);
    }

    private static ProjectStore Store() =>
        new(Microsoft.Extensions.Options.Options.Create(new ControllerOptions { WorkDirectory = TempDir() }), NullLogger<ProjectStore>.Instance);

    private static MemoryStream ZipOf(params (string Name, string Content)[] files)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in files)
            {
                using var w = new StreamWriter(z.CreateEntry(name).Open());
                w.Write(content);
            }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public async Task ProjectStore_extracts_safely_and_resolves_only_inside_the_project()
    {
        var store = Store();
        var id = await store.SaveZipAsync(ZipOf(("run.loadtest.yaml", "version: 1"), ("data/u.csv", "x")), default);
        Assert.True(File.Exists(store.ResolveLoadTest(id, "run.loadtest.yaml")));

        Assert.Throws<InvalidDataException>(() => store.ResolveLoadTest(id, "../x.yaml"));
        Assert.Throws<InvalidDataException>(() => store.ResolveLoadTest(id, "manca.yaml"));
        Assert.Throws<InvalidDataException>(() => store.ResolveLoadTest("../../etc", "x"));
        Assert.Throws<InvalidDataException>(() => store.ResolveLoadTest(new string('a', 32), "x"));      // progetto inesistente

        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveZipAsync(ZipOf(("../evil.txt", "x")), default));   // zip slip
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveZipAsync(new MemoryStream("non è uno zip"u8.ToArray()), default));
    }
}
