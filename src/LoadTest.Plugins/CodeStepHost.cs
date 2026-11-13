using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using LoadTest.Engine.Runtime;
using LoadTest.Sdk;

namespace LoadTest.Plugins;

/// <summary>
/// Esegue i passi code del WebTest.
/// Un riferimento senza estensione identifica un tipo ICodedTest già compilato
/// nell'assembly del progetto WebTest; un riferimento .csx mantiene il percorso legacy.
/// </summary>
public sealed class CodeStepHost(Func<string, PluginPackage> resolve) : ICodeStepHost, IDisposable
{
    private sealed class CompiledAssembly(string directory) : IDisposable
    {
        private sealed class WebTestLoadContext(string baseDirectory) : AssemblyLoadContext(isCollectible: true)
        {
            protected override Assembly? Load(AssemblyName name)
            {
                // L'SDK deve essere condiviso con l'Host per mantenere validi i cast a ICodedTest.
                if (name.Name == typeof(ICodedTest).Assembly.GetName().Name)
                    return null;

                var dependency = Path.Combine(baseDirectory, name.Name + ".dll");
                return File.Exists(dependency) ? LoadFromAssemblyPath(dependency) : null;
            }
        }

        private readonly WebTestLoadContext _context = new(Path.GetFullPath(directory));
        private readonly List<Assembly> _assemblies = [];

        public Type Resolve(string typeName)
        {
            if (_assemblies.Count == 0)
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var name = AssemblyName.GetAssemblyName(file);
                        if (name.Name == typeof(ICodedTest).Assembly.GetName().Name)
                            continue;

                        _assemblies.Add(_context.LoadFromAssemblyPath(Path.GetFullPath(file)));
                    }
                    catch (BadImageFormatException)
                    {
                        // Ignora eventuali DLL native/non .NET presenti nell'output.
                    }
                }
            }

            var matches = _assemblies
                .SelectMany(GetLoadableTypes)
                .Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true }
                    && typeof(ICodedTest).IsAssignableFrom(t)
                    && (string.Equals(t.Name, typeName, StringComparison.Ordinal)
                        || string.Equals(t.FullName, typeName, StringComparison.Ordinal)))
                .ToList();

            return matches.Count switch
            {
                1 => matches[0],
                0 => throw new PluginLoadException(
                    $"Nessun tipo pubblico ICodedTest chiamato '{typeName}' trovato negli assembly di '{directory}'."),
                _ => throw new PluginLoadException(
                    $"Il riferimento code '{typeName}' è ambiguo: specificare il nome completo del tipo.")
            };
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.OfType<Type>(); }
        }

        public void Dispose() => _context.Unload();
    }

    private readonly ConcurrentDictionary<string, Lazy<LoadedPlugin>> _loaded = new();
    private readonly ConcurrentDictionary<string, Lazy<CompiledAssembly>> _compiled = new();

    public async Task RunAsync(string path, TestContext ctx, CancellationToken ct)
    {
        // Nuova sintassi: code: NomeTipo (senza estensione). Il runner risolve il riferimento
        // rispetto al file YAML; la cartella padre di webtests è la radice del pacchetto compilato.
        if (string.IsNullOrEmpty(Path.GetExtension(path)))
        {
            var webTestsDirectory = Path.GetDirectoryName(path)
                ?? throw new PluginLoadException($"Percorso code non valido: '{path}'.");
            var packageDirectory = Directory.GetParent(webTestsDirectory)?.FullName
                ?? throw new PluginLoadException($"Impossibile individuare la cartella del pacchetto per '{path}'.");

            var key = Path.GetFullPath(packageDirectory);
            var compiled = _compiled.GetOrAdd(key,
                _ => new Lazy<CompiledAssembly>(() => new CompiledAssembly(key))).Value;
            var typeName = Path.GetFileName(path);
            var type = compiled.Resolve(typeName);
            if (Activator.CreateInstance(type) is not ICodedTest test)
                throw new PluginLoadException($"{type.FullName}: impossibile creare un'istanza ICodedTest.");

            ct.ThrowIfCancellationRequested();
            await test.RunAsync(ctx);
            return;
        }

        // Compatibilità legacy: i riferimenti .csx continuano a usare il compilatore plugin.
        var legacyKey = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
        var plugin = _loaded.GetOrAdd(legacyKey,
            _ => new Lazy<LoadedPlugin>(() => PluginLoader.Load(resolve(path)))).Value;

        var types = plugin.Types.Where(t => t.Kind == PluginKind.CodedTest).ToList();
        if (types.Count != 1)
            throw new PluginLoadException($"'{path}': serve esattamente un tipo ICodedTest, trovati {types.Count}");

        var legacyTest = (ICodedTest)PluginLoader.Instantiate(types[0].Type, null);
        ct.ThrowIfCancellationRequested();
        await legacyTest.RunAsync(ctx);
    }

    public void Dispose()
    {
        foreach (var item in _loaded.Values)
            if (item.IsValueCreated) item.Value.Dispose();
        _loaded.Clear();

        foreach (var item in _compiled.Values)
            if (item.IsValueCreated) item.Value.Dispose();
        _compiled.Clear();
    }
}
