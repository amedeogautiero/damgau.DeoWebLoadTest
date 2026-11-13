using System;
using System.Threading.Tasks;
using LoadTest.Sdk;

public sealed class CalculateValue : ICodedTest
{
    public Task RunAsync(TestContext ctx)
    {
        bool attached = System.Diagnostics.Debugger.IsAttached;
        ctx.Set("Debugger.IsAttached", attached.ToString());

        int quantity = 10;
        decimal unitPrice = 19.90m;
        decimal total = quantity * unitPrice;

        ctx.Set("quantity", quantity.ToString());
        ctx.Set("unitPrice", unitPrice.ToString());
        ctx.Set("total", total.ToString());

        return Task.CompletedTask;
    }
}