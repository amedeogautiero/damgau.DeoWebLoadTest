using System.Globalization;
using System.Text.Json;
using LoadTest.Sdk;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace LoadTest.Engine.Model;

public sealed class WebTestLoadException(string message) : Exception(message);

/// <summary>Legge un file *.webtest.yaml (formato: CONTRATTO.md sez. 4, versione 1.x).</summary>
public static class WebTestLoader
{
    public const int SupportedMajorVersion = 1;

    public static WebTestDefinition LoadFile(string path)
    {
        using var reader = File.OpenText(path);
        var def = Load(reader, path);
        def.SourcePath = Path.GetFullPath(path);
        return def;
    }

    public static WebTestDefinition Load(TextReader reader, string origin = "<memoria>")
    {
        var stream = new YamlStream();
        try { stream.Load(reader); }
        catch (YamlException ex) { throw new WebTestLoadException($"{origin}: YAML non valido ({ex.Message})"); }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new WebTestLoadException($"{origin}: la radice del documento deve essere una mappa");

        var def = new WebTestDefinition();

        var versionText = Str(root, "version") ?? throw new WebTestLoadException($"{origin}: campo 'version' mancante");
        var major = int.TryParse(versionText.Split('.')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mj) ? mj : -1;
        if (major != SupportedMajorVersion)
            throw new WebTestLoadException($"{origin}: versione formato '{versionText}' non supportata (attesa {SupportedMajorVersion}.x)");
        def.Version = major;

        def.Name = Str(root, "name") ?? "";
        def.Description = Str(root, "description");
        def.BaseUrl = Str(root, "baseUrl");

        if (Child(root, "defaults") is YamlMappingNode d)
        {
            def.Defaults.TimeoutSec = Int(d, "timeoutSec") ?? def.Defaults.TimeoutSec;
            def.Defaults.FollowRedirects = Bool(d, "followRedirects") ?? def.Defaults.FollowRedirects;
            def.Defaults.HttpVersion = Str(d, "httpVersion") ?? def.Defaults.HttpVersion;
            def.Defaults.Encoding = Str(d, "encoding") ?? def.Defaults.Encoding;
            def.Defaults.ParseDependentRequests = Bool(d, "parseDependentRequests") ?? def.Defaults.ParseDependentRequests;
            def.Defaults.Cache = Str(d, "cache") ?? def.Defaults.Cache;
            def.Defaults.Headers = StrMap(Child(d, "headers"), StringComparer.OrdinalIgnoreCase);
        }

        def.Context = StrMap(Child(root, "context"), StringComparer.Ordinal);

        if (Child(root, "data") is YamlMappingNode data)
            foreach (var (k, v) in data.Children)
                if (v is YamlMappingNode dm) def.Data[Scalar(k)] = ParseDataSource(dm, origin);

        if (Child(root, "credentials") is YamlMappingNode creds)
            foreach (var (k, v) in creds.Children)
                if (v is YamlMappingNode cm)
                    def.Credentials[Scalar(k)] = new CredentialDef { Type = Str(cm, "type") ?? "basic", User = Str(cm, "user") ?? "", Password = Str(cm, "password") ?? "" };

        if (Child(root, "clientCertificate") is YamlMappingNode cc)
            def.ClientCertificate = new ClientCertificateDef { Path = Str(cc, "path") ?? "", Password = Str(cc, "password") };

        if (Child(root, "proxy") is YamlMappingNode px)
            def.Proxy = new ProxyDef
            {
                Url = Str(px, "url") ?? "",
                Bypass = Child(px, "bypass") is YamlSequenceNode bp ? bp.Children.Select(Scalar).ToList() : []
            };

        if (Child(root, "plugins") is YamlMappingNode pl)
        {
            def.WebTestPlugins = ParsePluginRefs(Child(pl, "webTest"));
            def.RequestPlugins = ParsePluginRefs(Child(pl, "request"));
        }

        def.Steps = ParseSteps(Child(root, "steps"), origin);
        return def;
    }

    // ---------- passi ----------

    private static List<StepDef> ParseSteps(YamlNode? node, string origin)
    {
        var list = new List<StepDef>();
        if (node is null) return list;
        if (node is not YamlSequenceNode seq) throw new WebTestLoadException($"{origin}: 'steps' deve essere una lista");
        foreach (var item in seq.Children)
        {
            if (item is not YamlMappingNode m) throw new WebTestLoadException($"{origin}: ogni passo deve essere una mappa");
            list.Add(ParseStep(m, origin));
        }
        return list;
    }

    private static StepDef ParseStep(YamlMappingNode m, string origin)
    {
        if (Child(m, "transaction") is not null)
            return new TransactionStep { Name = Str(m, "transaction") ?? "", Steps = ParseSteps(Child(m, "steps"), origin) };

        if (Child(m, "if") is not null)
            return new IfStep
            {
                Condition = Str(m, "if") ?? "",
                Then = ParseSteps(Child(m, "then"), origin),
                Else = ParseSteps(Child(m, "else"), origin)
            };

        if (Child(m, "loop") is YamlMappingNode lm)
        {
            var forCount = Str(lm, "for");
            if (forCount is null)
                throw new WebTestLoadException($"{origin}: loop supporta solo 'for' (while/foreach non definiti dal contratto v0.1)");
            return new LoopStep { For = forCount, Steps = ParseSteps(Child(m, "steps"), origin) };
        }

        if (Child(m, "call") is not null)
            return new CallStep { Path = Str(m, "call") ?? "", Params = StrMap(Child(m, "params"), StringComparer.Ordinal) };

        if (Child(m, "code") is not null)
            return new CodeStep { Path = Str(m, "code") ?? "" };

        if (Child(m, "request") is YamlMappingNode rm)
        {
            var url = Str(rm, "url") ?? throw new WebTestLoadException($"{origin}: request senza 'url'");
            return new RequestStep
            {
                Name = Str(m, "name") ?? url,
                Id = Str(m, "id"),
                Method = (Str(rm, "method") ?? "GET").ToUpperInvariant(),
                Url = url,
                Headers = StrMap(Child(rm, "headers"), StringComparer.OrdinalIgnoreCase),
                Body = ParseBody(Child(rm, "body")),
                DependentRequests = Str(rm, "dependentRequests") ?? "auto",
                ThinkTimeMs = Int(m, "thinkTimeMs"),
                ExpectedStatus = Int(m, "expectedStatus"),
                Extract = ParseRules(Child(m, "extract"), isExtraction: true, origin),
                Validate = ParseRules(Child(m, "validate"), isExtraction: false, origin)
            };
        }

        throw new WebTestLoadException($"{origin}: passo non riconosciuto (chiavi: {string.Join(", ", m.Children.Keys.Select(Scalar))})");
    }

    private static BodyDef? ParseBody(YamlNode? n)
    {
        if (n is not YamlMappingNode b) return null;

        if (Child(b, "form") is YamlMappingNode f)
            return new BodyDef { Kind = BodyKind.Form, Form = f.Children.Select(kv => KeyValuePair.Create(Scalar(kv.Key), Scalar(kv.Value))).ToList() };
        if (Child(b, "raw") is { } raw) return new BodyDef { Kind = BodyKind.Raw, Text = Scalar(raw) };
        if (Child(b, "json") is { } json)
            return new BodyDef { Kind = BodyKind.Json, Text = json is YamlScalarNode js ? js.Value ?? "" : JsonSerializer.Serialize(ToObject(json)) };
        if (Child(b, "xml") is { } xml) return new BodyDef { Kind = BodyKind.Xml, Text = Scalar(xml) };
        if (Child(b, "file") is { } file) return new BodyDef { Kind = BodyKind.File, FilePath = Scalar(file) };
        if (Child(b, "multipart") is YamlSequenceNode mp)
        {
            var parts = new List<MultipartPart>();
            foreach (var p in mp.Children.OfType<YamlMappingNode>())
                parts.Add(new MultipartPart(Str(p, "name") ?? "", Str(p, "value"), Str(p, "file"), Str(p, "fileName"), Str(p, "contentType")));
            return new BodyDef { Kind = BodyKind.Multipart, Parts = parts };
        }
        return null;
    }

    private static List<RuleDef> ParseRules(YamlNode? n, bool isExtraction, string origin)
    {
        var list = new List<RuleDef>();
        if (n is not YamlSequenceNode seq) return list;
        foreach (var item in seq.Children.OfType<YamlMappingNode>())
        {
            var rule = new RuleDef
            {
                Type = Str(item, "type") ?? throw new WebTestLoadException($"{origin}: regola senza 'type'"),
                Name = Str(item, "name"),
                Required = Bool(item, "required") ?? false,
                Level = ParseLevel(Str(item, "level"), origin)
            };
            if (isExtraction && string.IsNullOrWhiteSpace(rule.Name))
                throw new WebTestLoadException($"{origin}: regola di estrazione '{rule.Type}' senza 'name'");
            foreach (var (k, v) in item.Children)
            {
                var key = Scalar(k);
                if (key is "type" or "name" or "level" or "required") continue;
                rule.Args[key] = Scalar(v);
            }
            list.Add(rule);
        }
        return list;
    }

    private static ValidationLevel ParseLevel(string? s, string origin) => s?.ToLowerInvariant() switch
    {
        null or "" or "low" => ValidationLevel.Low,
        "medium" => ValidationLevel.Medium,
        "high" => ValidationLevel.High,
        _ => throw new WebTestLoadException($"{origin}: validation level '{s}' non valido (low|medium|high)")
    };

    private static DataSourceDef ParseDataSource(YamlMappingNode m, string origin)
    {
        var ds = new DataSourceDef
        {
            Type = Str(m, "type") ?? "csv",
            Path = Str(m, "path"),
            Access = (Str(m, "access") ?? "sequential").ToLowerInvariant() switch
            {
                "sequential" => DataAccessMode.Sequential,
                "random" => DataAccessMode.Random,
                "unique" => DataAccessMode.Unique,
                var other => throw new WebTestLoadException($"{origin}: access '{other}' non valido")
            }
        };
        if (Child(m, "database") is YamlMappingNode db)
        {
            ds.Provider = Str(db, "provider");
            ds.Connection = Str(db, "connection");
            ds.Query = Str(db, "query");
        }
        return ds;
    }

    private static List<PluginRef> ParsePluginRefs(YamlNode? n)
    {
        var list = new List<PluginRef>();
        if (n is not YamlSequenceNode seq) return list;
        foreach (var p in seq.Children.OfType<YamlMappingNode>())
            list.Add(new PluginRef { Name = Str(p, "name") ?? "", Params = StrMap(Child(p, "params"), StringComparer.Ordinal) });
        return list;
    }

    // ---------- utilità YAML ----------

    private static YamlNode? Child(YamlMappingNode m, string key) =>
        m.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v : null;

    private static string? Str(YamlMappingNode m, string key) => Child(m, key) is YamlScalarNode s ? s.Value : null;

    private static int? Int(YamlMappingNode m, string key) =>
        int.TryParse(Str(m, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    private static bool? Bool(YamlMappingNode m, string key) =>
        bool.TryParse(Str(m, key), out var b) ? b : null;

    private static string Scalar(YamlNode n) =>
        n is YamlScalarNode s ? s.Value ?? "" : JsonSerializer.Serialize(ToObject(n));

    private static Dictionary<string, string> StrMap(YamlNode? n, StringComparer comparer)
    {
        var d = new Dictionary<string, string>(comparer);
        if (n is YamlMappingNode m)
            foreach (var (k, v) in m.Children) d[Scalar(k)] = Scalar(v);
        return d;
    }

    private static object? ToObject(YamlNode node) => node switch
    {
        YamlMappingNode m => m.Children.ToDictionary(kv => Scalar(kv.Key), kv => ToObject(kv.Value)),
        YamlSequenceNode s => s.Children.Select(ToObject).ToList(),
        YamlScalarNode sc => ScalarToObject(sc),
        _ => null
    };

    private static object? ScalarToObject(YamlScalarNode s)
    {
        var v = s.Value;
        if (v is null || s.Style != ScalarStyle.Plain) return v;
        if (v is "" or "null" or "~") return null;
        if (bool.TryParse(v, out var b)) return b;
        if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var dbl)) return dbl;
        return v;
    }
}
