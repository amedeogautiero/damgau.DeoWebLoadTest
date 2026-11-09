using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using Version = System.Version;

namespace LoadTest.Plugins;

public enum PluginKind { WebTest, Request, Extraction, Validation, DataSource, LoadTest, LoadProfile, CodedTest }

public sealed class PluginManifestException(string message) : Exception(message);

/// <summary>plugin.yaml (CONTRATTO sez. 6): name, version, sdk, kinds, entry, files.</summary>
public sealed class PluginManifest
{
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Sdk { get; init; } = "";
    public IReadOnlyList<PluginKind> Kinds { get; init; } = [];
    public string Entry { get; init; } = "";
    public IReadOnlyList<string> Files { get; init; } = [];

    public bool IsScript => Entry.EndsWith(".csx", StringComparison.OrdinalIgnoreCase);

    public static PluginManifest LoadFile(string path)
    {
        using var r = File.OpenText(path);
        return Load(r, path);
    }

    public static PluginManifest Load(TextReader reader, string origin = "<memoria>")
    {
        var ys = new YamlStream();
        try { ys.Load(reader); }
        catch (YamlException ex) { throw new PluginManifestException($"{origin}: YAML non valido ({ex.Message})"); }
        if (ys.Documents.Count == 0 || ys.Documents[0].RootNode is not YamlMappingNode root)
            throw new PluginManifestException($"{origin}: la radice deve essere una mappa");

        string? Str(string k) => root.Children.TryGetValue(new YamlScalarNode(k), out var v) && v is YamlScalarNode s ? s.Value : null;
        List<string> List(string k) => root.Children.TryGetValue(new YamlScalarNode(k), out var v) && v is YamlSequenceNode q
            ? q.Children.OfType<YamlScalarNode>().Select(x => x.Value ?? "").ToList() : [];

        var errors = new List<string>();
        var name = Str("name"); if (string.IsNullOrWhiteSpace(name)) errors.Add("'name' mancante");
        var version = Str("version"); if (!System.Version.TryParse(version, out _)) errors.Add("'version' mancante o non valida (es. 1.0.0)");
        var sdk = Str("sdk"); if (string.IsNullOrWhiteSpace(sdk)) errors.Add("'sdk' mancante"); else if (!SdkRange.TryParse(sdk, out _)) errors.Add($"'sdk' non valido: '{sdk}'");
        var entry = Str("entry");
        if (string.IsNullOrWhiteSpace(entry)) errors.Add("'entry' mancante");
        else if (!(entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || entry.EndsWith(".csx", StringComparison.OrdinalIgnoreCase)))
            errors.Add("'entry' deve essere .dll o .csx");

        var kinds = new List<PluginKind>();
        foreach (var k in List("kinds"))
            if (Enum.TryParse<PluginKind>(k, true, out var pk) && Enum.IsDefined(pk)) kinds.Add(pk);
            else errors.Add($"kind '{k}' non valido");
        if (kinds.Count == 0 && !errors.Any(e => e.StartsWith("kind"))) errors.Add("'kinds' vuoto o mancante");

        var files = List("files");
        foreach (var f in files.Append(entry ?? ""))
            if (f.Length > 0 && (Path.IsPathRooted(f) || f.Replace('\\', '/').Split('/').Contains("..")))
                errors.Add($"percorso non ammesso nel pacchetto: '{f}'");

        if (errors.Count > 0) throw new PluginManifestException($"{origin}: {string.Join("; ", errors)}");
        return new PluginManifest { Name = name!, Version = version!, Sdk = sdk!, Kinds = kinds, Entry = entry!, Files = files };
    }

    /// <summary>Vero se la versione dell'SDK dell'host rientra nell'intervallo dichiarato dal plugin.</summary>
    public bool IsCompatibleWith(Version hostSdk) => SdkRange.TryParse(Sdk, out var r) && r.Contains(hostSdk);
}

/// <summary>Intervallo tipo "&gt;=1.0 &lt;2.0" (comparatori separati da spazio, tutti da soddisfare).</summary>
internal sealed class SdkRange
{
    private readonly List<(string Op, Version V)> _parts = [];

    public static bool TryParse(string text, out SdkRange range)
    {
        range = new SdkRange();
        foreach (var tok in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var op = tok.StartsWith(">=") ? ">=" : tok.StartsWith("<=") ? "<=" : tok.StartsWith('>') ? ">" : tok.StartsWith('<') ? "<" : tok.StartsWith('=') ? "=" : "=";
            var num = tok.TrimStart('>', '<', '=');
            if (!Version.TryParse(num, out var v)) return false;
            range._parts.Add((op, v));
        }
        return range._parts.Count > 0;
    }

    public bool Contains(Version host)
    {
        // confronto su major.minor: le patch non rompono la compatibilità
        var h = new Version(host.Major, Math.Max(0, host.Minor));
        foreach (var (op, v) in _parts)
        {
            var c = h.CompareTo(new Version(v.Major, Math.Max(0, v.Minor)));
            var ok = op switch { ">=" => c >= 0, "<=" => c <= 0, ">" => c > 0, "<" => c < 0, _ => c == 0 };
            if (!ok) return false;
        }
        return true;
    }
}
