using System.IO.Compression;
using System.Security.Cryptography;
using Google.Protobuf;
using LoadTest.Controller.Rig;
using LoadTest.Controller.Runs;
using LoadTest.Engine.LoadTesting;
using LoadTest.Engine.Model;
using LoadTest.Plugins;
using LoadTest.Protocol;
using LoadTest.Sdk;

namespace LoadTest.Controller.Packaging;

/// <summary>Un contenuto pronto da inviare a pezzi: file già compresso (gzip) o pacchetto plugin in zip.</summary>
public sealed class TransferItem
{
    public required string Path { get; init; }
    public required ContentEncoding Encoding { get; init; }
    public required long Size { get; init; }
}

public sealed class PackageSet
{
    /// <summary>Voci uguali per tutti gli agenti.</summary>
    public required IReadOnlyList<ManifestEntry> Common { get; init; }
    /// <summary>Per agente: le fette dei file con partition: true.</summary>
    public required IReadOnlyList<IReadOnlyList<ManifestEntry>> PerAgent { get; init; }
    public required IReadOnlyDictionary<string, TransferItem> Items { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    public IReadOnlyList<ManifestEntry> ManifestFor(int agentIndex) => [.. Common, .. PerAgent[agentIndex]];
}

/// <summary>
/// Raccoglie ciò che serve agli agenti: load test, web test (anche richiamati con `call:`), data source, file di body, certificati,
/// deployment items del rig e pacchetti plugin (cartella `plugins/` accanto al load test, una sottocartella per pacchetto: P5 della chat 5).
/// Tutto deve stare nella cartella del load test (percorsi relativi, mai assoluti). I percorsi con variabili `{{...}}` non si possono
/// distribuire: avviso. Errori raccolti tutti insieme in <see cref="RunPlanException"/>.
/// </summary>
public static class RunPackager
{
    public static PackageSet Build(string loadTestPath, LoadTestDefinition def, RigDefinition rig, IReadOnlyList<double> shares, string workDir, IPluginCompiler? compiler)
    {
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(loadTestPath))!;
        var b = new Builder(baseDir, workDir, shares, compiler);

        b.AddFile(Path.GetFullPath(loadTestPath), EntryKind.LoadTest, false);
        foreach (var sc in def.Scenarios)
        {
            foreach (var t in sc.TestMix.Tests)
            {
                var p = b.Resolve(baseDir, t.Test, $"scenario '{sc.Name}': test");
                if (p is not null) b.AddWebTest(p);
            }
            if (sc.LoadPattern.Type == LoadPatternType.Custom && !string.IsNullOrWhiteSpace(sc.LoadPattern.Plugin)) b.NeedPlugin(sc.LoadPattern.Plugin!);
        }
        foreach (var p in def.LoadTestPlugins) b.NeedPlugin(p.Name);
        foreach (var d in rig.Deployment) b.AddDeployment(d);
        return b.Finish();
    }

    private sealed class Builder(string baseDir, string workDir, IReadOnlyList<double> shares, IPluginCompiler? compiler)
    {
        private sealed class FileSpec
        {
            public required string Abs { get; init; }
            public required string Rel { get; init; }
            public EntryKind Kind { get; set; }
            public bool Partitioned { get; set; }
        }

        private readonly Dictionary<string, FileSpec> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _plugins = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _visited = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _errors = [];
        private readonly List<string> _warnings = [];
        private readonly string _transfer = Path.Combine(workDir, "transfer");

        // ---------- raccolta ----------

        /// <summary>Percorso assoluto di un riferimento (relativo alla cartella del file che lo cita, poi a quella del load test), o null con errore/avviso.</summary>
        public string? Resolve(string dir, string path, string what)
        {
            if (path.Contains("{{")) { _warnings.Add($"{what}: percorso con variabili non distribuibile ('{path}')"); return null; }
            if (Path.IsPathRooted(path)) { _errors.Add($"{what}: percorso assoluto non distribuibile ('{path}')"); return null; }
            foreach (var root in new[] { dir, baseDir })
            {
                var candidate = Path.GetFullPath(Path.Combine(root, path));
                if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
            }
            _errors.Add($"{what}: file non trovato '{path}'");
            return null;
        }

        private string? Rel(string abs)
        {
            var rel = Path.GetRelativePath(baseDir, Path.GetFullPath(abs)).Replace('\\', '/');
            if (rel == ".." || rel.StartsWith("../") || Path.IsPathRooted(rel))
            {
                _errors.Add($"'{abs}' è fuori dalla cartella del load test: non si può distribuire");
                return null;
            }
            return rel;
        }

        public void AddFile(string abs, EntryKind kind, bool partitioned)
        {
            var rel = Rel(abs);
            if (rel is null) return;
            if (!File.Exists(abs)) { _errors.Add($"file non trovato: '{rel}'"); return; }
            if (_files.TryGetValue(rel, out var s))
            {
                s.Partitioned |= partitioned;
                if (kind is EntryKind.LoadTest or EntryKind.WebTest) s.Kind = kind;
                return;
            }
            _files[rel] = new FileSpec { Abs = abs, Rel = rel, Kind = kind, Partitioned = partitioned };
        }

        public void AddDeployment(RigDeploymentDef d)
        {
            var abs = Resolve(baseDir, d.Path, "rig.deployment");
            if (abs is null) return;
            if (Directory.Exists(abs))
            {
                foreach (var f in Directory.EnumerateFiles(abs, "*", SearchOption.AllDirectories)) AddFile(f, EntryKind.Deployment, d.Partition);
            }
            else AddFile(abs, EntryKind.Deployment, d.Partition);
        }

        public void NeedPlugin(string reference)
        {
            var name = reference.Split('/')[0].Trim();
            if (name.Length > 0) _plugins.Add(name);
        }

        public void AddWebTest(string abs)
        {
            var full = Path.GetFullPath(abs);
            if (!_visited.Add(full)) return;
            AddFile(full, EntryKind.WebTest, false);

            WebTestDefinition t;
            try { t = WebTestLoader.LoadFile(full); }
            catch (WebTestLoadException ex) { _errors.Add(ex.Message); return; }
            catch (IOException ex) { _errors.Add($"{full}: {ex.Message}"); return; }

            var dir = Path.GetDirectoryName(full)!;
            var label = Path.GetFileName(full);
            foreach (var (name, ds) in t.Data)
                if (!string.IsNullOrEmpty(ds.Path) && !ds.Type.Equals("database", StringComparison.OrdinalIgnoreCase))
                {
                    var p = Resolve(dir, ds.Path, $"{label}: data '{name}'");
                    if (p is not null) AddFile(p, EntryKind.Data, false);
                }
            if (t.ClientCertificate is { Path.Length: > 0 } cc)
            {
                var p = Resolve(dir, cc.Path, $"{label}: clientCertificate");
                if (p is not null) AddFile(p, EntryKind.Deployment, false);
            }
            foreach (var pr in t.WebTestPlugins.Concat(t.RequestPlugins)) NeedPlugin(pr.Name);
            Walk(t.Steps, dir, label);
        }

        private void Walk(IEnumerable<StepDef> steps, string dir, string label)
        {
            foreach (var step in steps)
            {
                switch (step)
                {
                    case RequestStep r:
                        if (r.Body is { } body)
                        {
                            if (body.Kind == BodyKind.File && !string.IsNullOrEmpty(body.FilePath)) AddRef(dir, body.FilePath, $"{label}: [{r.Name}] body file");
                            foreach (var part in body.Parts.Where(p => !string.IsNullOrEmpty(p.FilePath)))
                                AddRef(dir, part.FilePath!, $"{label}: [{r.Name}] multipart file");
                        }
                        foreach (var rule in r.Extract.Concat(r.Validate))
                            if (rule.Type.Equals("custom", StringComparison.OrdinalIgnoreCase) && rule.Args.TryGetValue("rule", out var ruleName)) NeedPlugin(ruleName);
                        break;
                    case TransactionStep t: Walk(t.Steps, dir, label); break;
                    case IfStep i: Walk(i.Then, dir, label); Walk(i.Else, dir, label); break;
                    case LoopStep l: Walk(l.Steps, dir, label); break;
                    case CallStep c:
                        var called = Resolve(dir, c.Path, $"{label}: call");
                        if (called is not null) AddWebTest(called);
                        break;
                    case CodeStep code:
                        AddRef(dir, code.Path, $"{label}: code");
                        _warnings.Add($"{label}: il passo code '{code.Path}' non è eseguibile sugli agenti finché non c'è un ICodeStepHost (G11)");
                        break;
                }
            }
        }

        private void AddRef(string dir, string path, string what)
        {
            var p = Resolve(dir, path, what);
            if (p is not null) AddFile(p, EntryKind.Deployment, false);
        }

        // ---------- composizione ----------

        public PackageSet Finish()
        {
            Directory.CreateDirectory(_transfer);
            var common = new List<ManifestEntry>();
            var perAgent = Enumerable.Range(0, shares.Count).Select(_ => new List<ManifestEntry>()).ToList();
            var items = new Dictionary<string, TransferItem>(StringComparer.Ordinal);

            AddPlugins(common, items);
            foreach (var f in _files.Values.OrderBy(x => x.Rel, StringComparer.Ordinal))
            {
                try
                {
                    if (f.Partitioned) AddPartitioned(f, perAgent, items);
                    else common.Add(AddPlain(f, items));
                }
                catch (IOException ex) { _errors.Add($"{f.Rel}: {ex.Message}"); }
            }

            if (_errors.Count > 0) throw new RunPlanException(_errors);
            return new PackageSet
            {
                Common = common, PerAgent = perAgent.Select(l => (IReadOnlyList<ManifestEntry>)l).ToList(),
                Items = items, Warnings = _warnings
            };
        }

        private ManifestEntry AddPlain(FileSpec f, Dictionary<string, TransferItem> items)
        {
            string sha;
            using (var fs = File.OpenRead(f.Abs)) sha = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            var size = new FileInfo(f.Abs).Length;
            if (!items.ContainsKey(sha))
            {
                var gz = Path.Combine(_transfer, sha + ".gz");
                using (var src = File.OpenRead(f.Abs)) GzipTo(src, gz);
                items[sha] = new TransferItem { Path = gz, Encoding = ContentEncoding.FileGzip, Size = size };
            }
            return Entry(f, sha, size, partitioned: false);
        }

        private void AddPartitioned(FileSpec f, List<List<ManifestEntry>> perAgent, Dictionary<string, TransferItem> items)
        {
            if (!f.Rel.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                _errors.Add($"{f.Rel}: partition: true è supportato solo per file .csv");
                return;
            }
            var slices = CsvPartitioner.Slice(File.ReadAllBytes(f.Abs), shares);
            for (var i = 0; i < slices.Length; i++)
            {
                var sha = Convert.ToHexString(SHA256.HashData(slices[i])).ToLowerInvariant();
                if (!items.ContainsKey(sha))
                {
                    var gz = Path.Combine(_transfer, sha + ".gz");
                    using (var ms = new MemoryStream(slices[i])) GzipTo(ms, gz);
                    items[sha] = new TransferItem { Path = gz, Encoding = ContentEncoding.FileGzip, Size = slices[i].Length };
                }
                perAgent[i].Add(Entry(f, sha, slices[i].Length, partitioned: true));
            }
        }

        private static ManifestEntry Entry(FileSpec f, string sha, long size, bool partitioned) => new()
        {
            Kind = f.Kind, Name = Path.GetFileName(f.Rel), Sha256 = sha, Size = size, Path = f.Rel,
            Encoding = ContentEncoding.FileGzip, Partitioned = partitioned
        };

        private static void GzipTo(Stream src, string target)
        {
            using var dst = File.Create(target);
            using var gz = new GZipStream(dst, CompressionLevel.Optimal);
            src.CopyTo(gz);
        }

        // ---------- plugin ----------

        private void AddPlugins(List<ManifestEntry> common, Dictionary<string, TransferItem> items)
        {
            if (_plugins.Count == 0) return;
            var found = ScanPlugins();
            foreach (var name in _plugins.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                if (!found.TryGetValue(name, out var pkg))
                {
                    _errors.Add($"plugin '{name}' non trovato in plugins/ accanto al load test");
                    continue;
                }
                try
                {
                    if (pkg.Manifest.IsScript)
                    {
                        if (compiler is null) { _errors.Add($"plugin '{name}': entry .csx ma nessun compilatore collegato (G16)"); continue; }
                        pkg = compiler.Compile(pkg, Path.Combine(workDir, "compiled", name));
                    }
                    common.Add(ZipPlugin(pkg, items));
                }
                catch (Exception ex) when (ex is IOException or PluginManifestException or InvalidOperationException or InvalidDataException)
                {
                    _errors.Add($"plugin '{name}': {ex.Message}");
                }
            }
        }

        private Dictionary<string, PluginPackage> ScanPlugins()
        {
            var map = new Dictionary<string, PluginPackage>(StringComparer.OrdinalIgnoreCase);
            var root = Path.Combine(baseDir, "plugins");
            if (!Directory.Exists(root)) return map;
            foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
            {
                if (!File.Exists(Path.Combine(dir, "plugin.yaml"))) continue;
                try
                {
                    var pkg = PluginPackage.Open(dir);
                    if (!map.TryAdd(pkg.Manifest.Name, pkg)) _warnings.Add($"plugins/: nome pacchetto duplicato '{pkg.Manifest.Name}' ({Path.GetFileName(dir)} ignorato)");
                }
                catch (PluginManifestException ex) { _warnings.Add($"plugins/{Path.GetFileName(dir)}: {ex.Message}"); }
            }
            return map;
        }

        private ManifestEntry ZipPlugin(PluginPackage pkg, Dictionary<string, TransferItem> items)
        {
            var rels = new List<string> { "plugin.yaml", pkg.Manifest.Entry.Replace('\\', '/').TrimStart('/') };
            rels.AddRange(pkg.Manifest.Files.Select(f => f.Replace('\\', '/').TrimStart('/')));
            rels = rels.Distinct(StringComparer.Ordinal).ToList();

            var final = Path.Combine(_transfer, pkg.Sha256 + ".zip");
            long size = 0;
            var tmp = Path.Combine(_transfer, "plugin-" + Guid.NewGuid().ToString("N") + ".zip");
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
                foreach (var rel in rels)
                {
                    var abs = Path.Combine(pkg.Directory, rel.Replace('/', Path.DirectorySeparatorChar));
                    zip.CreateEntryFromFile(abs, rel, CompressionLevel.Optimal);
                    size += new FileInfo(abs).Length;
                }
            if (File.Exists(final)) File.Delete(tmp); else File.Move(tmp, final);

            items[pkg.Sha256] = new TransferItem { Path = final, Encoding = ContentEncoding.PackageZip, Size = size };
            return new ManifestEntry
            {
                Kind = EntryKind.Plugin, Name = pkg.Manifest.Name, Version = pkg.Manifest.Version, Sha256 = pkg.Sha256,
                Size = size, Path = "plugins/" + pkg.Manifest.Name, Encoding = ContentEncoding.PackageZip
            };
        }
    }
}
