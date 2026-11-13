using LoadTest.Sdk;
using LoadTest.WebTestSample.Scripts;

namespace LoadTest.WebTestSample;

public sealed class WebTestSampleTests
{
    [Fact]
    public async Task RunAsync_calcola_il_totale_e_aggiorna_il_contesto()
    {
        var context = new TestContext
        {
            TestName = "LoadTest.WebTestSample",
            User = new UserContext { UserId = 1, ScenarioName = "Sample" }
        };

        var step = new CalcolaValore();

        await step.RunAsync(context);

        Assert.Equal("10", context.Get("quantita"));
        Assert.Equal("19.90", context.Get("prezzoUnitario"));
        Assert.Equal("199.00", context.Get("totale"));
    }
}
