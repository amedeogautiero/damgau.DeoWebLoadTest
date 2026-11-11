namespace LoadTest.Contracts;

/// <summary>
/// Stato complessivo di un'esecuzione di load test.
/// Il contratto è indipendente dal trasporto e dall'interfaccia che visualizza i dati.
/// </summary>
public enum RunProgressStatus
{
    Queued,
    Preparing,
    Running,
    Stopping,
    Completed,
    Failed,
    Cancelled
}

/// <summary>Tipo logico della serie di misurazione.</summary>
public enum RunProgressSeriesKind
{
    Test,
    Transaction,
    Request
}

/// <summary>
/// Fotografia immutabile dello stato di avanzamento di un run.
/// Tutti gli istanti sono UTC; le durate e i tempi di risposta sono espressi in millisecondi.
/// </summary>
public sealed record RunProgressSnapshot
{
    /// <summary>Identificativo del run, stabile per tutta la sua durata.</summary>
    public required string RunId { get; init; }

    /// <summary>Numero progressivo crescente all'interno dello stesso run.</summary>
    public long Sequence { get; init; }

    public RunProgressStatus Status { get; init; }

    /// <summary>Istante di avvio effettivo del run, null prima dell'avvio.</summary>
    public DateTimeOffset? StartedUtc { get; init; }

    /// <summary>Istante in cui è stato prodotto questo snapshot.</summary>
    public DateTimeOffset CapturedUtc { get; init; }

    public double ElapsedSeconds { get; init; }
    public int ActiveUsers { get; init; }

    public long CompletedTests { get; init; }
    public long FailedTests { get; init; }
    public long CompletedRequests { get; init; }
    public long FailedRequests { get; init; }

    /// <summary>Richieste completate al secondo, calcolate sull'intervallo di riferimento.</summary>
    public double RequestsPerSecond { get; init; }

    /// <summary>Serie aggregate per test, transazione e richiesta.</summary>
    public IReadOnlyList<RunProgressSeriesSnapshot> Series { get; init; }
        = Array.Empty<RunProgressSeriesSnapshot>();
}

/// <summary>
/// Dati aggregati di una singola serie. I percentili devono essere calcolati
/// fondendo gli istogrammi HdrHistogram, mai facendo la media dei percentili.
/// </summary>
public sealed record RunProgressSeriesSnapshot
{
    public RunProgressSeriesKind Kind { get; init; }

    public required string ScenarioName { get; init; }
    public required string TestName { get; init; }

    public string? PageName { get; init; }
    public string? RequestName { get; init; }
    public string? TransactionName { get; init; }

    public long Count { get; init; }
    public long Failed { get; init; }
    public double ErrorRatePercent { get; init; }

    /// <summary>Operazioni completate al secondo per questa serie.</summary>
    public double OperationsPerSecond { get; init; }

    public double P50Milliseconds { get; init; }
    public double P95Milliseconds { get; init; }
    public double P99Milliseconds { get; init; }
    public double MaxMilliseconds { get; init; }

    public long BytesSent { get; init; }
    public long BytesReceived { get; init; }
}
