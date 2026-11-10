namespace LoadTest.Agent;

/// <summary>Configurazione dell'agente (sezione "Agent"). Tutti i valori hanno un default sensato per lo sviluppo locale.</summary>
public sealed class AgentOptions
{
    public string ControllerUrl { get; set; } = "https://localhost:5001";
    /// <summary>Vuoto = identificativo generato la prima volta e salvato in WorkDirectory/agent-id.</summary>
    public string AgentId { get; set; } = "";
    public string Name { get; set; } = Environment.MachineName;
    /// <summary>Capacità dichiarata in Hello (0 = non dichiarata).</summary>
    public int MaxUsers { get; set; }
    public string WorkDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "agent-data");

    /// <summary>Token inviato nel metadato gRPC "authorization: Bearer ..." (G3). Vuoto = nessun token.</summary>
    public string Token { get; set; } = "";
    /// <summary>Certificato client (.pfx) per mTLS. Vuoto = nessun certificato client.</summary>
    public string ClientCertificatePath { get; set; } = "";
    public string ClientCertificatePassword { get; set; } = "";
    /// <summary>Solo sviluppo: accetta qualsiasi certificato del server.</summary>
    public bool AllowInvalidServerCertificate { get; set; }

    public int HeartbeatSec { get; set; } = 5;
    public int ReconnectMinSec { get; set; } = 1;
    public int ReconnectMaxSec { get; set; } = 30;

    /// <summary>Limiti sul manifest ricevuto (il controller non è fidato ciecamente: disco e zip bomb).</summary>
    public long MaxEntryBytes { get; set; } = 1L << 30;
    public long MaxRunBytes { get; set; } = 4L << 30;

    /// <summary>Mantiene la cartella di lavoro della run dopo la fine (diagnostica).</summary>
    public bool KeepRunFolders { get; set; }
}

public sealed class AgentRejectedException(string reason) : Exception($"il controller ha rifiutato l'agente: {reason}");
