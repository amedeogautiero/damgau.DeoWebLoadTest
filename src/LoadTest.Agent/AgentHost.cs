using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LoadTest.Agent;

/// <summary>
/// Avvio dell'agente come worker. Separato da Program.cs per poterlo lanciare anche dalla CLI (`loadtest agent`).
/// Configurazione: sezione "Agent" di appsettings.json, variabili Agent__ControllerUrl=..., argomenti --Agent:ControllerUrl=...
/// Nessun Kestrel sull'agente (CONTRATTO sez. 1).
/// </summary>
public static class AgentHost
{
    public static async Task RunAsync(string[] args, CancellationToken ct = default)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
        builder.Services.AddHostedService<AgentWorker>();
        using var host = builder.Build();
        await host.RunAsync(ct);
    }
}
