using System.Collections.Concurrent;
using LoadTest.Engine.Runtime;
using LoadTest.Sdk;

namespace LoadTest.Plugins;

/// <summary>
/// Implementa ICodeStepHost per i passi `code: percorso.csx`. Il file deve contenere esattamente un ICodedTest.
/// `resolve` trasforma il percorso in un pacchetto compilato (Controller o VS Code Host); l'Agent riceve pacchetti già compilati.
/// Limite noto (P3): ICodedTest.RunAsync non ha accesso a un esecutore HTTP, quindi il passo può solo leggere/scrivere il contesto.
/// </summary>
public sealed class CodeStepHost(Func<string, PluginPackage> resolve) : ICodeStepHost, IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<LoadedPlugin>> _loaded = new();

    public async Task RunAsync(string path, TestContext ctx, CancellationToken ct)
    {
        // la data di modifica entra nella chiave: un file cambiato viene ricompilato
        var key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
        var plugin = _loaded.GetOrAdd(key, _ => new Lazy<LoadedPlugin>(() => PluginLoader.Load(resolve(path)))).Value;

        var types = plugin.Types.Where(t => t.Kind == PluginKind.CodedTest).ToList();
        if (types.Count != 1)
            throw new PluginLoadException($"'{path}': serve esattamente un tipo ICodedTest, trovati {types.Count}");

        var test = (ICodedTest)PluginLoader.Instantiate(types[0].Type, null);
        ct.ThrowIfCancellationRequested();
        await test.RunAsync(ctx);
    }

    public void Dispose()
    {
        foreach (var l in _loaded.Values) if (l.IsValueCreated) l.Value.Dispose();
        _loaded.Clear();
    }
}
