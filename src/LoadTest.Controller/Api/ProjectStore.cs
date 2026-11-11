using System.IO.Compression;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace LoadTest.Controller.Api;

/// <summary>
/// Progetti caricati dalla CLI (zip della cartella del load test) quando il controller non vede il filesystem del chiamante (CI).
/// Estrazione difensiva (zip slip, dimensione, numero di voci); i progetti scadono dopo ProjectRetentionHours.
/// </summary>
public sealed partial class ProjectStore(IOptions<ControllerOptions> options, ILogger<ProjectStore> log)
{
    private const int MaxEntries = 20_000;

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex IdRx();

    private string Root => Path.Combine(Path.GetFullPath(options.Value.WorkDirectory), "projects");

    public async Task<string> SaveZipAsync(Stream zip, CancellationToken ct)
    {
        Directory.CreateDirectory(Root);
        Cleanup();
        var id = Guid.NewGuid().ToString("N");
        var tmp = Path.Combine(Root, id + ".zip");
        var dir = Path.Combine(Root, id);
        try
        {
            await using (var fs = File.Create(tmp)) await zip.CopyToAsync(fs, ct);
            try { Extract(tmp, dir, options.Value.MaxProjectUploadBytes); }
            catch (InvalidDataException) { TryDelete(dir); throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { TryDelete(dir); throw new InvalidDataException(ex.Message); }
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
        log.LogInformation("Progetto {Id} caricato", id);
        return id;
    }

    /// <summary>Percorso assoluto del load test dentro un progetto caricato; lancia InvalidDataException se id o percorso non sono validi.</summary>
    public string ResolveLoadTest(string projectId, string relativePath)
    {
        if (!IdRx().IsMatch(projectId)) throw new InvalidDataException("projectId non valido");
        var dir = Path.Combine(Root, projectId);
        if (!Directory.Exists(dir)) throw new InvalidDataException("progetto non trovato (scaduto?)");
        var full = SafeCombine(dir, relativePath);
        if (!File.Exists(full)) throw new InvalidDataException($"file non trovato nel progetto: '{relativePath}'");
        return full;
    }

    public static string SafeCombine(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\0')) throw new InvalidDataException("percorso non valido");
        var s = relative.Replace('\\', '/');
        if (s.StartsWith('/') || (s.Length >= 2 && s[1] == ':')) throw new InvalidDataException($"percorso non ammesso: '{relative}'");
        var parts = s.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or "..")) throw new InvalidDataException($"percorso non ammesso: '{relative}'");
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, string.Join(Path.DirectorySeparatorChar, parts)));
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException($"percorso fuori dalla cartella: '{relative}'");
        return full;
    }

    /// <summary>Estrae contando i byte mentre legge (la dimensione dichiarata nello zip non è fidata).</summary>
    public static void Extract(string zipPath, string targetDir, long maxTotalBytes)
    {
        Directory.CreateDirectory(targetDir);
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(zipPath); }
        catch (InvalidDataException) { throw new InvalidDataException("lo zip del progetto non è valido"); }
        using (zip)
        {
            long total = 0;
            var count = 0;
            var buffer = new byte[81920];
            foreach (var entry in zip.Entries)
            {
                if (++count > MaxEntries) throw new InvalidDataException("zip con troppe voci");
                var name = entry.FullName.Replace('\\', '/');
                if (name.EndsWith('/')) { Directory.CreateDirectory(SafeCombine(targetDir, name.TrimEnd('/'))); continue; }
                var dest = SafeCombine(targetDir, name);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using var src = entry.Open();
                using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
                int n;
                while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += n;
                    if (total > maxTotalBytes) throw new InvalidDataException("progetto oltre la dimensione massima");
                    dst.Write(buffer, 0, n);
                }
            }
        }
    }

    private void Cleanup()
    {
        var limit = DateTime.UtcNow - TimeSpan.FromHours(Math.Max(1, options.Value.ProjectRetentionHours));
        foreach (var d in Directory.EnumerateDirectories(Root))
            if (Directory.GetCreationTimeUtc(d) < limit) TryDelete(d);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
