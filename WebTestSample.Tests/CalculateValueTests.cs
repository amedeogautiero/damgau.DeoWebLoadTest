using LoadTest.Sdk;
using WebTestSample.Scripts;

namespace WebTestSample.Tests;

public sealed class CalculateValueTests
{
    [Fact]
    public async Task RunAsync_calculates_total_and_updates_test_context()
    {
        var context = new TestContext
        {
            TestName = "WebTestSample",
            User = new UserContext { UserId = 1, ScenarioName = "Sample" }
        };

        var step = new CalculateValue();

        await step.RunAsync(context);

        Assert.Equal("10", context.Get("quantity"));
        Assert.Equal("19.90", context.Get("unitPrice"));
        Assert.Equal("199.00", context.Get("total"));
    }
}
