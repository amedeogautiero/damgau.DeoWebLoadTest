using LoadTest.Agent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Agente: nessun Kestrel, solo connessione gRPC in uscita verso il controller (CONTRATTO sez. 1 e 7).
// Configurazione: sezione "Agent" di appsettings.json, variabili d'ambiente (Agent__ControllerUrl=...) o argomenti (--Agent:ControllerUrl=...).
var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.AddHostedService<AgentWorker>();
await builder.Build().RunAsync();
