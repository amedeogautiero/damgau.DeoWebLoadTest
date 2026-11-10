namespace LoadTest.Protocol;

/// <summary>Costanti del protocollo controller-agente (CONTRATTO sez. 7). I valori sono PROPOSTA-CONTRATTO (vedi G5).</summary>
public static class ProtocolInfo
{
    /// <summary>Versione del protocollo: il controller rifiuta gli agenti con un valore diverso.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>Dimensione dei PackageChunk.Data (prima della compressione di trasporto di gRPC, se attiva).</summary>
    public const int ChunkSizeBytes = 64 * 1024;

    /// <summary>Limite di messaggio da impostare su server e client (default gRPC: 4 MB in ricezione).</summary>
    public const int MaxMessageBytes = 16 * 1024 * 1024;

    /// <summary>Metadato gRPC del token di autenticazione ("Bearer &lt;token&gt;"); in alternativa si usa mTLS (G3).</summary>
    public const string AuthMetadataKey = "authorization";

    /// <summary>Sopra questa CPU% l'agente è "saturo" (CONTRATTO sez. 7).</summary>
    public const double SaturationCpuPercent = 80.0;
}
