using LoadTest.Sdk;

namespace LoadTest.Agent;

/// <summary>
/// Decide quanti utenti avviare per ogni scenario sull'agente. Si aggancia al LoadTestRunner come profilo `custom`
/// (nessuna modifica all'Engine). Per scenario: se il controller ha mandato UpdateLoad vale quel target (già finale);
/// altrimenti il profilo originale (costante/gradini/plugin) × user_share, arrotondato. Il tetto max_users è sul TOTALE
/// dell'agente e, se superato, riduce gli scenari in proporzione (usa l'ultimo valore desiderato degli altri scenari: converge dal secondo tick).
/// </summary>
public sealed class LoadCoordinator
{
    private readonly object _gate = new();
    private readonly string[] _names;
    private readonly ILoadProfile?[] _inner;
    private readonly int[] _initial;
    private readonly int[] _desired;
    private readonly int[] _override;
    private readonly double _share;
    private readonly int _max;

    /// <param name="inner">Profilo originale per scenario; null = profilo goal (lo guida il controller con UpdateLoad).</param>
    /// <param name="initialTotals">Utenti totali prima del primo UpdateLoad per i profili senza `inner` (es. minUsers del goal).</param>
    public LoadCoordinator(IReadOnlyList<string> names, IReadOnlyList<ILoadProfile?> inner, IReadOnlyList<int> initialTotals, double share, int maxUsers)
    {
        _names = [.. names];
        _inner = [.. inner];
        _initial = [.. initialTotals];
        _desired = new int[_names.Length];
        _override = Enumerable.Repeat(-1, _names.Length).ToArray();
        _share = share;
        _max = maxUsers;
    }

    public ILoadProfile ProfileFor(int index) => new ScenarioProfile(this, index);

    public int GetUserCount(int index, TimeSpan elapsed, LoadState state)
    {
        lock (_gate)
        {
            int want;
            if (_override[index] >= 0) want = _override[index];
            else
            {
                var total = _inner[index]?.GetUserCount(elapsed, state) ?? _initial[index];
                want = (int)Math.Round(Math.Max(0, total) * _share, MidpointRounding.AwayFromZero);
            }
            _desired[index] = want;
            long sum = 0;
            foreach (var d in _desired) sum += d;
            return _max > 0 && sum > _max ? (int)Math.Floor(want * (double)_max / sum) : want;
        }
    }

    /// <summary>Target finale dell'agente per lo scenario (UpdateLoad). false se lo scenario non esiste.</summary>
    public bool SetTarget(string scenario, int users)
    {
        var i = Array.IndexOf(_names, scenario);
        if (i < 0) return false;
        lock (_gate) _override[i] = Math.Max(0, users);
        return true;
    }

    /// <summary>Stop graceful: nessun nuovo utente e quelli attivi si ritirano a fine test.</summary>
    public void ZeroAll()
    {
        lock (_gate)
            for (var i = 0; i < _override.Length; i++) _override[i] = 0;
    }

    private sealed class ScenarioProfile(LoadCoordinator owner, int index) : ILoadProfile
    {
        public int GetUserCount(TimeSpan elapsed, LoadState state) => owner.GetUserCount(index, elapsed, state);
    }
}
