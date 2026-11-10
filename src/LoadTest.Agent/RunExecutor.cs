using LoadTest.Engine.LoadTesting;
using LoadTest.Engine.Runtime;
using LoadTest.Metrics;
using LoadTest.Protocol;
using LoadTest.Sdk;

namespace LoadTest.Agent;

/// <summary>Run preparata: definizione caricata e validata, plugin caricati, profili originali messi da parte (vedi <see cref="LoadCoordinator"/>).</summary>
public sealed class PreparedRun : IDisposable
{
    public required RunWorkspace Workspace { get; init; }
    public required LoadTestDefinition Definition { get; init; }
    public required AgentPlugins Plugins { get; init; }
    public required IReadOnlyList<(ILoadTestPlugin Plugin, IReadOnlyDictionary<string, string> Args)> LoadTestPlugins { get; init; }
    public required IReadOnlyList<ILoadProfile?> InnerProfiles { get; init; }
    public required IReadOnlyList<int> InitialTotals { get; init; }

    public LoadCoordinator CreateCoordinator(double share, int maxUsers) =>
        new(Definition.Scenarios.Select(s => s.Name).ToList(), InnerProfiles, InitialTotals, share, maxUsers);

    public void Dispose() => Plugins.Dispose();
}

public sealed class RunInputs
{
    public required PreparedRun Run { get; init; }
    public required LoadCoordinator Coordinator { get; init; }
    public required StartRun Start { get; init; }
    public required string AgentId { get; init; }
    public required string AgentName { get; init; }
    public required Func<IHttpExecutor> HttpFactory { get; init; }
    public required Action<IntervalSnapshot> OnInterval { get; init; }
    public required Action<int> OnActiveUsers { get; init; }
    public required Action<Exception> OnPluginError { get; init; }
}

public static class RunExecutor
{
    public const string ProfilePrefix = "agent:";

    /// <summary>Carica e valida il load test e i plugin. Lancia LoadTestLoadException (tutti gli errori) o eccezioni dei plugin: la run non è accettata.</summary>
    public static PreparedRun Prepare(RunWorkspace ws)
    {
        var def = LoadTestLoader.LoadFile(ws.LoadTestPath);
        var plugins = new AgentPlugins(ws.PluginDirs);
        try
        {
            var loadPlugins = new List<(ILoadTestPlugin Plugin, IReadOnlyDictionary<string, string> Args)>();
            foreach (var pr in def.LoadTestPlugins)
                foreach (var pl in plugins.CreateLoadTestPlugins(pr.Name, pr.Params))
                    loadPlugins.Add((pl, pr.Params));

            var inner = new List<ILoadProfile?>();
            var initial = new List<int>();
            foreach (var sc in def.Scenarios)
            {
                var lp = sc.LoadPattern;
                switch (lp.Type)
                {
                    case LoadPatternType.Goal:
                        inner.Add(null);                       // lo guida il controller (UpdateLoad)
                        initial.Add(lp.Goal?.MinUsers ?? 0);
                        break;
                    case LoadPatternType.Custom:
                        inner.Add(plugins.CreateLoadProfile(lp.Plugin ?? ""));
                        initial.Add(0);
                        break;
                    default:
                        inner.Add(LoadProfileFactory.Create(lp));
                        initial.Add(0);
                        break;
                }
            }

            // Ogni scenario passa dal coordinatore: lo si ottiene dichiarandolo profilo `custom` (hook già presente: CustomProfileResolver).
            for (var i = 0; i < def.Scenarios.Count; i++)
                def.Scenarios[i].LoadPattern = new LoadPatternDef { Type = LoadPatternType.Custom, Plugin = ProfilePrefix + i };

            return new PreparedRun
            {
                Workspace = ws, Definition = def, Plugins = plugins,
                LoadTestPlugins = loadPlugins, InnerProfiles = inner, InitialTotals = initial
            };
        }
        catch
        {
            plugins.Dispose();
            throw;
        }
    }

    public static async Task<LoadTestResult> RunAsync(RunInputs x, CancellationToken ct)
    {
        var ws = x.Run.Workspace;
        var runner = new LoadTestRunner(x.Run.Definition, new LoadTestRunnerOptions
        {
            Services = new EngineServices { Http = new UnusedHttpExecutor() },   // ignorato dal runner: ogni utente usa HttpFactory
            HttpFactory = x.HttpFactory,
            BaseDirectory = Path.GetDirectoryName(ws.LoadTestPath)!,
            Plugins = x.Run.LoadTestPlugins,
            CustomProfileResolver = name =>
                name.StartsWith(ProfilePrefix, StringComparison.Ordinal) && int.TryParse(name[ProfilePrefix.Length..], out var i)
                    ? x.Coordinator.ProfileFor(i) : null,
            Seed = unchecked(x.Start.Seed * 397 + ws.PartitionIndex),
            RunId = ws.RunId,
            AgentId = x.AgentId,
            AgentName = x.AgentName,
            ControllerName = ws.ControllerName,
            OnInterval = x.OnInterval,
            OnPluginError = x.OnPluginError
        });
        runner.Events.Heartbeat += a => x.OnActiveUsers(a.ActiveUsers);
        return await runner.RunAsync(ct);
    }

    private sealed class UnusedHttpExecutor : IHttpExecutor
    {
        public Task<ResponseContext> SendAsync(RequestContext request, CancellationToken ct) =>
            throw new NotSupportedException("l'esecutore HTTP si crea per utente con HttpFactory");
    }
}
