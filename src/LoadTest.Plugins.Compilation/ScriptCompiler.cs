using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace LoadTest.Plugins.Compilation;

public sealed class ScriptCompilationException(IReadOnlyList<string> errors)
    : Exception(string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>Assembly compilato e, quando richiesto, relativi simboli Portable PDB.</summary>
public sealed record ScriptCompilationResult(byte[] Assembly, byte[]? Pdb);

/// <summary>
/// Compila sorgenti .csx in un assembly (IL portabile) nel processo che prepara l'esecuzione.
/// Il Controller usa il compilatore nel flusso di packaging attuale; il VS Code Host può usarlo
/// direttamente per la compilazione locale e la generazione dei simboli di debug.
/// PROPOSTA P2: il .csx contiene using e classi pubbliche che implementano le interfacce SDK, nessuna istruzione top-level
/// (compilato come libreria, non come script: i tipi restano di primo livello e scopribili dal caricatore).
/// Using impliciti (convenienza, PROPOSTA): System, System.Collections.Generic, System.Linq, System.Threading,
/// System.Threading.Tasks, LoadTest.Sdk.
/// </summary>
public static class ScriptCompiler
{
    private const string ImplicitUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.Linq;
        global using System.Threading;
        global using System.Threading.Tasks;
        global using LoadTest.Sdk;
        """;

    /// <param name="sources">percorso relativo → testo.</param>
    public static byte[] Compile(string assemblyName, IReadOnlyDictionary<string, string> sources,
        IReadOnlyList<MetadataReference>? references = null) =>
        CompileCore(assemblyName, sources, references, emitPortablePdb: false).Assembly;

    /// <summary>
    /// Compila gli stessi sorgenti producendo anche un Portable PDB. I path delle chiavi di
    /// <paramref name="sources"/> diventano i document path del PDB: per il debugging locale
    /// il chiamante deve quindi passare i percorsi originali dei file sorgente.
    /// </summary>
    public static ScriptCompilationResult CompileWithSymbols(string assemblyName,
        IReadOnlyDictionary<string, string> sources, IReadOnlyList<MetadataReference>? references = null) =>
        CompileCore(assemblyName, sources, references, emitPortablePdb: true);

    private static ScriptCompilationResult CompileCore(string assemblyName,
        IReadOnlyDictionary<string, string> sources, IReadOnlyList<MetadataReference>? references, bool emitPortablePdb)
    {
        var parse = new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.None, SourceCodeKind.Regular);
        var trees = sources.OrderBy(s => s.Key, StringComparer.Ordinal)
            .Select(s => CSharpSyntaxTree.ParseText(s.Value, parse, path: s.Key, encoding: Encoding.UTF8)).ToList();
        trees.Add(CSharpSyntaxTree.ParseText(ImplicitUsings, parse, path: "<usings>", encoding: Encoding.UTF8));

        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: emitPortablePdb ? OptimizationLevel.Debug : OptimizationLevel.Release,
            nullableContextOptions: NullableContextOptions.Enable, allowUnsafe: false, deterministic: true);

        var compilation = CSharpCompilation.Create(assemblyName, trees, references ?? PluginReferences.FromRuntime(), options);

        var errors = new List<string>();
        foreach (var d in compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
            errors.Add(d.ToString());
        if (errors.Count > 0) throw new ScriptCompilationException(errors);

        foreach (var tree in trees.Where(t => t.FilePath != "<usings>"))
            new ForbiddenApiWalker(compilation.GetSemanticModel(tree), tree.FilePath, errors).Visit(tree.GetRoot());
        foreach (var name in compilation.ReferencedAssemblyNames)
            if (!PluginReferences.IsAllowed(name.Name))
                errors.Add($"riferimento non consentito: {name.Name}");
        if (errors.Count > 0) throw new ScriptCompilationException(errors);

        using var pe = new MemoryStream();
        using var pdb = emitPortablePdb ? new MemoryStream() : null;
        var emit = compilation.Emit(pe, pdbStream: pdb,
            options: emitPortablePdb ? new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb) : null);
        if (!emit.Success)
            throw new ScriptCompilationException(emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToList());

        return new ScriptCompilationResult(pe.ToArray(), pdb?.ToArray());
    }
}

/// <summary>Trasforma un pacchetto con entry .csx in un pacchetto compilato (entry .dll), inserito nella cache per hash (PROPOSTA P7).</summary>
public static class ScriptPackageCompiler
{
    /// <summary>
    /// Compila entry e `files` .csx insieme; gli altri `files` sono copiati.
    /// Se `emitSymbols` è true, include anche il Portable PDB accanto alla DLL.
    /// Il plugin.yaml del pacchetto compilato ha
    /// `entry: &lt;nome&gt;.dll` e non elenca più i .csx. Restituisce il pacchetto in cache: il suo Sha256 è quello da mettere nel manifest di PrepareRun.
    /// </summary>
    public static PluginPackage Compile(PluginPackage source, PluginCache cache, IReadOnlyList<MetadataReference>? references = null,
        string? signingPrivateKey = null, bool emitSymbols = false,
        IReadOnlyDictionary<string, string>? sourceDocumentPaths = null)
    {
        var m = source.Manifest;
        if (!m.IsScript) throw new ArgumentException("il pacchetto non ha un entry .csx", nameof(source));

        var csx = new List<string> { m.Entry };
        csx.AddRange(m.Files.Where(f => f.EndsWith(".csx", StringComparison.OrdinalIgnoreCase)));
        var others = m.Files.Where(f => !f.EndsWith(".csx", StringComparison.OrdinalIgnoreCase)).ToList();

        string SourceDocumentPath(string file)
        {
            if (!emitSymbols) return file.Replace('\\', '/');
            if (sourceDocumentPaths is not null && sourceDocumentPaths.TryGetValue(file, out var originalPath))
                return Path.GetFullPath(originalPath);
            return Path.GetFullPath(Path.Combine(source.Directory, file.Replace('/', Path.DirectorySeparatorChar)));
        }

        var sources = csx.Distinct().ToDictionary(
            SourceDocumentPath,
            f => File.ReadAllText(Path.Combine(source.Directory, f.Replace('/', Path.DirectorySeparatorChar))));

        var asmName = SafeName(m.Name);
        var compilation = emitSymbols
            ? ScriptCompiler.CompileWithSymbols(asmName, sources, references)
            : new ScriptCompilationResult(ScriptCompiler.Compile(asmName, sources, references), null);

        var tmp = Path.Combine(Path.GetTempPath(), "loadtest-csx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllBytes(Path.Combine(tmp, asmName + ".dll"), compilation.Assembly);

            var packageFiles = new List<string>(others);
            if (compilation.Pdb is not null)
            {
                var pdbName = asmName + ".pdb";
                File.WriteAllBytes(Path.Combine(tmp, pdbName), compilation.Pdb);
                packageFiles.Add(pdbName);
            }

            foreach (var f in others)
            {
                var dest = Path.Combine(tmp, f.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(Path.Combine(source.Directory, f.Replace('/', Path.DirectorySeparatorChar)), dest);
            }
            File.WriteAllText(Path.Combine(tmp, "plugin.yaml"), ManifestYaml(m, asmName + ".dll", packageFiles));
            var added = cache.Add(PluginPackage.Open(tmp));
            if (signingPrivateKey is not null) PluginSigning.SignPackage(added, signingPrivateKey);   // P10 bis: firma del pacchetto compilato
            return added;
        }
        finally { Directory.Delete(tmp, true); }
    }

    /// <summary>
    /// `code: plugins/Custom.csx` (sez. 4) non ha manifest: il file singolo diventa un pacchetto implicito
    /// `kinds: [codedTest]`, versione 1.0.0, sdk = major.minor dell'host (PROPOSTA P2).
    /// </summary>
    public static PluginPackage CompileSingleFile(string csxPath, PluginCache cache, IReadOnlyList<MetadataReference>? references = null,
        string? signingPrivateKey = null, bool emitSymbols = false)
    {
        var name = Path.GetFileNameWithoutExtension(csxPath);
        var host = PluginLoader.HostSdkVersion;
        var tmp = Path.Combine(Path.GetTempPath(), "loadtest-csx-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var file = Path.GetFileName(csxPath);
            File.Copy(csxPath, Path.Combine(tmp, file));
            var originalBytes = File.ReadAllBytes(csxPath);
            var copiedBytes = File.ReadAllBytes(Path.Combine(tmp, file));

            var identical = originalBytes.SequenceEqual(copiedBytes);

            Console.WriteLine($"[ScriptCompiler] Originale: {originalBytes.Length} byte");
            Console.WriteLine($"[ScriptCompiler] Copia:     {copiedBytes.Length} byte");
            Console.WriteLine($"[ScriptCompiler] Identici:  {identical}");
            var implicitManifest = new PluginManifest
            {
                Name = name, Version = "1.0.0", Sdk = $">={host.Major}.{host.Minor} <{host.Major + 1}.0",
                Kinds = [PluginKind.CodedTest], Entry = file, Files = []
            };
            File.WriteAllText(Path.Combine(tmp, "plugin.yaml"), ManifestYaml(implicitManifest, file, []));
            var sourceDocumentPaths = emitSymbols
                ? new Dictionary<string, string>(StringComparer.Ordinal) { [file] = Path.GetFullPath(csxPath) }
                : null;
            return Compile(PluginPackage.Open(tmp), cache, references, signingPrivateKey, emitSymbols, sourceDocumentPaths);
        }
        finally { Directory.Delete(tmp, true); }
    }

    private static string ManifestYaml(PluginManifest m, string entry, IReadOnlyList<string> files)
    {
        static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        var sb = new StringBuilder();
        sb.Append("name: ").Append(Q(m.Name)).Append('\n');
        sb.Append("version: ").Append(Q(m.Version)).Append('\n');
        sb.Append("sdk: ").Append(Q(m.Sdk)).Append('\n');
        sb.Append("kinds: [ ").Append(string.Join(", ", m.Kinds.Select(k => char.ToLowerInvariant(k.ToString()[0]) + k.ToString()[1..]))).Append(" ]\n");
        sb.Append("entry: ").Append(Q(entry)).Append('\n');
        sb.Append("files: [ ").Append(string.Join(", ", files.Select(Q))).Append(" ]\n");
        return sb.ToString();
    }

    private static string SafeName(string name)
    {
        var s = new string(name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        return s.Length == 0 || char.IsDigit(s[0]) ? "_" + s : s;
    }
}
