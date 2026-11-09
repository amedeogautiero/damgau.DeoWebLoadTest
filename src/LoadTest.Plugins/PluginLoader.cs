using System.Reflection;
using System.Runtime.Loader;
using LoadTest.Sdk;

namespace LoadTest.Plugins;

public sealed class PluginLoadException(string message) : Exception(message);

/// <summary>Tipo concreto trovato nel plugin, con il kind che soddisfa.</summary>
public sealed record PluginType(PluginKind Kind, Type Type);

/// <summary>Plugin caricato: assembly in un contesto isolato (scaricabile) più i tipi per kind dichiarato.</summary>
public sealed class LoadedPlugin : IDisposable
{
    private readonly PluginLoadContext _ctx;
    public PluginManifest Manifest { get; }
    public IReadOnlyList<PluginType> Types { get; }

    internal LoadedPlugin(PluginManifest m, PluginLoadContext ctx, IReadOnlyList<PluginType> types) { Manifest = m; _ctx = ctx; Types = types; }

    public IEnumerable<T> Create<T>(PluginKind kind, IReadOnlyDictionary<string, string>? args = null) where T : class =>
        Types.Where(t => t.Kind == kind).Select(t => (T)PluginLoader.Instantiate(t.Type, args));

    public void Dispose() => _ctx.Unload();
}

internal sealed class PluginLoadContext(string dir) : AssemblyLoadContext(isCollectible: true)
{
    protected override Assembly? Load(AssemblyName name)
    {
        // L'SDK è sempre quello dell'host: un solo LoadTest.Sdk, altrimenti i cast alle interfacce falliscono.
        if (name.Name == "LoadTest.Sdk") return null;
        var candidate = Path.Combine(dir, name.Name + ".dll");
        return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
    }
}

public static class PluginLoader
{
    private static readonly Dictionary<PluginKind, Type> Interfaces = new()
    {
        [PluginKind.WebTest] = typeof(IWebTestPlugin),
        [PluginKind.Request] = typeof(IRequestPlugin),
        [PluginKind.Extraction] = typeof(IExtractionRule),
        [PluginKind.Validation] = typeof(IValidationRule),
        [PluginKind.DataSource] = typeof(IDataSource),
        [PluginKind.LoadTest] = typeof(ILoadTestPlugin),
        [PluginKind.LoadProfile] = typeof(ILoadProfile),
        [PluginKind.CodedTest] = typeof(ICodedTest),
    };

    /// <summary>Versione dell'SDK dell'host, presa dall'assembly (nessuna costante nel contratto: vedi nota S2).</summary>
    public static Version HostSdkVersion { get; } = typeof(IWebTestPlugin).Assembly.GetName().Version ?? new Version(1, 0);

    /// <summary>Carica un pacchetto con entry .dll. Per .csx vedi la fase 2 (compilazione sul controller).</summary>
    public static LoadedPlugin Load(PluginPackage pkg)
    {
        var m = pkg.Manifest;
        if (m.IsScript) throw new PluginLoadException($"'{m.Name}': l'entry .csx va compilato dal controller prima del caricamento (fase 2)");
        if (!m.IsCompatibleWith(HostSdkVersion))
            throw new PluginLoadException($"'{m.Name}': richiede SDK '{m.Sdk}', l'host ha {HostSdkVersion}");

        var ctx = new PluginLoadContext(pkg.Directory);
        try
        {
            var asm = ctx.LoadFromAssemblyPath(pkg.EntryPath);
            var types = new List<PluginType>();
            foreach (var kind in m.Kinds)
            {
                var iface = Interfaces[kind];
                foreach (var t in SafeTypes(asm).Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true } && iface.IsAssignableFrom(t)))
                    types.Add(new PluginType(kind, t));
                if (types.All(x => x.Kind != kind))
                    throw new PluginLoadException($"'{m.Name}': nessun tipo pubblico che implementi {iface.Name} (kind '{kind}') in {m.Entry}");
            }
            return new LoadedPlugin(m, ctx, types);
        }
        catch { ctx.Unload(); throw; }
    }

    private static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }

    /// <summary>
    /// Istanzia con costruttore senza parametri, oppure con uno che accetta solo IReadOnlyDictionary&lt;string,string&gt;
    /// (parametri `params:` del web test). PROPOSTA P1: il contratto non dice come i `params` arrivano al plugin.
    /// </summary>
    internal static object Instantiate(Type t, IReadOnlyDictionary<string, string>? args)
    {
        var withArgs = t.GetConstructor([typeof(IReadOnlyDictionary<string, string>)]);
        if (withArgs is not null) return withArgs.Invoke([args ?? new Dictionary<string, string>()]);
        if (t.GetConstructor(Type.EmptyTypes) is not null) return Activator.CreateInstance(t)!;
        throw new PluginLoadException($"{t.FullName}: serve un costruttore pubblico senza parametri (o con IReadOnlyDictionary<string,string>)");
    }
}
