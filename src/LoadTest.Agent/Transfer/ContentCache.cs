using System.Security.Cryptography;

namespace LoadTest.Agent.Transfer;

/// <summary>Cache locale dei file singoli per SHA-256 del contenuto non compresso: &lt;root&gt;/&lt;sha256&gt;.bin. Inserimento atomico e verificato.</summary>
public sealed class ContentCache
{
    private readonly string _root;

    public ContentCache(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public bool Contains(string sha256) => File.Exists(PathFor(sha256));

    public string PathFor(string sha256)
    {
        if (!Sha.IsValid(sha256)) throw new ArgumentException("hash SHA-256 non valido", nameof(sha256));
        return Path.Combine(_root, sha256.ToLowerInvariant() + ".bin");
    }

    /// <summary>Legge lo stream (max expectedSize byte), verifica dimensione e hash, poi lo pubblica in cache.</summary>
    public void Add(Stream content, string expectedSha256, long expectedSize)
    {
        var target = PathFor(expectedSha256);
        if (File.Exists(target)) return;

        var tmp = Path.Combine(_root, ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                int n;
                while ((n = content.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += n;
                    if (total > expectedSize) throw new InvalidDataException("contenuto oltre la dimensione dichiarata");
                    hash.AppendData(buffer, 0, n);
                    fs.Write(buffer, 0, n);
                }
            }
            if (total != expectedSize) throw new InvalidDataException($"dimensione {total} diversa da quella dichiarata ({expectedSize})");
            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"hash del file non corrisponde: atteso {expectedSha256}, trovato {actual}");
            try { File.Move(tmp, target); }
            catch (IOException) when (File.Exists(target)) { /* inserito in parallelo */ }
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}
