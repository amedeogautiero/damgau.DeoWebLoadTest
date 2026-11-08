// TIPI DI SUPPORTO NON DEFINITI DAL CONTRATTO v0.1 -> PROPOSTA-CONTRATTO (vedi elenco nella risposta della chat Engine).
namespace LoadTest.Sdk;

public enum ValidationLevel { Low = 0, Medium = 1, High = 2 }

public sealed class UserContext
{
    public int UserId { get; init; }
    public string UserContextId { get; init; } = "";      // $LoadTestUserContext
    public string AgentId { get; init; } = "";
    public string AgentName { get; init; } = "";
    public string ControllerName { get; init; } = "";
    public string ScenarioName { get; init; } = "";
    public bool IsNewUser { get; init; } = true;
    /// <summary>Stato che sopravvive fra un test e l'altro dello stesso utente virtuale.</summary>
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();
}

/// <summary>Contesto base, usato da ICodedTest.</summary>
public class TestContext
{
    public required string TestName { get; init; }
    public required UserContext User { get; init; }
    public int Iteration { get; init; }
    public IDictionary<string, string> Parameters { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();
    public CancellationToken CancellationToken { get; init; }

    public string? Get(string name) => Parameters.TryGetValue(name, out var v) ? v : null;
    public void Set(string name, string value) => Parameters[name] = value;
}

public sealed class WebTestContext : TestContext
{
    /// <summary>Riga corrente di ogni data source, per nome ({{utenti.username}}).</summary>
    public IDictionary<string, IReadOnlyDictionary<string, string>> DataRows { get; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
}

public enum BodyKind { None, Form, Raw, Json, Xml, File, Multipart }

public sealed record MultipartPart(string Name, string? Value, string? FilePath, string? FileName, string? ContentType);

public sealed class RequestBody
{
    public BodyKind Kind { get; set; }
    public string? Text { get; set; }                       // raw | json | xml
    public string? FilePath { get; set; }                   // file
    public List<KeyValuePair<string, string>> Form { get; set; } = [];
    public List<MultipartPart> Parts { get; set; } = [];
}

public sealed record ValidationBinding(IValidationRule Rule, IReadOnlyDictionary<string, string> Args, ValidationLevel Level = ValidationLevel.Low);
public sealed record ExtractionBinding(string ParameterName, IExtractionRule Rule, IReadOnlyDictionary<string, string> Args, bool Required = false);

public sealed class RequestContext
{
    public required WebTestContext WebTest { get; init; }
    public required string StepName { get; init; }
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = "";
    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public RequestBody? Body { get; set; }
    public int TimeoutSec { get; set; } = 60;
    public bool FollowRedirects { get; set; } = true;
    public string HttpVersion { get; set; } = "1.1";
    public string Encoding { get; set; } = "utf-8";

    public IList<ValidationBinding> ValidationRules { get; } = new List<ValidationBinding>();
    public IList<ExtractionBinding> ExtractionRules { get; } = new List<ExtractionBinding>();

    public void AddValidationRule(IValidationRule rule, IReadOnlyDictionary<string, string>? args = null, ValidationLevel level = ValidationLevel.Low)
        => ValidationRules.Add(new ValidationBinding(rule, args ?? new Dictionary<string, string>(), level));

    public void AddExtractionRule(string parameterName, IExtractionRule rule, IReadOnlyDictionary<string, string>? args = null, bool required = false)
        => ExtractionRules.Add(new ExtractionBinding(parameterName, rule, args ?? new Dictionary<string, string>(), required));
}

public sealed class ResponseContext
{
    public int StatusCode { get; init; }
    public string? ReasonPhrase { get; init; }
    /// <summary>Status di ogni hop (redirect inclusi); l'ultimo coincide con StatusCode.</summary>
    public IReadOnlyList<int> StatusHistory { get; init; } = [];
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; init; } = [];
    public string BodyText { get; init; } = "";
    public string FinalUrl { get; init; } = "";
    public long ElapsedMicros { get; init; }
    public long BytesSent { get; init; }

    public string? GetHeader(string name) => Headers.TryGetValue(name, out var v) && v.Count > 0 ? v[0] : null;
}
