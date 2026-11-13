using LoadTest.Sdk;

namespace LoadTest.WebTestSample.Scripts;

/// <summary>
/// Esempio di Code Step implementato come normale classe C#.
/// </summary>
public sealed class CalcolaValore : ICodedTest
{
    public Task RunAsync(TestContext ctx)
    {
        int quantita = 10;
        decimal prezzoUnitario = 19.90m;
        decimal totale = quantita * prezzoUnitario;

        ctx.Set("quantita", quantita.ToString());
        ctx.Set("prezzoUnitario", prezzoUnitario.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ctx.Set("totale", totale.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return Task.CompletedTask;
    }
}
