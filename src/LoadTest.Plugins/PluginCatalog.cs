namespace LoadTest.Plugins;

/// <summary>
/// Insieme dei pacchetti disponibili, indicizzati per nome del plugin.yaml (PROPOSTA P5).
/// Riferimento a un plugin: "Pacchetto" oppure "Pacchetto/NomeTipo" (per pacchetti con più tipi dello stesso kind).
/// Pacchetti su disco: cartella `plugins/` accanto al .webtest.yaml/.loadtest.yaml, una sottocartella per pacchetto.
/// </summary>
public sealed class PluginCatalog
{
    private readonly Dictionary<string, PluginPackage> _byName = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<PluginPackage> Packages => _byName.Values;
    public bool Contains(string name) => _byName.ContainsKey(name);

    public PluginPackage Get(string name) =>
        _byName.TryGetValue(name, out var p) ? p : throw new PluginLoadException($"plugin '{name}' non trovato");

    public void Add(PluginPackage pkg)
    {
        if (pkg.Manifest.IsScript) throw new PluginLoadException($"'{pkg.Manifest.Name}': pacchetto .csx non compilato");
        if (!_byName.TryAdd(pkg.Manifest.Name, pkg)) throw new PluginLoadException($"plugin duplicato: '{pkg.Manifest.Name}'");
    }

    /// <param name="compileScript">Compila i pacchetti .csx nel processo che prepara l'esecuzione (Controller o VS Code Host). Senza, un .csx è un errore.</param>
    public static PluginCatalog FromDirectory(string root, Func<PluginPackage, PluginPackage>? compileScript = null)
    {
        var c = new PluginCatalog();
        if (!Directory.Exists(root)) return c;
        foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            if (!File.Exists(Path.Combine(dir, "plugin.yaml"))) continue;
            var pkg = PluginPackage.Open(dir);
            if (pkg.Manifest.IsScript)
                pkg = compileScript?.Invoke(pkg)
                    ?? throw new PluginLoadException($"'{pkg.Manifest.Name}': pacchetto .csx, la compilazione è del controller");
            c.Add(pkg);
        }
        return c;
    }

    /// <summary>Lato agente: i pacchetti del manifest di PrepareRun, già in cache (già compilati).</summary>
    /// <param name="trust">Se presente, ogni pacchetto deve rispettare la politica di firma (PROPOSTA P10 bis).</param>
    public static PluginCatalog FromCache(PluginCache cache, IEnumerable<string> hashes, PluginTrust? trust = null)
    {
        var c = new PluginCatalog();
        foreach (var h in hashes)
        {
            var pkg = cache.Open(h);
            trust?.Check(pkg);
            c.Add(pkg);
        }
        return c;
    }

    public static (string Package, string? Type) ParseReference(string reference)
    {
        var i = reference.IndexOf('/');
        return i < 0 ? (reference, null) : (reference[..i], reference[(i + 1)..]);
    }
}
