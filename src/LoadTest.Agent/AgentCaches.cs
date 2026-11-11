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

    /// <summary>Segna una voce come usata ora (l'ultima modifica del file/cartella è il suo "ultimo uso").</summary>
    public void Touch(string sha256, bool isPlugin)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (isPlugin)
            {
                var dir = Path.Combine(Plugins.Root, sha256.ToLowerInvariant());
                if (Directory.Exists(dir)) Directory.SetLastWriteTimeUtc(dir, now);
            }
            else if (Files.Contains(sha256)) File.SetLastWriteTimeUtc(Files.PathFor(sha256), now);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public readonly record struct EvictionResult(int Removed, long FreedBytes);

    /// <summary>
    /// Elimina voci della cache non protette: prima quelle più vecchie di <paramref name="maxAge"/>, poi le meno recenti finché il totale
    /// scende sotto <paramref name="maxBytes"/>. Da chiamare solo con l'agente libero (i pacchetti plugin di una run in corso si caricano dalla cache).
    /// </summary>
    public EvictionResult Evict(ISet<string> protectedShas, long maxBytes, TimeSpan maxAge)
    {
        var entries = new List<(string Sha, string Path, bool IsDir, DateTime LastUse, long Size)>();
        foreach (var f in Directory.EnumerateFiles(Files.Root, "*.bin"))
            entries.Add((System.IO.Path.GetFileNameWithoutExtension(f).ToLowerInvariant(), f, false, File.GetLastWriteTimeUtc(f), new FileInfo(f).Length));
        if (Directory.Exists(Plugins.Root))
            foreach (var d in Directory.EnumerateDirectories(Plugins.Root))
            {
                var name = System.IO.Path.GetFileName(d);
                if (Transfer.Sha.IsValid(name)) entries.Add((name.ToLowerInvariant(), d, true, Directory.GetLastWriteTimeUtc(d), DirSize(d)));
            }

        var now = DateTime.UtcNow;
        var removed = 0;
        long freed = 0;
        var deleted = new HashSet<string>(StringComparer.Ordinal);

        bool Delete((string Sha, string Path, bool IsDir, DateTime LastUse, long Size) e)
        {
            try
            {
                if (e.IsDir) Directory.Delete(e.Path, true); else File.Delete(e.Path);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            deleted.Add(e.Path);
            removed++;
            freed += e.Size;
            return true;
        }

        var candidates = entries.Where(e => !protectedShas.Contains(e.Sha)).OrderBy(e => e.LastUse).ToList();
        if (maxAge > TimeSpan.Zero)
            foreach (var e in candidates.Where(e => now - e.LastUse > maxAge)) Delete(e);

        if (maxBytes > 0)
        {
            var total = entries.Where(e => !deleted.Contains(e.Path)).Sum(e => e.Size);
            foreach (var e in candidates.Where(e => !deleted.Contains(e.Path)))
            {
                if (total <= maxBytes) break;
                if (Delete(e)) total -= e.Size;
            }
        }
        return new EvictionResult(removed, freed);
    }

    private static long DirSize(string dir)
    {
        long total = 0;
        try { foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) total += new FileInfo(f).Length; }
        catch (IOException) { }
        return total;
    }

    /// <summary>Svuota temporanei e cartelle di run (non le cache). Da chiamare all'avvio dell'agente.</summary>
    public void CleanTransient()
    {
        foreach (var root in new[] { TempRoot, RunsRoot })
            foreach (var d in Directory.EnumerateDirectories(root))
                try { Directory.Delete(d, true); } catch (IOException) { }
    }
}
