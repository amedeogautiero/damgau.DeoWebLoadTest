using LoadTest.Agent.Transfer;
using LoadTest.Plugins;

namespace LoadTest.Agent;

/// <summary>Cartelle dell'agente: cache per hash (file e pacchetti plugin), temporanei e cartelle di lavoro delle run.</summary>
public sealed class AgentCaches
{
    public ContentCache Files { get; }
    public PluginCache Plugins { get; }
    public string TempRoot { get; }
    public string RunsRoot { get; }

    public AgentCaches(string workDirectory)
    {
        var root = Path.GetFullPath(workDirectory);
        Files = new ContentCache(Path.Combine(root, "cache", "files"));
        Plugins = new PluginCache(Path.Combine(root, "cache", "plugins"));
        TempRoot = Path.Combine(root, "tmp");
        RunsRoot = Path.Combine(root, "runs");
        Directory.CreateDirectory(TempRoot);
        Directory.CreateDirectory(RunsRoot);
    }

    /// <summary>Svuota temporanei e cartelle di run (non le cache). Da chiamare all'avvio dell'agente.</summary>
    public void CleanTransient()
    {
        foreach (var root in new[] { TempRoot, RunsRoot })
            foreach (var d in Directory.EnumerateDirectories(root))
                try { Directory.Delete(d, true); } catch (IOException) { }
    }
}
