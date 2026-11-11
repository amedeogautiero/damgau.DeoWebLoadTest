using LoadTest.Sdk;

namespace LoadTest.Plugins;

/// <summary>
/// Plugin della run, caricati dalla cache. Riferimenti per nome: "Pacchetto" oppure "Pacchetto/NomeTipo".
/// Gestisce i kind loadTest e loadProfile usati dall'esecuzione locale e dagli agenti.
/// </summary>
public sealed class AgentPlugins : IDisposable
{
    private readonly Dictionary<string, PluginPackage> _packages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LoadedPlugin> _loaded = new(StringComparer.OrdinalIgnoreCase);

    public AgentPlugins(IEnumerable<string> packageDirs)
    {
        foreach (var directory in packageDirs)
        {
            var package = PluginPackage.Open(directory);
            _packages[package.Manifest.Name] = package;
        }
    }

    public IReadOnlyCollection<string> PackageNames => _packages.Keys;

    public IReadOnlyList<ILoadTestPlugin> CreateLoadTestPlugins(
        string reference,
        IReadOnlyDictionary<string, string> args)
    {
        var (plugin, type) = Resolve(reference);
        var list = plugin.Create<ILoadTestPlugin>(PluginKind.LoadTest, args)
            .Where(instance => type is null || instance.GetType().Name == type)
            .ToList();

        if (list.Count == 0)
            throw new InvalidOperationException(
                $"plugin '{reference}': nessun tipo loadTest trovato (il pacchetto dichiara il kind?)");

        return list;
    }

    public ILoadProfile CreateLoadProfile(string reference)
    {
        var (plugin, type) = Resolve(reference);
        var profile = plugin.Create<ILoadProfile>(PluginKind.LoadProfile)
            .FirstOrDefault(instance => type is null || instance.GetType().Name == type);

        return profile ?? throw new InvalidOperationException(
            $"plugin '{reference}': nessun tipo loadProfile trovato (il pacchetto dichiara il kind?)");
    }

    private (LoadedPlugin Plugin, string? Type) Resolve(string reference)
    {
        var slash = reference.IndexOf('/');
        var packageName = slash < 0 ? reference : reference[..slash];
        var typeName = slash < 0 ? null : reference[(slash + 1)..];

        if (!_packages.TryGetValue(packageName, out var package))
            throw new InvalidOperationException($"plugin '{reference}' non presente nel manifest della run");

        if (!_loaded.TryGetValue(package.Manifest.Name, out var loaded))
            _loaded[package.Manifest.Name] = loaded = PluginLoader.Load(package);

        return (loaded, typeName);
    }

    public void Dispose()
    {
        foreach (var plugin in _loaded.Values)
            plugin.Dispose();

        _loaded.Clear();
    }
}
