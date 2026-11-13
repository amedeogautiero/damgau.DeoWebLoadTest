namespace LoadTest.Engine.Runtime;

/// <summary>Evento di dettaglio prodotto durante l'esecuzione di un WebTest.</summary>
public sealed record ExecutionEvent
{
    public required string EventType { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? ScenarioName { get; init; }
    public string? TestName { get; init; }
    public int? UserId { get; init; }
    public int? Iteration { get; init; }
    public string? RequestName { get; init; }
    /// <summary>Categoria dello step visualizzato nel debugger: Request, Code, Extractor o Validator.</summary>
    public string? StepType { get; init; }
    /// <summary>Nome leggibile dello step, della regola o del codice eseguito.</summary>
    public string? StepName { get; init; }
    /// <summary>Stato dello step: Started, Succeeded o Failed.</summary>
    public string? StepStatus { get; init; }
    /// <summary>Dettagli strutturati specifici dello step.</summary>
    public object? StepDetails { get; init; }
    public string? Method { get; init; }
    public string? Url { get; init; }
    public IReadOnlyDictionary<string, string>? RequestHeaders { get; init; }
    public object? RequestBody { get; init; }
    public int? StatusCode { get; init; }
    public string? ReasonPhrase { get; init; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? ResponseHeaders { get; init; }
    public string? ResponseBody { get; init; }
    public long? ElapsedMicros { get; init; }
    public string? ErrorType { get; init; }
    public string? ErrorMessage { get; init; }
    /// <summary>Catena completa delle eccezioni, inclusa l'inner exception.</summary>
    public string? ExceptionDetails { get; init; }
    /// <summary>Nodo request originale del passo WebTest, prima del binding delle variabili.</summary>
    public object? RequestDefinition { get; init; }
    /// <summary>Snapshot del contesto all'avvio del WebTest, senza cronologia/provenienza delle variabili.</summary>
    public object? ContextSnapshot { get; init; }
    public string? VariableName { get; init; }
    public string? VariableValue { get; init; }
    public bool? Success { get; init; }
    public IReadOnlyList<string>? Failures { get; init; }
}
