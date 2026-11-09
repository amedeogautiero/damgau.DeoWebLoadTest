using LoadTest.Engine.Model;

namespace LoadTest.Plugins;

/// <summary>Carica i pacchetti del catalogo una sola volta e istanzia i tipi per kind. Le istanze create sono nuove a ogni chiamata.</summary>
public sealed class PluginHost(PluginCatalog catalog) : IDisposable
{
    private readonly Dictionary<string, LoadedPlugin> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public PluginCatalog Catalog { get; } = catalog;

    public LoadedPlugin Load(string packageName)
    {
        lock (_gate)
        {
            if (_loaded.TryGetValue(packageName, out var lp)) return lp;
            lp = PluginLoader.Load(Catalog.Get(packageName));
            _loaded[packageName] = lp;
            return lp;
        }
    }

    /// <summary>Istanzia i tipi del kind indicato per il riferimento (`Pacchetto` o `Pacchetto/Tipo`), passando i `params`.</summary>
    public IReadOnlyList<T> Create<T>(PluginKind kind, PluginRef reference) where T : class
    {
        var (pkg, typeName) = PluginCatalog.ParseReference(reference.Name);
        var candidates = Load(pkg).Types.Where(t => t.Kind == kind).ToList();
        if (candidates.Count == 0) throw new PluginLoadException($"'{pkg}' non dichiara il kind '{kind}'");
        if (typeName is not null)
        {
            candidates = candidates.Where(t => t.Type.Name == typeName || t.Type.FullName == typeName).ToList();
            if (candidates.Count == 0) throw new PluginLoadException($"'{pkg}': nessun tipo '{typeName}' per il kind '{kind}'");
        }
        return candidates.Select(t => (T)PluginLoader.Instantiate(t.Type, reference.Params)).ToList();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var lp in _loaded.Values) lp.Dispose();
            _loaded.Clear();
        }
    }
}
