using System.IO.Compression;

namespace LoadTest.Cli.Remote;

/// <summary>Zip della cartella del load test da inviare al controller. Esclude cartelle di build/versionamento e database dei risultati.</summary>
public static class ProjectZipper
{
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", ".git", ".vs", ".vscode", "node_modules", "loadtest-results" };
    private static readonly HashSet<string> SkipExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".db", ".db-wal", ".db-shm", ".zip" };

    public static string Create(string directory, long maxBytes)
    {
        var root = Path.GetFullPath(directory);
        var zipPath = Path.Combine(Path.GetTempPath(), "loadtest-project-" + Guid.NewGuid().ToString("N") + ".zip");
        long total = 0;
        try
        {
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            foreach (var file in Enumerate(root))
            {
                total += new FileInfo(file).Length;
                if (total > maxBytes)
                    throw new CliException(ExitCodes.Usage, $"la cartella del load test supera {maxBytes / (1024 * 1024)} MB: alleggeriscila, alza --max-upload-mb o usa --no-upload con --remote-path");
                zip.CreateEntryFromFile(file, Path.GetRelativePath(root, file).Replace('\\', '/'), CompressionLevel.Optimal);
            }
            return zipPath;
        }
        catch
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
            throw;
        }
    }

    private static IEnumerable<string> Enumerate(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir).OrderBy(x => x, StringComparer.Ordinal))
            if (!SkipExtensions.Contains(Path.GetExtension(f))) yield return f;
        foreach (var d in Directory.EnumerateDirectories(dir).OrderBy(x => x, StringComparer.Ordinal))
            if (!SkipDirs.Contains(Path.GetFileName(d)))
                foreach (var f in Enumerate(d)) yield return f;
    }
}
