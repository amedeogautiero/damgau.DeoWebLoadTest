using LoadTest.Plugins;
using LoadTest.Plugins.Compilation;
using LoadTest.Sdk;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace LoadTest.Engine.Tests;

public class ScriptCompilerTests
{
    private const string Manifest = """
        name: Echo
        version: 1.0.0
        sdk: ">=1.0 <2.0"
        kinds: [ extraction ]
        entry: Echo.csx
        files: [ ]
        """;

    private const string EchoRule = """
        public sealed class EchoRule : IExtractionRule
        {
            public ExtractionResult Extract(RequestContext req, ResponseContext resp, IReadOnlyDictionary<string, string> args)
                => ExtractionResult.Ok(resp.BodyText.ToUpperInvariant());
        }
        """;

    private static PluginPackage NewPackage(string code, string manifest = Manifest)
    {
        var d = Directory.CreateTempSubdirectory("csx").FullName;
        File.WriteAllText(Path.Combine(d, "plugin.yaml"), manifest);
        File.WriteAllText(Path.Combine(d, "Echo.csx"), code);
        return PluginPackage.Open(d);
    }

    private static PluginCache NewCache() => new(Directory.CreateTempSubdirectory("cache").FullName);

    [Fact]
    public void CompileWithSymbols_emits_portable_pdb()
    {
        var temp = Directory.CreateTempSubdirectory("csx-pdb");
        try
        {
            var sourcePath = Path.GetFullPath(Path.Combine(temp.FullName, "DebugScript.csx"));
            const string code = """
            public sealed class DebugScript : ICodedTest
            {
                public Task RunAsync(TestContext ctx)
                {
                    var value = 42;
                    ctx.Set("value", value.ToString());
                    return Task.CompletedTask;
                }
            }
            """;

        var result = ScriptCompiler.CompileWithSymbols(
            "DebugScript",
            new Dictionary<string, string> { [sourcePath] = code });

            Assert.NotEmpty(result.Assembly);
            Assert.NotNull(result.Pdb);
            Assert.NotEmpty(result.Pdb!);
            Assert.Equal("BSJB", System.Text.Encoding.ASCII.GetString(result.Pdb!, 0, 4));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public void Compiles_class_only_script_and_loads_it()
    {
        var compiled = ScriptPackageCompiler.Compile(NewPackage(EchoRule), NewCache(), emitSymbols: true);

        Assert.False(compiled.Manifest.IsScript);
        Assert.EndsWith(".dll", compiled.Manifest.Entry);
        var pdbName = Path.GetFileNameWithoutExtension(compiled.Manifest.Entry) + ".pdb";
        Assert.Contains(pdbName, compiled.Manifest.Files);
        Assert.True(File.Exists(Path.Combine(compiled.Directory, pdbName)));
        using var plugin = PluginLoader.Load(compiled);
        var rule = plugin.Create<IExtractionRule>(PluginKind.Extraction).Single();
        Assert.Equal("AB", rule.Extract(null!, new ResponseContext { BodyText = "ab" }, new Dictionary<string, string>()).Value);
    }

    [Fact]
    public void Same_source_gives_same_hash()
    {
        var a = ScriptPackageCompiler.Compile(NewPackage(EchoRule), NewCache());
        var b = ScriptPackageCompiler.Compile(NewPackage(EchoRule), NewCache());
        Assert.Equal(a.Sha256, b.Sha256);
    }

    [Fact]
    public void Rejects_pinvoke()
    {
        const string code = """
            public static class Native
            {
                [System.Runtime.InteropServices.DllImport("libc")]
                public static extern int getpid();
            }
            public sealed class EchoRule : IExtractionRule
            {
                public ExtractionResult Extract(RequestContext req, ResponseContext resp, IReadOnlyDictionary<string, string> args) => ExtractionResult.Ok("x");
            }
            """;
        var ex = Assert.Throws<ScriptCompilationException>(() => ScriptPackageCompiler.Compile(NewPackage(code), NewCache()));
        Assert.Contains(ex.Errors, e => e.Contains("non consentit") || e.Contains("error"));
    }

    [Fact]
    public void Rejects_top_level_statements()
    {
        Assert.Throws<ScriptCompilationException>(() => ScriptPackageCompiler.Compile(NewPackage("var x = 1;\n" + EchoRule), NewCache()));
    }

    [Fact]
    public void CompileSingleFile_pdb_checksum_matches_original_source()
    {
        var temp = Directory.CreateTempSubdirectory("single-file-pdb");
        try
        {
            var sourcePath = Path.GetFullPath(Path.Combine(temp.FullName, "ChecksumTest.csx"));
            const string code = """
                public sealed class ChecksumTest : ICodedTest
                {
                    public Task RunAsync(TestContext ctx)
                    {
                        var value = 42;
                        ctx.Set("value", value.ToString());
                        return Task.CompletedTask;
                    }
                }
                """;
            File.WriteAllText(sourcePath, code, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var cache = new PluginCache(Path.Combine(temp.FullName, "cache"));
            var package = ScriptPackageCompiler.CompileSingleFile(
                sourcePath, cache, emitSymbols: true);

            var pdbName = Path.GetFileNameWithoutExtension(package.Manifest.Entry) + ".pdb";
            var pdbPath = Path.Combine(package.Directory, pdbName);
            Assert.True(File.Exists(pdbPath));

            using var pdbStream = File.OpenRead(pdbPath);
            using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
            var reader = provider.GetMetadataReader();
            var documentHandle = reader.Documents.Single(handle =>
                string.Equals(
                    reader.GetString(reader.GetDocument(handle).Name),
                    sourcePath,
                    StringComparison.OrdinalIgnoreCase));
            var document = reader.GetDocument(documentHandle);

            // Verifica algoritmo e checksum del documento sorgente nel Portable PDB.
            Assert.Equal(
                new Guid("ff1816ec-aa5e-4d10-87f7-6f4963833460"),
                reader.GetGuid(document.HashAlgorithm));

            var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(sourcePath)));
            var actualHash = reader.GetBlobBytes(document.Hash);
            Assert.Equal(expectedHash, actualHash);

            // Il PDB deve contenere sia il sorgente originale sia il documento degli using impliciti.
            var documentNames = reader.Documents
                .Select(handle => reader.GetString(reader.GetDocument(handle).Name))
                .ToArray();
            Assert.Contains(sourcePath, documentNames);
            Assert.Contains("<usings>", documentNames);

            // Verifica che la DLL punti proprio al Portable PDB appena generato.
            var assemblyPath = Path.Combine(package.Directory, package.Manifest.Entry);
            using var assemblyStream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(assemblyStream);
            var debugEntries = peReader.ReadDebugDirectory();
            var codeViewEntry = Assert.Single(debugEntries.Where(entry =>
                entry.Type == DebugDirectoryEntryType.CodeView));
            var codeView = peReader.ReadCodeViewDebugDirectoryData(codeViewEntry);
            Assert.EndsWith(pdbName, codeView.Path, StringComparison.OrdinalIgnoreCase);

            // L'identificativo CodeView (GUID + stamp) deve corrispondere all'identificativo del PDB.
            var pdbId = reader.DebugMetadataHeader!.Id;
            Assert.True(pdbId.Length >= 20);
            Assert.Equal(new Guid(pdbId.AsSpan(0, 16)), codeView.Guid);
            Assert.Equal(BitConverter.ToUInt32(pdbId.AsSpan(16, 4)), codeViewEntry.Stamp);

            // Se presente, il record PdbChecksum della DLL deve corrispondere ai byte del PDB.
            var checksumEntry = debugEntries.SingleOrDefault(entry =>
                entry.Type == DebugDirectoryEntryType.PdbChecksum);
            if (checksumEntry.DataSize > 0)
            {
                var checksum = peReader.ReadPdbChecksumDebugDirectoryData(checksumEntry);
                var pdbBytes = File.ReadAllBytes(pdbPath);
                var calculatedPdbHash = checksum.AlgorithmName.ToUpperInvariant() switch
                {
                    "SHA256" => SHA256.HashData(pdbBytes),
                    "SHA1" => SHA1.HashData(pdbBytes),
                    _ => throw new Xunit.Sdk.XunitException(
                        $"Algoritmo checksum PDB non supportato nel test: {checksum.AlgorithmName}")
                };
                Assert.Equal(calculatedPdbHash, checksum.Checksum);
            }
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public void Single_file_becomes_implicit_coded_test_package()
    {
        const string code = """
            public sealed class Custom : ICodedTest
            {
                public Task RunAsync(TestContext ctx) { ctx.Set("fatto", "1"); return Task.CompletedTask; }
            }
            """;
        var path = Path.Combine(Directory.CreateTempSubdirectory("single").FullName, "Custom.csx");
        File.WriteAllText(path, code);

        var pkg = ScriptPackageCompiler.CompileSingleFile(path, NewCache());

        Assert.Equal([PluginKind.CodedTest], pkg.Manifest.Kinds);
        using var plugin = PluginLoader.Load(pkg);
        Assert.Single(plugin.Create<ICodedTest>(PluginKind.CodedTest));
    }
}
