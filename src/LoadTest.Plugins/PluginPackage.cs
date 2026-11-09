using System.Security.Cryptography;
using System.Text;

namespace LoadTest.Plugins;

/// <summary>
/// Pacchetto = cartella con plugin.yaml, entry e files. Hash (PROPOSTA, il contratto dice solo "SHA-256"):
/// SHA-256 dell'elenco ordinato (ordinal) di righe "percorso/relativo\0sha256-hex-del-file\n" per plugin.yaml, entry e files,
/// con separatore '/'. Così l'hash non dipende da ordine di lettura, timestamp o separatori del sistema operativo.
/// </summary>
public sealed class PluginPackage
{
    public required PluginManifest Manifest { get; init; }
    public required string Directory { get; init; }
    public required string Sha256 { get; init; }

    public string EntryPath => Path.Combine(Directory, Manifest.Entry);

    public static PluginPackage Open(string directory)
    {
        var dir = Path.GetFullPath(directory);
        var manifest = PluginManifest.LoadFile(Path.Combine(dir, "plugin.yaml"));
        var rel = new SortedSet<string>(StringComparer.Ordinal) { "plugin.yaml", Norm(manifest.Entry) };
        foreach (var f in manifest.Files) rel.Add(Norm(f));

        var sb = new StringBuilder();
        foreach (var r in rel)
        {
            var full = Path.Combine(dir, r.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) throw new PluginManifestException($"{dir}: file del pacchetto mancante: '{r}'");
            using var s = File.OpenRead(full);
            sb.Append(r).Append('\0').Append(Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant()).Append('\n');
        }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
        return new PluginPackage { Manifest = manifest, Directory = dir, Sha256 = hash };
    }

    private static string Norm(string p) => p.Replace('\\', '/').TrimStart('/');
}

/// <summary>
/// Cache locale per hash (lato agente e controller): &lt;root&gt;/&lt;sha256&gt;/ contiene i file del pacchetto.
/// Inserimento atomico: copia in cartella temporanea, verifica dell'hash, poi rename.
/// </summary>
public sealed class PluginCache(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    public bool Contains(string sha256) => System.IO.Directory.Exists(PathFor(sha256));

    /// <summary>Sottoinsieme degli hash del manifest che NON sono in cache (da chiedere con PackageRequest).</summary>
    public IReadOnlyList<string> Missing(IEnumerable<string> hashes) => hashes.Where(h => !Contains(h)).Distinct().ToList();

    public PluginPackage Open(string sha256) => PluginPackage.Open(PathFor(sha256));

    /// <summary>Copia il pacchetto in cache e verifica che l'hash coincida con quello dichiarato.</summary>
    public PluginPackage Add(PluginPackage source, string? expectedSha256 = null)
    {
        var expected = expectedSha256 ?? source.Sha256;
        var target = PathFor(expected);
        if (System.IO.Directory.Exists(target)) return PluginPackage.Open(target);

        System.IO.Directory.CreateDirectory(Root);
        var tmp = Path.Combine(Root, ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyDir(source.Directory, tmp);
            var copied = PluginPackage.Open(tmp);
            if (!string.Equals(copied.Sha256, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"hash del pacchetto '{source.Manifest.Name}' non corrisponde: atteso {expected}, trovato {copied.Sha256}");
            try { System.IO.Directory.Move(tmp, target); }
            catch (IOException) when (System.IO.Directory.Exists(target)) { /* inserito in parallelo da un altro processo */ }
        }
        finally { if (System.IO.Directory.Exists(tmp)) System.IO.Directory.Delete(tmp, true); }
        return PluginPackage.Open(target);
    }

    private string PathFor(string sha256)
    {
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) throw new ArgumentException("hash SHA-256 non valido", nameof(sha256));
        return Path.Combine(Root, sha256.ToLowerInvariant());
    }

    private static void CopyDir(string from, string to)
    {
        System.IO.Directory.CreateDirectory(to);
        foreach (var f in System.IO.Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, f));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest);
        }
    }
}
