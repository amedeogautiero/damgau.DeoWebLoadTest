using System.IO.Compression;
using LoadTest.Plugins;
using LoadTest.Protocol;

namespace LoadTest.Agent.Transfer;

/// <summary>
/// Riceve i PackageChunk richiesti con PackageRequest. Ogni voce: sequenza consecutiva da 0, tetto sui byte ricevuti,
/// poi verifica di dimensione e hash e inserimento nella cache giusta (file → ContentCache, pacchetto plugin → PluginCache).
/// Qualsiasi anomalia lancia InvalidDataException (o l'eccezione del componente sottostante): il chiamante annulla la preparazione.
/// </summary>
public sealed class TransferReceiver : IDisposable
{
    private sealed class State
    {
        public required ManifestEntry Entry { get; init; }
        public required string TempPath { get; init; }
        public required long Cap { get; init; }
        public FileStream? Out;
        public long NextSeq;
        public long Bytes;
        public bool Done;
    }

    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    private readonly ContentCache _files;
    private readonly PluginCache _plugins;
    private readonly string _dir;

    public TransferReceiver(IEnumerable<ManifestEntry> requested, ContentCache files, PluginCache plugins, string tempRoot)
    {
        _files = files;
        _plugins = plugins;
        _dir = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        foreach (var e in requested)
        {
            var sha = e.Sha256.ToLowerInvariant();
            if (_states.ContainsKey(sha)) continue;
            // tetto sui byte compressi: dimensione dichiarata + 20% + 1 MiB (euristica per dati incomprimibili e overhead dello zip)
            _states[sha] = new State { Entry = e, TempPath = Path.Combine(_dir, sha + ".part"), Cap = e.Size + e.Size / 5 + (1L << 20) };
        }
    }

    public int Pending => _states.Values.Count(s => !s.Done);

    /// <returns>true se questo chunk ha completato la voce (già verificata e inserita in cache).</returns>
    public bool Accept(PackageChunk c)
    {
        if (!_states.TryGetValue(c.Sha256.ToLowerInvariant(), out var st) || st.Done)
            throw new InvalidDataException($"chunk non richiesto: {c.Sha256}");
        if (c.Sequence != st.NextSeq)
            throw new InvalidDataException($"chunk fuori sequenza per {c.Sha256}: atteso {st.NextSeq}, ricevuto {c.Sequence}");
        st.NextSeq++;
        st.Bytes += c.Data.Length;
        if (st.Bytes > st.Cap) throw new InvalidDataException($"troppi byte ricevuti per {c.Sha256}");

        st.Out ??= new FileStream(st.TempPath, FileMode.Create, FileAccess.Write, FileShare.None);
        c.Data.WriteTo(st.Out);
        if (!c.Last) return false;

        st.Out.Dispose();
        st.Out = null;
        try { Store(st); }
        finally { if (File.Exists(st.TempPath)) File.Delete(st.TempPath); }
        st.Done = true;
        return true;
    }

    private void Store(State st)
    {
        var e = st.Entry;
        var sha = e.Sha256.ToLowerInvariant();
        switch (e.Encoding)
        {
            case ContentEncoding.FileGzip:
            {
                using var fs = File.OpenRead(st.TempPath);
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                _files.Add(gz, sha, e.Size);
                break;
            }
            case ContentEncoding.PackageZip:
            {
                var dir = st.TempPath + ".dir";
                try
                {
                    ZipExtractor.Extract(st.TempPath, dir, e.Size);
                    _plugins.Add(PluginPackage.Open(dir), sha);   // verifica che l'hash del pacchetto coincida
                }
                finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                break;
            }
            default:
                throw new InvalidDataException($"encoding {e.Encoding} non supportato");
        }
    }

    public void Dispose()
    {
        foreach (var s in _states.Values) s.Out?.Dispose();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
        catch (IOException) { }
    }
}
