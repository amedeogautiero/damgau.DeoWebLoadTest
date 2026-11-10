using System.IO.Compression;

namespace LoadTest.Agent.Transfer;

public static class ZipExtractor
{
    public const int MaxEntries = 10_000;

    /// <summary>Estrae rifiutando percorsi fuori dalla cartella; i byte sono contati mentre si leggono (la dimensione dichiarata nello zip non è fidata).</summary>
    public static void Extract(string zipPath, string targetDir, long maxTotalBytes)
    {
        Directory.CreateDirectory(targetDir);
        using var zip = ZipFile.OpenRead(zipPath);
        long total = 0;
        var count = 0;
        var buffer = new byte[81920];
        foreach (var entry in zip.Entries)
        {
            if (++count > MaxEntries) throw new InvalidDataException("zip con troppe voci");
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/'))
            {
                Directory.CreateDirectory(SafePath.Combine(targetDir, name.TrimEnd('/')));
                continue;
            }
            var dest = SafePath.Combine(targetDir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var src = entry.Open();
            using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
            int n;
            while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += n;
                if (total > maxTotalBytes) throw new InvalidDataException("zip oltre la dimensione dichiarata");
                dst.Write(buffer, 0, n);
            }
        }
    }
}
