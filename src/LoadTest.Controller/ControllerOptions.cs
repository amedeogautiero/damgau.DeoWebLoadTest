namespace LoadTest.Controller;

/// <summary>Configurazione del controller (sezione "Controller" di appsettings.json, variabili Controller__..., argomenti --Controller:...).</summary>
public sealed class ControllerOptions
{
    public string Name { get; set; } = Environment.MachineName;

    /// <summary>Endpoint gRPC degli agenti (HTTP/2). https richiede CertificatePath; http (h2c) solo per lo sviluppo.</summary>
    public string ListenUrl { get; set; } = "https://0.0.0.0:5001";
    /// <summary>Endpoint dell'API JSON (CLI, estensione VS Code). Di default solo locale.</summary>
    public string ApiUrl { get; set; } = "http://127.0.0.1:5000";
    /// <summary>Se non vuoto, l'API richiede "Authorization: Bearer ...". Se vuoto, l'API accetta solo richieste da loopback.</summary>
    public string ApiToken { get; set; } = "";

    public string CertificatePath { get; set; } = "";
    public string CertificatePassword { get; set; } = "";
    /// <summary>mTLS: Kestrel esige un certificato client; se TrustedClientCertificateThumbprints è vuoto vale la catena di fiducia di sistema.</summary>
    public bool RequireClientCertificate { get; set; }
    public List<string> TrustedClientCertificateThumbprints { get; set; } = [];

    /// <summary>Token ammessi per gli agenti ("authorization: Bearer ..."). Vuoto + AllowAnonymousAgents=false = nessun agente ammesso senza mTLS.</summary>
    public List<string> AgentTokens { get; set; } = [];
    /// <summary>Solo sviluppo: ammette agenti senza token né certificato.</summary>
    public bool AllowAnonymousAgents { get; set; }

    public string WorkDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "controller-data");
    /// <summary>Percorso del database dei risultati; vuoto = WorkDirectory/results.db.</summary>
    public string ResultsDatabase { get; set; } = "";

    public int ReadyTimeoutSec { get; set; } = 300;
    public int StartDelaySec { get; set; } = 5;
    public int ScriptTimeoutSec { get; set; } = 300;
    /// <summary>false: se un agente del rig non è connesso la run non parte. true: parte con gli agenti disponibili (avviso).</summary>
    public bool AllowPartialRig { get; set; }

    /// <summary>Progetti caricati dalla CLI (POST /api/projects): dimensione massima dello zip e giorni di permanenza.</summary>
    public long MaxProjectUploadBytes { get; set; } = 512L << 20;
    public int ProjectRetentionHours { get; set; } = 24;

    public string ResultsPath => string.IsNullOrWhiteSpace(ResultsDatabase) ? Path.Combine(WorkDirectory, "results.db") : ResultsDatabase;
}
