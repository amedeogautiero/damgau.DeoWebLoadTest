using Grpc.Core;
using LoadTest.Controller.Agents;
using LoadTest.Plugins;
using LoadTest.Protocol;
using Microsoft.Extensions.Options;

namespace LoadTest.Controller.Grpc;

/// <summary>
/// Gestione di una connessione agente: Hello → Welcome (handshake) → registrazione → inoltro dei messaggi.
/// Separato dal servizio gRPC (che aggiunge solo l'autenticazione) per poterlo provare con stream in memoria.
/// </summary>
public sealed class ControllerHub(AgentRegistry registry, IOptions<ControllerOptions> options, ILogger<ControllerHub> log)
{
    public async Task HandleAsync(IAsyncStreamReader<AgentMsg> req, IServerStreamWriter<ControllerMsg> resp, CancellationToken ct)
    {
        if (!await req.MoveNext(ct) || req.Current.MCase != AgentMsg.MOneofCase.Hello)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "atteso Hello come primo messaggio"));

        var hello = req.Current.Hello;
        var welcome = Handshake.Evaluate(hello, options.Value.Name, PluginLoader.HostSdkVersion, DateTimeOffset.UtcNow);
        await resp.WriteAsync(new ControllerMsg { Welcome = welcome });
        if (!welcome.Accepted)
        {
            log.LogWarning("Agente '{Name}' ({Id}) rifiutato: {Reason}", hello.Name, hello.AgentId, welcome.RejectReason);
            return;
        }

        var conn = new AgentConnection(hello);
        registry.AddOrReplace(conn);
        log.LogInformation("Agente '{Name}' ({Id}, {Rid}) connesso", hello.Name, hello.AgentId, hello.Rid);

        var pump = Task.Run(() => conn.PumpAsync(resp, ct), CancellationToken.None);
        try
        {
            while (await req.MoveNext(ct))
            {
                try { conn.Dispatch(req.Current); }
                catch (Exception ex) { log.LogWarning(ex, "Errore nella gestione di un messaggio dell'agente {Name}", conn.Name); }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or RpcException)
        {
            log.LogInformation("Connessione dell'agente '{Name}' interrotta: {Message}", conn.Name, ex.Message);
        }
        finally
        {
            registry.Remove(conn);
            conn.Close();
            try { await pump; } catch (Exception) { }
            log.LogInformation("Agente '{Name}' disconnesso", conn.Name);
        }
    }
}
