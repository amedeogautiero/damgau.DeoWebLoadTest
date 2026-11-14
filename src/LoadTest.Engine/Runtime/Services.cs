using LoadTest.Engine.Model;
using LoadTest.Engine.Rules;
using LoadTest.Sdk;

namespace LoadTest.Engine.Runtime;

// ---- Punti di integrazione verso altre chat (PROPOSTA-CONTRATTO) ----

/// <summary>Misure grezze emesse dall'engine; la chat Load test le trasforma in istogrammi (chiave: scenario, test, pagina, richiesta, transazione).</summary>
public interface IMeasurementSink
{
    void RequestCompleted(RequestMeasurement m);
    void TransactionCompleted(TransactionMeasurement m);
    void TestCompleted(TestMeasurement m);
}

public sealed record RequestMeasurement(
    string Scenario, string Test, string Page, string Url, string? Transaction,
    long ElapsedMicros, long BytesSent, long BytesReceived, int StatusCode,
    bool Success, string? ErrorType, string? ErrorMessage);

public sealed record TransactionMeasurement(string Scenario, string Test, string Transaction, long ElapsedMicros, bool Success);
public sealed record TestMeasurement(string Scenario, string Test, long ElapsedMicros, bool Success);

public sealed class NullMeasurementSink : IMeasurementSink
{
    public static readonly NullMeasurementSink Instance = new();
    public void RequestCompleted(RequestMeasurement m) { }
    public void TransactionCompleted(TransactionMeasurement m) { }
    public void TestCompleted(TestMeasurement m) { }
}

/// <summary>Applica il think profile del load test (recorded | normal | off). Default: usa il valore registrato.</summary>
public interface IThinkTimeProvider
{
    Task DelayAsync(int recordedMs, WebTestContext ctx, CancellationToken ct);
}

public sealed class RecordedThinkTimeProvider : IThinkTimeProvider
{
    public Task DelayAsync(int recordedMs, WebTestContext ctx, CancellationToken ct) =>
        recordedMs > 0 ? Task.Delay(recordedMs, ct) : Task.CompletedTask;
}

/// <summary>Esegue i passi `code:` (.csx/DLL). Implementato dalla chat Plugin.</summary>
public interface ICodeStepHost
{
    Task RunAsync(string path, TestContext ctx, CancellationToken ct);
}

public sealed class EngineServices
{
    public required IHttpExecutor Http { get; init; }
    public IMeasurementSink Sink { get; init; } = NullMeasurementSink.Instance;
    /// <summary>Riceve eventi di dettaglio WebTest. Gli errori del consumer non interrompono l'esecuzione.</summary>
    public Action<ExecutionEvent>? OnExecutionEvent { get; init; }
    /// <summary>True only for an interactive WebTest debug run.</summary>
    public bool DebugMode { get; init; }
    /// <summary>Suspends at a YAML request breakpoint until the debugger resumes or cancellation is requested.</summary>
    public Func<ExecutionEvent, CancellationToken, Task>? WaitForContinue { get; init; }
    /// <summary>Gestisce gli errori dei callback di osservazione senza interrompere l'esecuzione.</summary>
    public Action<Exception>? OnPluginError { get; init; }
    public IThinkTimeProvider ThinkTime { get; init; } = new RecordedThinkTimeProvider();
    public RuleRegistry Rules { get; init; } = RuleRegistry.CreateDefault();
    public IReadOnlyList<IWebTestPlugin> WebTestPlugins { get; init; } = [];
    public IReadOnlyList<IRequestPlugin> RequestPlugins { get; init; } = [];
    public ICodeStepHost? CodeSteps { get; init; }
    /// <summary>Livello di validazione della run (runSettings.validationLevel). Le regole con livello superiore sono saltate.</summary>
    public ValidationLevel ValidationLevel { get; init; } = ValidationLevel.High;
    /// <summary>Caricamento dei test richiamati da `call:`; il percorso è già assoluto.</summary>
    public Func<string, WebTestDefinition> LoadTest { get; init; } = WebTestLoader.LoadFile;
    public int MaxCallDepth { get; init; } = 16;
}

public interface IHttpExecutor
{
    /// <summary>Invia la richiesta (variabili già sostituite, URL assoluto). Lancia TimeoutException allo scadere di TimeoutSec.</summary>
    Task<ResponseContext> SendAsync(RequestContext request, CancellationToken ct);
}

public sealed record WebTestResult(bool Success, IReadOnlyList<string> Failures, TimeSpan Elapsed);
