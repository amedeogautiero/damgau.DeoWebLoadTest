using LoadTest.Sdk;

namespace LoadTest.Engine.LoadTesting;

/// <summary>Imposta lo User-Agent in base al browser assegnato all'utente dal browserMix (UserContext.Items["browser"]), se la richiesta non ne ha già uno.</summary>
public sealed class BrowserUserAgentPlugin : IRequestPlugin
{
    private static readonly Dictionary<string, string> Agents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36",
        ["firefox"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:132.0) Gecko/20100101 Firefox/132.0",
        ["edge"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0",
        ["safari"] = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15"
    };

    public void PreRequestDataBinding(RequestContext ctx) { }

    public void PreRequest(RequestContext ctx)
    {
        if (ctx.Headers.ContainsKey("User-Agent")) return;
        if (ctx.WebTest.User.Items.TryGetValue("browser", out var b) && b is string name && Agents.TryGetValue(name, out var ua))
            ctx.Headers["User-Agent"] = ua;
    }

    public void PostRequest(RequestContext ctx, ResponseContext resp) { }
    public void PostRequestDataBinding(RequestContext ctx, ResponseContext resp) { }
}
