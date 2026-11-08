using LoadTest.Sdk;

namespace LoadTest.Engine.LoadTesting;

/// <summary>Il tempo trascorso parte dall'avvio della run, warmup incluso (ASSUNZIONE: vedi note di consegna).</summary>
public sealed class ConstantProfile(int users) : ILoadProfile
{
    public int GetUserCount(TimeSpan elapsed, LoadState state) => users;
}

public sealed class StepProfile(int start, int max, int step, int stepSec) : ILoadProfile
{
    public int GetUserCount(TimeSpan elapsed, LoadState state)
    {
        var steps = elapsed <= TimeSpan.Zero ? 0 : (long)Math.Floor(elapsed.TotalSeconds / stepSec);
        return (int)Math.Min(max, start + steps * step);
    }
}

/// <summary>
/// Goal-based: ogni intervalSec confronta il contatore con target ± tolerance (tolleranza ASSOLUTA, nelle unità del contatore: PROPOSTA)
/// e sale/scende di `adjustment` utenti entro [minUsers, maxUsers]. Parte da minUsers. Senza valore del contatore non cambia nulla.
/// </summary>
public sealed class GoalProfile(GoalDef goal) : ILoadProfile
{
    private readonly object _gate = new();
    private int _users = goal.MinUsers;
    private TimeSpan _lastAdjust = TimeSpan.Zero;

    public int GetUserCount(TimeSpan elapsed, LoadState state)
    {
        lock (_gate)
        {
            if (elapsed - _lastAdjust < TimeSpan.FromSeconds(goal.IntervalSec)) return _users;
            _lastAdjust = elapsed;
            if (state.GoalCounterValue is not { } v) return _users;
            if (v < goal.Target - goal.Tolerance) _users = Math.Min(goal.MaxUsers, _users + goal.Adjustment);
            else if (v > goal.Target + goal.Tolerance) _users = Math.Max(goal.MinUsers, _users - goal.Adjustment);
            return _users;
        }
    }
}

public static class LoadProfileFactory
{
    /// <param name="customResolver">Risolve il nome di un profilo `custom` (fornito dalla chat Plugin).</param>
    public static ILoadProfile Create(LoadPatternDef p, Func<string, ILoadProfile?>? customResolver = null) => p.Type switch
    {
        LoadPatternType.Constant => new ConstantProfile(p.Users),
        LoadPatternType.Step => new StepProfile(p.Start, p.Max, p.Step, p.StepSec),
        LoadPatternType.Goal => new GoalProfile(p.Goal ?? throw new InvalidOperationException("goal mancante")),
        LoadPatternType.Custom => customResolver?.Invoke(p.Plugin ?? "")
            ?? throw new InvalidOperationException($"profilo di carico custom '{p.Plugin}' non disponibile"),
        _ => throw new NotSupportedException($"loadPattern {p.Type} non supportato")
    };
}
