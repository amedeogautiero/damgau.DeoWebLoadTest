namespace LoadTest.Engine.LoadTesting;

public sealed class MixUserState
{
    public int AssignedTest { get; set; } = -1;
    public int SequencePosition { get; set; }
}

/// <summary>
/// Scelta del test per i 4 modelli di mix (semantica = PROPOSTA, il contratto elenca solo i nomi):
/// tests: la quota di test AVVIATI segue le percentuali (scelta del test più "in difetto");
/// users: ogni utente riceve un test alla creazione e lo ripete; la quota di UTENTI per test segue le percentuali;
/// pace: scelta casuale pesata, il ritmo (testsPerUserPerHour) lo applica lo scheduler;
/// sequential: ogni utente percorre i test nell'ordine dichiarato, ciclando (le percentuali non contano).
/// </summary>
public sealed class TestMixSelector
{
    private readonly object _gate = new();
    private readonly MixModel _model;
    private readonly double[] _weights;
    private readonly long[] _started;
    private readonly long[] _active;
    private long _startedTotal, _activeTotal;
    private readonly Random _rng;

    public int Count => _weights.Length;

    public TestMixSelector(MixModel model, IReadOnlyList<double> percents, Random rng)
    {
        if (percents.Count == 0) throw new ArgumentException("nessun test nel mix", nameof(percents));
        _model = model;
        _weights = percents.Select(p => p / 100.0).ToArray();
        _started = new long[_weights.Length];
        _active = new long[_weights.Length];
        _rng = rng;
    }

    public void AssignUser(MixUserState u)
    {
        if (_model != MixModel.Users) return;
        lock (_gate)
        {
            var i = MostBehind(_active, _activeTotal);
            _active[i]++;
            _activeTotal++;
            u.AssignedTest = i;
        }
    }

    public void ReleaseUser(MixUserState u)
    {
        if (_model != MixModel.Users || u.AssignedTest < 0) return;
        lock (_gate)
        {
            _active[u.AssignedTest]--;
            _activeTotal--;
            u.AssignedTest = -1;
        }
    }

    public int NextTest(MixUserState u)
    {
        lock (_gate)
        {
            switch (_model)
            {
                case MixModel.Users:
                    return u.AssignedTest >= 0 ? u.AssignedTest : 0;
                case MixModel.Sequential:
                    return u.SequencePosition++ % _weights.Length;
                case MixModel.Pace:
                    return Weighted.Pick(_weights, _rng);
                default:
                    var i = MostBehind(_started, _startedTotal);
                    _started[i]++;
                    _startedTotal++;
                    return i;
            }
        }
    }

    private int MostBehind(long[] counts, long total)
    {
        var best = 0;
        var bestDeficit = double.NegativeInfinity;
        for (var i = 0; i < _weights.Length; i++)
        {
            var deficit = _weights[i] * (total + 1) - counts[i];
            if (deficit > bestDeficit) { bestDeficit = deficit; best = i; }
        }
        return best;
    }
}

internal static class Weighted
{
    public static int Pick(IReadOnlyList<double> weights, Random rng)
    {
        var total = 0.0;
        foreach (var w in weights) total += w;
        if (total <= 0) return 0;
        var x = rng.NextDouble() * total;
        var acc = 0.0;
        for (var i = 0; i < weights.Count; i++)
        {
            acc += weights[i];
            if (x < acc) return i;
        }
        return weights.Count - 1;
    }
}
