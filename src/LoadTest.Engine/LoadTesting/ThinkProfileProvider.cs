using LoadTest.Engine.Runtime;
using LoadTest.Sdk;

namespace LoadTest.Engine.LoadTesting;

/// <summary>
/// Applica il thinkProfile dello scenario dell'utente.
/// recorded: tempo registrato * scale/100. off: nessuna attesa.
/// normal: N(avgMs, stdDevMs) troncata a 0, * scale/100, applicata solo ai passi che hanno un think time registrato (> 0).
/// Il significato di avgMs rispetto al valore registrato NON è definito dal contratto (vedi note di consegna).
/// Usa il Random dell'utente (UserContext.Items["rng"]) per essere riproducibile con il seed della run.
/// </summary>
public sealed class ThinkProfileProvider(IReadOnlyDictionary<string, ThinkProfileDef> byScenario) : IThinkTimeProvider
{
    public Task DelayAsync(int recordedMs, WebTestContext ctx, CancellationToken ct)
    {
        if (recordedMs <= 0) return Task.CompletedTask;
        byScenario.TryGetValue(ctx.User.ScenarioName, out var p);
        p ??= new ThinkProfileDef();

        double ms;
        switch (p.Mode)
        {
            case ThinkMode.Off: return Task.CompletedTask;
            case ThinkMode.Normal:
                var rng = ctx.User.Items.TryGetValue("rng", out var r) && r is Random ur ? ur : Random.Shared;
                ms = Math.Max(0, p.AvgMs + p.StdDevMs * Gaussian(rng));
                break;
            default:
                ms = recordedMs;
                break;
        }
        ms *= p.Scale / 100.0;
        return ms < 1 ? Task.CompletedTask : Task.Delay(TimeSpan.FromMilliseconds(ms), ct);
    }

    private static double Gaussian(Random r)
    {
        var u1 = 1.0 - r.NextDouble();
        var u2 = r.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
