using LoadTest.Controller.Rig;
using LoadTest.Engine.LoadTesting;
using LoadTest.Protocol;
using LoadTest.Sdk;

namespace LoadTest.Controller.Runs;

/// <summary>
/// Profili di carico lato controller: calcola ogni secondo il totale di utenti per scenario (costante, a gradini, goal-based)
/// e lo ripartisce esattamente fra gli agenti (resto maggiore, tetti per agente). Manda UpdateLoad solo quando qualcosa cambia.
/// Gli scenari con profilo `custom` (plugin) restano guidati dall'agente.
/// </summary>
public sealed class LoadDriver
{
    private sealed class Entry
    {
        public required string Scenario { get; init; }
        public required ILoadProfile Profile { get; init; }
        public string? GoalCounter { get; init; }
        public int[]? Last { get; set; }
    }

    private readonly string _runId;
    private readonly List<Entry> _entries = [];
    private readonly IReadOnlyList<double> _shares;
    private readonly IReadOnlyList<int> _caps;
    private readonly Func<string, double?> _counters;

    public LoadDriver(string runId, LoadTestDefinition def, IReadOnlyList<double> shares, IReadOnlyList<int> maxUsers, Func<string, double?> counters)
    {
        _runId = runId; _shares = shares; _caps = maxUsers; _counters = counters;
        foreach (var sc in def.Scenarios)
        {
            if (sc.LoadPattern.Type == LoadPatternType.Custom) continue;
            _entries.Add(new Entry
            {
                Scenario = sc.Name,
                Profile = LoadProfileFactory.Create(sc.LoadPattern),
                GoalCounter = sc.LoadPattern is { Type: LoadPatternType.Goal, Goal: { } g } ? g.Counter : null
            });
        }
    }

    /// <returns>(indice dell'agente, UpdateLoad) per gli agenti i cui target sono cambiati.</returns>
    public IReadOnlyList<(int Agent, UpdateLoad Update)> Tick(TimeSpan elapsed, int activeUsers)
    {
        var updates = new UpdateLoad?[_shares.Count];
        foreach (var e in _entries)
        {
            double? goal = e.GoalCounter is { } name ? _counters(name) : null;
            var total = Math.Max(0, e.Profile.GetUserCount(elapsed, new LoadState(activeUsers, goal)));
            var split = IntegerSplit.Allocate(total, _shares, _caps);
            if (e.Last is not null && split.SequenceEqual(e.Last)) continue;
            e.Last = split;
            for (var i = 0; i < split.Length; i++)
            {
                updates[i] ??= new UpdateLoad { RunId = _runId };
                updates[i]!.Targets.Add(new ScenarioTarget { Scenario = e.Scenario, Users = split[i] });
            }
        }
        var list = new List<(int, UpdateLoad)>();
        for (var i = 0; i < updates.Length; i++)
            if (updates[i] is { } u) list.Add((i, u));
        return list;
    }
}
