using LoadTest.Cli.Commands;
using LoadTest.Cli.Remote;
using LoadTest.Engine.Http;
using LoadTest.Engine.Runtime;

namespace LoadTest.Cli;

/// <summary>Codici di uscita per la CI (G23).</summary>
public static class ExitCodes
{
    public const int Success = 0;
    /// <summary>La run non è andata a buon fine o una soglia (--max-error-rate, ...) non è rispettata.</summary>
    public const int Failed = 1;
    /// <summary>Argomenti o file di input non validi.</summary>
    public const int Usage = 2;
    /// <summary>Infrastruttura: controller non raggiungibile, nessun agente disponibile, run non avviabile.</summary>
    public const int Infrastructure = 3;
    public const int Interrupted = 130;
}

public sealed class CliException(int exitCode, string message) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>Dipendenze sostituibili nei test.</summary>
public sealed class CliServices
{
    public Func<IHttpExecutor> HttpFactory { get; init; } = () => new HttpClientExecutor();
    public Func<string, string?, ControllerClient> ClientFactory { get; init; } = (url, token) => ControllerClient.Create(url, token);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
}

public sealed class CliContext
{
    public required TextWriter Out { get; init; }
    public required TextWriter Err { get; init; }
    /// <summary>Prima richiesta di interruzione (Ctrl+C): stop ordinato.</summary>
    public CancellationToken Stop { get; init; }
    /// <summary>Seconda richiesta: interruzione immediata.</summary>
    public CancellationToken Abort { get; init; }
    public CliServices Services { get; init; } = new();
}

public static class CliApp
{
    public static async Task<int> RunAsync(string[] args, CliContext ctx)
    {
        try
        {
            if (args.Length == 0)
            {
                ctx.Err.WriteLine(Usage.Text);
                return ExitCodes.Usage;
            }
            var command = args[0].ToLowerInvariant();
            if (command is "help" or "--help" or "-h") { ctx.Out.WriteLine(Usage.Text); return ExitCodes.Success; }
            if (command is "version" or "--version") { ctx.Out.WriteLine(typeof(CliApp).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"); return ExitCodes.Success; }

            var rest = args[1..];
            if (rest.Any(a => a is "--help" or "-h")) { ctx.Out.WriteLine(Usage.For(command)); return ExitCodes.Success; }

            return command switch
            {
                "run" => await RunCommand.ExecuteAsync(rest, ctx),
                "validate" => ValidateCommand.Execute(rest, ctx),
                "report" => ReportCommand.Execute(rest, ctx),
                "agent" => await ServerCommands.AgentAsync(rest, ctx),
                "controller" => await ServerCommands.ControllerAsync(rest, ctx),
                "agents" => await RemoteCommands.AgentsAsync(rest, ctx),
                "status" => await RemoteCommands.StatusAsync(rest, ctx),
                "stop" => await RemoteCommands.StopAsync(rest, ctx),
                _ => throw new CliException(ExitCodes.Usage, $"comando sconosciuto '{args[0]}' (usa 'loadtest help')")
            };
        }
        catch (CliException ex)
        {
            ctx.Err.WriteLine("errore: " + ex.Message);
            return ex.ExitCode;
        }
        catch (OperationCanceledException) { return ExitCodes.Interrupted; }
        catch (Exception ex)
        {
            ctx.Err.WriteLine("errore imprevisto: " + ex.Message);
            return ExitCodes.Infrastructure;
        }
    }
}

internal static class Usage
{
    public const string Text = """
        loadtest — web load test (versione CLI)

        Uso: loadtest <comando> [opzioni]

        Comandi:
          run <file.loadtest.yaml>   esegue un load test (locale, oppure su un controller con --controller)
          validate <file...>         controlla load test, web test, file e plugin referenziati
          report --db <file>         riassunto, soglie ed esportazioni di una run già salvata
          controller                 avvia il controller (gRPC per gli agenti + API JSON)
          agent                      avvia un agente
          agents | status | stop     interrogano o fermano un controller (--controller URL)
          help <comando>             dettaglio di un comando

        Codici di uscita: 0 ok · 1 run non riuscita o soglie non rispettate · 2 argomenti/file non validi
                          3 infrastruttura (controller, agenti) · 130 interrotto con Ctrl+C
        """;

    private const string Gate = """
          Soglie (CI; senza soglie conta solo che la run sia completata e abbia misurato richieste):
            --max-error-rate <%>   --max-p95-ms <ms>   --max-p99-ms <ms>
            --min-rps <n>          --min-requests <n>  --max-failed-tests <n>
          Uscite:
            --json <file|->  --csv <file>  --junit <file>  --quiet  --top <n>
        """;

    public static string For(string command) => command switch
    {
        "run" => """
            loadtest run <file.loadtest.yaml> [opzioni]

              Senza --controller: esegue qui (nessun agente, la sezione `rig` è ignorata) e salva i risultati in SQLite.
              --db <file>          database dei risultati (default: ./loadtest-results.db)
              --name <nome>        nome della run   --seed <n>   seme dei numeri casuali

              Con --controller <url>: esegue sugli agenti del controller; Ctrl+C = stop ordinato, due volte = abort.
              --token <t>          token dell'API (oppure variabile LOADTEST_TOKEN)
              --upload | --no-upload   invia la cartella del load test al controller (default: sì se l'host non è locale)
              --remote-path <p>    percorso del file visto dal controller (senza upload)
              --max-upload-mb <n>  limite dello zip inviato (default 256)

            """ + Gate,
        "validate" => "loadtest validate <file.loadtest.yaml...>\n\n  Controlla load test, rig, web test (anche `call:`), data source, file di body e plugin. Esce con 2 se qualcosa non va.",
        "report" => "loadtest report --db <file> [--run <id>]\n\n  Senza --run usa l'ultima run del database.\n\n" + Gate,
        
        "controller" => """
            loadtest controller [opzioni]
              --listen <url>   endpoint gRPC degli agenti (default https://0.0.0.0:5001)   
              --api <url>   API JSON (default http://127.0.0.1:5000)
              --cert <pfx> 
              --cert-password <p>   certificato del server (obbligatorio per https)
              --agent-token <t>   token ammesso per gli agenti (ripetibile)   
              --allow-anonymous-agents   solo sviluppo
              --require-client-cert   mTLS   
              --trusted-client-cert <thumbprint> (ripetibile)
              --api-token <t>   
              --work-dir <dir>   
              --results-db <file>   
              --allow-partial-rig
              Qualsiasi chiave come --Controller:Chiave=valore.
            """,
        
        "agent" => """
            loadtest agent [opzioni]
              --controller <url> 
              --token <t> 
              --name <nome> 
              --agent-id <id> 
              --work-dir <dir> 
              --max-users <n>
              --client-cert <pfx> 
              --client-cert-password <p> 
              --insecure (solo sviluppo)
              Qualsiasi chiave come --Agent:Chiave=valore.
            """,

        "agents" => "loadtest agents --controller <url> [--token <t>]\n\n  Elenca gli agenti connessi. Variabili: LOADTEST_CONTROLLER, LOADTEST_TOKEN.",
        "status" => "loadtest status <runId> --controller <url> [--token <t>]",
        "stop" => "loadtest stop <runId> --controller <url> [--token <t>] [--abort] [--reason <testo>]",
        _ => Text
    };
}
