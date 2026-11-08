using LoadTest.Engine.LoadTesting;
using LoadTest.Engine.Model;
using LoadTest.Engine.Runtime;
using LoadTest.Sdk;

namespace LoadTest.Engine.Tests;

public class LoadTestingTests
{
    // Esempio della sez. 5 del contratto (sezioni di altre chat comprese: devono essere ignorate senza errori).
    private const string ContractSample = """
        version: 1
        name: Carico giornaliero
        runSettings:
          warmupSec: 60
          durationMin: 30
          coolDownSec: 30
          samplingSec: 5
          connectionModel: pool
          connectionPoolSize: 100
          validationLevel: high
          timingDetail: statistics
          maxErrorDetails: 1000
          stopOnErrors: 500
          saveLogOnFailure: true
          sqlTracing: false
          ipSwitching: { enabled: false, addresses: [ ] }
        scenarios:
          - name: Utenti anonimi
            thinkProfile: { mode: normal, avgMs: 2000, stdDevMs: 500, scale: 100 }
            testMix:
              model: users
              tests:
                - { test: ricerca.webtest.yaml, percent: 70 }
                - { test: catalogo.webtest.yaml, percent: 30 }
            loadPattern:
              type: step
              start: 10
              max: 200
              step: 20
              stepSec: 60
            newUsersPercent: 40
            browserMix:
              - { browser: chrome, percent: 80 }
              - { browser: firefox, percent: 20 }
            networkMix:
              - { network: dsl, percent: 100 }
        counterSets: [ ]
        thresholds: [ ]
        monitoredMachines: [ ]
        rig:
          agents:
            - { name: agent-linux-1, weight: 2, attributes: { os: linux }, maxUsers: 500 }
          deployment: [ { path: data/utenti.csv, partition: true } ]
        plugins:
          loadTest: [ { name: MioLoadPlugin } ]
        """;

    [Fact]
    public void Loader_parses_contract_sample()
    {
        var def = LoadTestLoader.Load(new StringReader(ContractSample));
        Assert.Equal(30, def.RunSettings.DurationMin);
        Assert.Equal(ConnectionModel.Pool, def.RunSettings.ConnectionModel);
        var sc = Assert.Single(def.Scenarios);
        Assert.Equal(MixModel.Users, sc.TestMix.Model);
        Assert.Equal(LoadPatternType.Step, sc.LoadPattern.Type);
        Assert.Equal(40, sc.NewUsersPercent);
        Assert.Equal("MioLoadPlugin", Assert.Single(def.LoadTestPlugins).Name);
    }

    [Fact]
    public void Loader_reports_all_errors()
    {
        const string bad = """
            version: 1
            runSettings: { durationMin: 5, iterations: 10 }
            scenarios:
              - name: A
                testMix: { model: tests, tests: [ { test: a.webtest.yaml, percent: 60 } ] }
                loadPattern: { type: step, start: 5, max: 2, step: 0, stepSec: 10 }
            """;
        var ex = Assert.Throws<LoadTestLoadException>(() => LoadTestLoader.Load(new StringReader(bad)));
        Assert.True(ex.Errors.Count >= 3, string.Join(" | ", ex.Errors));
    }

    [Fact]
    public void Step_profile_ramps_and_caps()
    {
        var p = new StepProfile(10, 50, 20, 60);
        var s = new LoadState(0, null);
        Assert.Equal(10, p.GetUserCount(TimeSpan.Zero, s));
        Assert.Equal(10, p.GetUserCount(TimeSpan.FromSeconds(59), s));
        Assert.Equal(30, p.GetUserCount(TimeSpan.FromSeconds(120), s));
        Assert.Equal(50, p.GetUserCount(TimeSpan.FromHours(1), s));
    }

    [Fact]
    public void Goal_profile_adjusts_within_tolerance_and_bounds()
    {
        var p = new GoalProfile(new GoalDef { Counter = "cpu", Target = 70, Tolerance = 5, MinUsers = 10, MaxUsers = 30, Adjustment = 10, IntervalSec = 30 });
        Assert.Equal(10, p.GetUserCount(TimeSpan.Zero, new LoadState(10, 40)));
        Assert.Equal(10, p.GetUserCount(TimeSpan.FromSeconds(10), new LoadState(10, 40)));   // prima dell'intervallo
        Assert.Equal(20, p.GetUserCount(TimeSpan.FromSeconds(30), new LoadState(10, 40)));
        Assert.Equal(20, p.GetUserCount(TimeSpan.FromSeconds(60), new LoadState(20, 72)));   // dentro la tolleranza
        Assert.Equal(10, p.GetUserCount(TimeSpan.FromSeconds(90), new LoadState(20, 95)));
        Assert.Equal(10, p.GetUserCount(TimeSpan.FromSeconds(120), new LoadState(10, 95)));  // minimo
    }

    [Fact]
    public void Tests_mix_tracks_percentages()
    {
        var mix = new TestMixSelector(MixModel.Tests, [70, 30], new Random(1));
        var u = new MixUserState();
        var counts = new int[2];
        for (var i = 0; i < 1000; i++) counts[mix.NextTest(u)]++;
        Assert.InRange(counts[0], 699, 701);
        Assert.InRange(counts[1], 299, 301);
    }

    [Fact]
    public void Users_mix_assigns_users_by_percentage_and_releases()
    {
        var mix = new TestMixSelector(MixModel.Users, [70, 30], new Random(1));
        var users = Enumerable.Range(0, 10).Select(_ => new MixUserState()).ToList();
        foreach (var u in users) mix.AssignUser(u);
        Assert.Equal(7, users.Count(u => u.AssignedTest == 0));
        Assert.All(users, u => Assert.Equal(u.AssignedTest, mix.NextTest(u)));
        var released = users.First(u => u.AssignedTest == 0);
        mix.ReleaseUser(released);
        var replacement = new MixUserState();
        mix.AssignUser(replacement);
        Assert.Equal(0, replacement.AssignedTest);
    }

    [Fact]
    public void Sequential_mix_cycles_in_order()
    {
        var mix = new TestMixSelector(MixModel.Sequential, [0, 0, 0], new Random(1));
        var u = new MixUserState();
        Assert.Equal(new[] { 0, 1, 2, 0, 1 }, Enumerable.Range(0, 5).Select(_ => mix.NextTest(u)).ToArray());
    }

    [Fact]
    public async Task Runner_runs_fixed_iterations_and_collects_metrics()
    {
        const string loadYaml = """
            version: 1
            name: t
            runSettings: { warmupSec: 0, iterations: 6, coolDownSec: 2, samplingSec: 1 }
            scenarios:
              - name: S
                thinkProfile: { mode: off }
                testMix: { model: users, tests: [ { test: a.webtest.yaml, percent: 100 } ] }
                loadPattern: { type: constant, users: 2 }
            """;
        const string testYaml = """
            version: 1
            name: A
            baseUrl: https://x.it
            steps:
              - name: Home
                request: { method: GET, url: / }
            """;

        var clients = new System.Collections.Concurrent.ConcurrentBag<FakeHttp>();   // FakeHttp non è thread-safe: uno per utente
        var runner = new LoadTestRunner(LoadTestLoader.Load(new StringReader(loadYaml)), new LoadTestRunnerOptions
        {
            Services = new EngineServices { Http = new FakeHttp() },
            HttpFactory = () => { var h = new FakeHttp(); clients.Add(h); return h; },
            TestLoader = _ => WebTestLoader.Load(new StringReader(testYaml))
        });

        var result = await runner.RunAsync();

        Assert.Equal(LoadTestStatus.Completed, result.Status);
        Assert.Equal(6, result.TestsCompleted);
        Assert.Equal(0, result.TestsFailed);
        Assert.Equal(6, clients.Sum(c => c.Sent.Count));
        var requests = result.Intervals.SelectMany(i => i.Series).Where(kv => kv.Key.Kind == LoadTest.Metrics.MetricKind.Request).Sum(kv => kv.Value.Count);
        Assert.Equal(6, requests);
    }
}
