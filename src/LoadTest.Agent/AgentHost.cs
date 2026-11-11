using LoadTest.Agent.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LoadTest.Agent;

/// <summary>
/// Avvio dell'agente come worker. Separato da Program.cs per poterlo lanciare anche dalla CLI (`loadtest agent`).
/// Configurazione: appsettings.json accanto all'eseguibile (lo scrive `install`), sezione "Agent"; variabili Agent__ControllerUrl=...;
/// argomenti --Agent:ControllerUrl=... Si integra con systemd (Type=notify) e con il Service Control Manager di Windows; con launchd
/// e da terminale gira come processo normale (SIGTERM/Ctrl+C fermano l'host). Nessun Kestrel sull'agente (CONTRATTO sez. 1).
/// </summary>
public static class AgentHost
{
    public static async Task RunAsync(string[] args, CancellationToken ct = default)
    {
        // radice del contenuto = cartella dell'eseguibile (un servizio Windows parte da System32)
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
        builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
        builder.Services.AddWindowsService(o => o.ServiceName = ServicePlanner.WindowsServiceName);
        builder.Services.AddSystemd();
        builder.Services.AddHostedService<AgentWorker>();
        using var host = builder.Build();
        await host.RunAsync(ct);
    }
}
