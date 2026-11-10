using System.Text.RegularExpressions;
using LoadTest.Agent.Transfer;
using LoadTest.Protocol;

namespace LoadTest.Agent;

/// <summary>Cartella di lavoro di una run: validazione del manifest ricevuto e copia dei file dalla cache.</summary>
public sealed class RunWorkspace
{
    private static readonly Regex RunIdRx = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public const int MaxEntries = 2000;

    public required string RunId { get; init; }
    public required string RunDir { get; init; }
    public required string LoadTestPath { get; init; }
    public required int PartitionIndex { get; init; }
    public required int PartitionCount { get; init; }
    public required string ControllerName { get; init; }
    /// <summary>Cartelle (nella cache dei plugin) dei pacchetti plugin della run.</summary>
    public required IReadOnlyList<string> PluginDirs { get; init; }

    /// <summary>Controlla il manifest (il controller non è fidato ciecamente) e normalizza i percorsi e gli hash IN PLACE.</summary>
    public static IReadOnlyList<ManifestEntry> ValidateManifest(PrepareRun p, AgentOptions o)
    {
        if (!RunIdRx.IsMatch(p.RunId)) throw new InvalidDataException($"run_id non valido: '{p.RunId}'");
        if (p.Manifest.Count == 0 || p.Manifest.Count > MaxEntries) throw new InvalidDataException($"manifest con {p.Manifest.Count} voci");

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        ManifestEntry? load = null;
        foreach (var e in p.Manifest)
        {
            if (e.Kind == EntryKind.Unspecified) throw new InvalidDataException($"voce '{e.Name}' senza kind");
            if (!Sha.IsValid(e.Sha256)) throw new InvalidDataException($"voce '{e.Name}': sha256 non valido");
            e.Sha256 = e.Sha256.ToLowerInvariant();
            if (e.Size < 0 || e.Size > o.MaxEntryBytes) throw new InvalidDataException($"voce '{e.Name}': dimensione {e.Size} non ammessa");
            var expected = e.Kind == EntryKind.Plugin ? ContentEncoding.PackageZip : ContentEncoding.FileGzip;
            if (e.Encoding != expected) throw new InvalidDataException($"voce '{e.Name}': encoding {e.Encoding} non valido per {e.Kind}");
            if (!SafePath.IsSafeRelative(e.Path, out var norm)) throw new InvalidDataException($"voce '{e.Name}': percorso non ammesso '{e.Path}'");
            e.Path = norm;
            if (!paths.Add(norm)) throw new InvalidDataException($"percorso duplicato nel manifest: '{norm}'");
            total += e.Size;
            if (total > o.MaxRunBytes) throw new InvalidDataException("manifest oltre il limite di dimensione della run");
            if (e.Kind == EntryKind.LoadTest)
            {
                if (load is not null) throw new InvalidDataException("più voci LOAD_TEST nel manifest");
                load = e;
            }
        }
        if (load is null) throw new InvalidDataException("manifest senza voce LOAD_TEST");
        if (!SafePath.IsSafeRelative(p.LoadTestPath, out var lp) || !string.Equals(lp, load.Path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("load_test_path non coincide con la voce LOAD_TEST del manifest");
        if (p.PartitionIndex < 0 || p.PartitionCount < 0 || (p.PartitionCount > 0 && p.PartitionIndex >= p.PartitionCount))
            throw new InvalidDataException("partizione non valida");
        return p.Manifest.ToList();
    }

    /// <summary>Crea la cartella della run copiando i file dalla cache; i pacchetti plugin restano nella cache (si caricano da lì).</summary>
    public static RunWorkspace Materialize(PrepareRun p, IReadOnlyList<ManifestEntry> entries, AgentCaches caches)
    {
        var runDir = Path.Combine(caches.RunsRoot, p.RunId);
        if (Directory.Exists(runDir)) Directory.Delete(runDir, true);
        Directory.CreateDirectory(runDir);

        var pluginDirs = new List<string>();
        foreach (var e in entries)
        {
            if (e.Kind == EntryKind.Plugin)
            {
                var dir = caches.Plugins.Open(e.Sha256).Directory;
                if (!pluginDirs.Contains(dir)) pluginDirs.Add(dir);
                continue;
            }
            var dest = SafePath.Combine(runDir, e.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(caches.Files.PathFor(e.Sha256), dest, overwrite: true);
        }

        return new RunWorkspace
        {
            RunId = p.RunId, RunDir = runDir, LoadTestPath = SafePath.Combine(runDir, p.LoadTestPath),
            PartitionIndex = p.PartitionIndex, PartitionCount = p.PartitionCount, ControllerName = p.ControllerName,
            PluginDirs = pluginDirs
        };
    }

    public static void Delete(AgentCaches caches, string runId)
    {
        var dir = Path.Combine(caches.RunsRoot, runId);
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (IOException) { }
    }
}
