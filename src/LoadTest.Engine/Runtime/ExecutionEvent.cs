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
    public string? VariableName { get; init; }
    public string? VariableValue { get; init; }
    public bool? Success { get; init; }
    public IReadOnlyList<string>? Failures { get; init; }
}
