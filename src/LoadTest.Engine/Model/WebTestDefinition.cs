using LoadTest.Sdk;

namespace LoadTest.Engine.Model;

public sealed class WebTestDefinition
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? BaseUrl { get; set; }
    public DefaultsDef Defaults { get; set; } = new();
    public Dictionary<string, string> Context { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, DataSourceDef> Data { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, CredentialDef> Credentials { get; set; } = new(StringComparer.Ordinal);
    public ClientCertificateDef? ClientCertificate { get; set; }
    public ProxyDef? Proxy { get; set; }
    public List<PluginRef> WebTestPlugins { get; set; } = [];
    public List<PluginRef> RequestPlugins { get; set; } = [];
    public List<StepDef> Steps { get; set; } = [];
    public string? SourcePath { get; set; }
}

public sealed class DefaultsDef
{
    public int TimeoutSec { get; set; } = 60;
    public bool FollowRedirects { get; set; } = true;
    public string HttpVersion { get; set; } = "1.1";
    public string Encoding { get; set; } = "utf-8";
    public bool ParseDependentRequests { get; set; } = true;
    public string Cache { get; set; } = "simulated";
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DataSourceDef
{
    public string Type { get; set; } = "csv";
    public string? Path { get; set; }
    public DataAccessMode Access { get; set; } = DataAccessMode.Sequential;
    public string? Provider { get; set; }
    public string? Connection { get; set; }
    public string? Query { get; set; }
}

public sealed class CredentialDef { public string Type { get; set; } = "basic"; public string User { get; set; } = ""; public string Password { get; set; } = ""; }
public sealed class ClientCertificateDef { public string Path { get; set; } = ""; public string? Password { get; set; } }
public sealed class ProxyDef { public string Url { get; set; } = ""; public List<string> Bypass { get; set; } = []; }
public sealed class PluginRef { public string Name { get; set; } = ""; public Dictionary<string, string> Params { get; set; } = new(); }

public sealed class RuleDef
{
    public string Type { get; set; } = "";
    public string? Name { get; set; }                       // solo extract: nome del parametro di contesto
    public ValidationLevel Level { get; set; } = ValidationLevel.Low;
    public bool Required { get; set; }
    public Dictionary<string, string> Args { get; set; } = new(StringComparer.Ordinal);
}

public sealed class BodyDef
{
    public BodyKind Kind { get; set; }
    public string? Text { get; set; }
    public string? FilePath { get; set; }
    public List<KeyValuePair<string, string>> Form { get; set; } = [];
    public List<MultipartPart> Parts { get; set; } = [];
}

public abstract class StepDef { }

public sealed class RequestStep : StepDef
{
    public string Name { get; set; } = "";
    public string? Id { get; set; }
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = "";
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public BodyDef? Body { get; set; }
    public string DependentRequests { get; set; } = "auto";
    public int? ThinkTimeMs { get; set; }
    public int? ExpectedStatus { get; set; }
    public bool Breakpoint { get; set; }
    public List<RuleDef> Extract { get; set; } = [];
    public List<RuleDef> Validate { get; set; } = [];
}

public sealed class TransactionStep : StepDef { public string Name { get; set; } = ""; public List<StepDef> Steps { get; set; } = []; }
public sealed class IfStep : StepDef { public string Condition { get; set; } = ""; public List<StepDef> Then { get; set; } = []; public List<StepDef> Else { get; set; } = []; }
public sealed class LoopStep : StepDef { public string? For { get; set; } public List<StepDef> Steps { get; set; } = []; }
public sealed class CallStep : StepDef { public string Path { get; set; } = ""; public Dictionary<string, string> Params { get; set; } = new(StringComparer.Ordinal); }
public sealed class CodeStep : StepDef { public string Path { get; set; } = ""; }
