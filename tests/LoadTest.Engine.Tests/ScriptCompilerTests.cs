using LoadTest.Plugins;
using LoadTest.Plugins.Compilation;
using LoadTest.Sdk;

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
        using var temp = Directory.CreateTempSubdirectory("csx-pdb");
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

    [Fact]
    public void Compiles_class_only_script_and_loads_it()
    {
        var compiled = ScriptPackageCompiler.Compile(NewPackage(EchoRule), NewCache());

        Assert.False(compiled.Manifest.IsScript);
        Assert.EndsWith(".dll", compiled.Manifest.Entry);
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
