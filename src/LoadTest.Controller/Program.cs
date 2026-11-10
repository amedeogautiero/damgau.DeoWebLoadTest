using LoadTest.Controller;
using LoadTest.Controller.Agents;
using LoadTest.Controller.Api;
using LoadTest.Controller.Grpc;
using LoadTest.Controller.Runs;
using LoadTest.Protocol;
using LoadTest.Reports;
using LoadTest.Reports.Storage;
using Microsoft.Extensions.Options;

// Controller: server gRPC per gli agenti + API JSON locale. Configurazione: sezione "Controller" (appsettings.json,
// variabili Controller__ListenUrl=..., argomenti --Controller:ListenUrl=...).
var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<ControllerOptions>(builder.Configuration.GetSection("Controller"));
var opt = builder.Configuration.GetSection("Controller").Get<ControllerOptions>() ?? new ControllerOptions();

builder.WebHost.ConfigureKestrel(k => KestrelSetup.Configure(k, opt));
builder.Services.AddGrpc(g =>
{
    g.MaxReceiveMessageSize = ProtocolInfo.MaxMessageBytes;
    g.MaxSendMessageSize = ProtocolInfo.MaxMessageBytes;
});
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<ControllerHub>();
builder.Services.AddSingleton<IResultsRepository>(sp =>
{
    var path = sp.GetRequiredService<IOptions<ControllerOptions>>().Value.ResultsPath;
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    return new SqliteResultsRepository(path);
});
builder.Services.AddSingleton<RunOrchestrator>();
builder.Services.AddSingleton<RunManager>();

var app = builder.Build();
var agentPort = new Uri(opt.ListenUrl).Port;
var apiPort = new Uri(opt.ApiUrl).Port;
app.MapGrpcService<AgentLinkService>().RequireHost("*:" + agentPort);
app.MapControllerApi("*:" + apiPort);
app.Run();
