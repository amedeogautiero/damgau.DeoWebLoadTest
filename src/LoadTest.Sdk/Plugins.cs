// Interfacce ESATTAMENTE come da CONTRATTO.md v0.1, sezione 6.
namespace LoadTest.Sdk;

public interface IWebTestPlugin
{
    void PreWebTest(WebTestContext ctx);
    void PostWebTest(WebTestContext ctx);
    void PreTransaction(WebTestContext ctx, string name);
    void PostTransaction(WebTestContext ctx, string name);
}

public interface IRequestPlugin
{
    void PreRequestDataBinding(RequestContext ctx);
    void PreRequest(RequestContext ctx);
    void PostRequest(RequestContext ctx, ResponseContext resp);
    void PostRequestDataBinding(RequestContext ctx, ResponseContext resp);
}

public interface IExtractionRule { ExtractionResult Extract(RequestContext req, ResponseContext resp, IReadOnlyDictionary<string, string> args); }
public interface IValidationRule { ValidationResult Validate(RequestContext req, ResponseContext resp, IReadOnlyDictionary<string, string> args); }

public interface IDataSource : IAsyncDisposable
{
    Task OpenAsync(DataSourceConfig cfg, PartitionInfo partition);
    ValueTask<IReadOnlyDictionary<string, string>?> NextAsync(UserContext user);
}

public interface ILoadTestPlugin
{
    void Initialize(ILoadTestEvents events, IReadOnlyDictionary<string, string> args);
}

public interface ILoadTestEvents
{
    event Action<LoadTestInfo> LoadTestStarting;
    event Action<LoadTestInfo> LoadTestFinished;
    event Action WarmupComplete;
    event Action<TestEventArgs> TestStarting;
    event Action<TestEventArgs> TestFinished;
    event Action<TestSelectedArgs> TestSelected;      // permette di cambiare il test scelto
    event Action<HeartbeatArgs> Heartbeat;            // ogni secondo
    event Action<ThresholdArgs> ThresholdExceeded;
    event Action<string> Aborted;
}

public interface ILoadProfile { int GetUserCount(TimeSpan elapsed, LoadState state); }

public interface ICodedTest { Task RunAsync(TestContext ctx); }  // test in codice, equivalente ai coded web test
