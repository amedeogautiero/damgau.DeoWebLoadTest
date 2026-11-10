using LoadTest.Engine.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LoadTest.Agent;

/// <summary>Mantiene la connessione col controller: riconnessione con backoff esponenziale; un rifiuto dell'handshake aspetta il massimo.</summary>
public sealed class AgentWorker(IOptions<AgentOptions> options, ILogger<AgentWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var o = options.Value;
        var agentId = AgentIdentity.Resolve(o);
        new AgentCaches(o.WorkDirectory).CleanTransient();   // resti di run interrotte (cartelle di lavoro, file temporanei)
        log.LogInformation("Agente {Agent} ({Rid}) verso {Url}", agentId, PlatformInfo.CurrentRid(), o.ControllerUrl);

        var min = TimeSpan.FromSeconds(Math.Max(1, o.ReconnectMinSec));
        var max = TimeSpan.FromSeconds(Math.Max(o.ReconnectMinSec, o.ReconnectMaxSec));
        var delay = min;

        while (!stop.IsCancellationRequested)
        {
            AgentSession? session = null;
            var startedAt = DateTime.UtcNow;
            try
            {
                await using var link = GrpcControllerLink.Connect(o, stop);
                session = new AgentSession(o, agentId, link, () => new HttpClientExecutor(), log);
                await session.RunAsync(stop);
                log.LogInformation("Connessione chiusa dal controller");
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (AgentRejectedException ex)
            {
                log.LogError("{Message}", ex.Message);
                delay = max;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Connessione al controller interrotta");
            }

            if (session?.Welcomed == true && DateTime.UtcNow - startedAt > TimeSpan.FromSeconds(30)) delay = min;
            try { await Task.Delay(delay, stop); }
            catch (OperationCanceledException) { break; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, max.TotalSeconds));
        }
    }
}
