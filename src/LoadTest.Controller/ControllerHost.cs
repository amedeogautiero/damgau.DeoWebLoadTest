using LoadTest.Controller.Agents;
using LoadTest.Controller.Api;
using LoadTest.Controller.Grpc;
using LoadTest.Controller.Runs;
using LoadTest.Protocol;
using LoadTest.Reports;
using LoadTest.Reports.Storage;
using Microsoft.Extensions.Options;

namespace LoadTest.Controller;

/// <summary>
/// Costruzione e avvio del controller: server gRPC per gli agenti + API JSON. Separato da Program.cs per poterlo lanciare dalla CLI
/// (`loadtest controller`). Configurazione: sezione "Controller" (appsettings.json, Controller__ListenUrl=..., --Controller:ListenUrl=...).
/// </summary>
public static class ControllerHost
{
    public static WebApplication Build(string[] args)
    {
        // radice del contenuto = cartella dell'eseguibile (un servizio Windows parte da System32)
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        builder.Services.Configure<ControllerOptions>(builder.Configuration.GetSection("Controller"));
        // come servizio: systemd (Type=notify) e Service Control Manager; da terminale non cambia nulla
        builder.Services.AddWindowsService(o => o.ServiceName = "LoadTestController");
        builder.Services.AddSystemd();
        var opt = builder.Configuration.GetSection("Controller").Get<ControllerOptions>() ?? new ControllerOptions();

        builder.WebHost.ConfigureKestrel(k => KestrelSetup.Configure(k, opt));
        builder.Services.AddGrpc(g =>
        {
            g.MaxReceiveMessageSize = ProtocolInfo.MaxMessageBytes;
            g.MaxSendMessageSize = ProtocolInfo.MaxMessageBytes;
        });
        builder.Services.AddSingleton<AgentRegistry>();
        builder.Services.AddSingleton<ControllerHub>();
        builder.Services.AddSingleton<ProjectStore>();
        builder.Services.AddSingleton<IResultsRepository>(sp =>
        {
            var path = sp.GetRequiredService<IOptions<ControllerOptions>>().Value.ResultsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            return new SqliteResultsRepository(path);
        });
        builder.Services.AddSingleton<RunOrchestrator>();
        builder.Services.AddSingleton<RunManager>();

        var app = builder.Build();

        // La dashboard è incorporata nell'assembly: non dipende da ContentRoot, WebRoot o directory di avvio.
        app.UseMiddleware<EmbeddedDashboardMiddleware>();
        app.MapGrpcService<AgentLinkService>().RequireHost("*:" + new Uri(opt.ListenUrl).Port);
        app.MapControllerApi("*:" + new Uri(opt.ApiUrl).Port);
        return app;
    }

    public static Task RunAsync(string[] args, CancellationToken ct = default) => ((IHost)Build(args)).RunAsync(ct);
}
