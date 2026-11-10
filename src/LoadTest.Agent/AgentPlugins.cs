using LoadTest.Plugins;
using LoadTest.Sdk;

namespace LoadTest.Agent;

/// <summary>
/// Plugin della run, caricati dalla cache. Riferimenti per nome (P5 della chat 5): "Pacchetto" oppure "Pacchetto/NomeTipo".
/// Oggi collega solo i kind loadTest e loadProfile; webTest/request/extraction/validation/dataSource/codedTest
/// dipendono da punti aperti dell'Engine (vedi PROPOSTE-CONTRATTO-chat6-parte2.md, G11).
/// </summary>
public sealed class AgentPlugins : IDisposable
{
    private readonly Dictionary<string, PluginPackage> _packages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LoadedPlugin> _loaded = new(StringComparer.OrdinalIgnoreCase);

    public AgentPlugins(IEnumerable<string> packageDirs)
    {
        foreach (var d in packageDirs)
        {
            var pkg = PluginPackage.Open(d);
            _packages[pkg.Manifest.Name] = pkg;
        }
    }

    public IReadOnlyCollection<string> PackageNames => _packages.Keys;

    public IReadOnlyList<ILoadTestPlugin> CreateLoadTestPlugins(string reference, IReadOnlyDictionary<string, string> args)
    {
        var (lp, type) = Resolve(reference);
        var list = lp.Create<ILoadTestPlugin>(PluginKind.LoadTest, args)
            .Where(x => type is null || x.GetType().Name == type).ToList();
        if (list.Count == 0) throw new InvalidOperationException($"plugin '{reference}': nessun tipo loadTest trovato (il pacchetto dichiara il kind?)");
        return list;
    }

    public ILoadProfile CreateLoadProfile(string reference)
    {
        var (lp, type) = Resolve(reference);
        var p = lp.Create<ILoadProfile>(PluginKind.LoadProfile)
            .FirstOrDefault(x => type is null || x.GetType().Name == type);
        return p ?? throw new InvalidOperationException($"plugin '{reference}': nessun tipo loadProfile trovato (il pacchetto dichiara il kind?)");
    }

    private (LoadedPlugin Plugin, string? Type) Resolve(string reference)
    {
        var slash = reference.IndexOf('/');
        var name = slash < 0 ? reference : reference[..slash];
        var type = slash < 0 ? null : reference[(slash + 1)..];
        if (!_packages.TryGetValue(name, out var pkg))
            throw new InvalidOperationException($"plugin '{reference}' non presente nel manifest della run");
        if (!_loaded.TryGetValue(pkg.Manifest.Name, out var lp))
            _loaded[pkg.Manifest.Name] = lp = PluginLoader.Load(pkg);
        return (lp, type);
    }

    public void Dispose()
    {
        foreach (var p in _loaded.Values) p.Dispose();
        _loaded.Clear();
    }
}
