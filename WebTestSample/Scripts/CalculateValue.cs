namespace WebTestSample.Scripts;

/// <summary>
/// Example Code Step implemented as an ordinary C# class.
/// </summary>
public sealed class CalculateValue : ICodedTest
{
    public Task RunAsync(TestContext ctx)
    {
        int quantity = 10;
        decimal unitPrice = 19.90m;
        decimal total = quantity * unitPrice;

        ctx.Set("quantity", quantity.ToString());
        ctx.Set("unitPrice", unitPrice.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ctx.Set("total", total.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return Task.CompletedTask;
    }
}
