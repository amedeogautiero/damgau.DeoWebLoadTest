using LoadTest.Controller.Agents;
using LoadTest.Controller.Runs;
using LoadTest.Engine.LoadTesting;

namespace LoadTest.Controller.Rig;

public sealed class AgentAssignment
{
    public required AgentConnection Connection { get; init; }
    public required int Index { get; init; }
    public required int Count { get; init; }
    /// <summary>Quota di carico 0..1 (peso / somma dei pesi).</summary>
    public required double Share { get; init; }
    /// <summary>Tetto di utenti (0 = nessun tetto): minimo fra rig.maxUsers e il max_users dichiarato dall'agente.</summary>
    public required int MaxUsers { get; init; }
    /// <summary>Quota delle `iterations` del load test (0 = modalità a durata).</summary>
    public int Iterations { get; init; }
    public int UserIdBase { get; init; }
}

public sealed record RigPlan(IReadOnlyList<AgentAssignment> Assignments, IReadOnlyList<string> Warnings);

/// <summary>Sceglie gli agenti della run e ne calcola quote, tetti e iterazioni (CONTRATTO sez. 5, `rig`).</summary>
public static class RigPlanner
{
    public const int UserIdStride = 1_000_000;

    public static RigPlan Plan(LoadTestDefinition def, RigDefinition rig, IReadOnlyList<AgentConnection> free, bool allowPartial)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var chosen = new List<(AgentConnection Conn, double Weight, int Max)>();

        if (rig.Agents.Count == 0)
        {
            foreach (var c in free.OrderBy(a => a.Name, StringComparer.Ordinal)) chosen.Add((c, 1, c.Hello.MaxUsers));
        }
        else
        {
            foreach (var r in rig.Agents)
            {
                var c = free.FirstOrDefault(a => string.Equals(a.Name, r.Name, StringComparison.OrdinalIgnoreCase))
                        ?? free.FirstOrDefault(a => string.Equals(a.AgentId, r.Name, StringComparison.Ordinal));
                string? problem = null;
                if (c is null) problem = $"agente '{r.Name}' non connesso o occupato";
                else if (chosen.Any(x => x.Conn == c)) problem = $"agente '{r.Name}' indicato più volte nel rig";
                else if (!AttributesMatch(c, r.Attributes, warnings, out var why)) problem = $"agente '{r.Name}': {why}";

                if (problem is not null)
                {
                    if (allowPartial) warnings.Add(problem + " (ignorato)");
                    else errors.Add(problem);
                    continue;
                }
                chosen.Add((c!, r.Weight, MinPositive(r.MaxUsers ?? 0, c!.Hello.MaxUsers)));
            }
        }
        if (chosen.Count == 0) errors.Add("nessun agente disponibile per la run");

        int[]? iterations = null;
        if (errors.Count == 0 && def.RunSettings.Iterations is { } total)
        {
            if (total < chosen.Count) errors.Add($"iterations ({total}) minori del numero di agenti ({chosen.Count})");
            else
            {
                iterations = IntegerSplit.LargestRemainder(total, chosen.Select(c => c.Weight).ToList());
                for (var i = 0; i < iterations.Length; i++)
                    if (iterations[i] < 1) errors.Add($"iterations troppo poche per il peso dell'agente '{chosen[i].Conn.Name}'");
            }
        }
        if (errors.Count > 0) throw new RunPlanException(errors);

        var weightSum = chosen.Sum(c => c.Weight);
        var list = new List<AgentAssignment>();
        for (var i = 0; i < chosen.Count; i++)
            list.Add(new AgentAssignment
            {
                Connection = chosen[i].Conn, Index = i, Count = chosen.Count,
                Share = chosen[i].Weight / weightSum, MaxUsers = chosen[i].Max,
                Iterations = iterations?[i] ?? 0, UserIdBase = i * UserIdStride
            });
        return new RigPlan(list, warnings);
    }

    private static int MinPositive(int a, int b) => a <= 0 ? Math.Max(b, 0) : b <= 0 ? a : Math.Min(a, b);

    /// <summary>Attributi riconosciuti: os, rid, arch, name. Gli altri non sono noti all'agente (Hello non li porta): ignorati con avviso.</summary>
    private static bool AttributesMatch(AgentConnection c, Dictionary<string, string> attrs, List<string> warnings, out string why)
    {
        why = "";
        var rid = c.Hello.Rid;
        foreach (var (k, v) in attrs)
        {
            bool ok;
            switch (k.ToLowerInvariant())
            {
                case "os": ok = rid.StartsWith(v + "-", StringComparison.OrdinalIgnoreCase) || c.Hello.Os.Contains(v, StringComparison.OrdinalIgnoreCase); break;
                case "rid": ok = string.Equals(rid, v, StringComparison.OrdinalIgnoreCase); break;
                case "arch": ok = rid.EndsWith("-" + v, StringComparison.OrdinalIgnoreCase); break;
                case "name": ok = string.Equals(c.Name, v, StringComparison.OrdinalIgnoreCase); break;
                default:
                    warnings.Add($"agente '{c.Name}': attributo '{k}' non verificabile (non è nell'Hello), ignorato");
                    continue;
            }
            if (!ok)
            {
                why = $"attributo {k}={v} non corrisponde (rid {rid}, os {c.Hello.Os})";
                return false;
            }
        }
        return true;
    }
}
