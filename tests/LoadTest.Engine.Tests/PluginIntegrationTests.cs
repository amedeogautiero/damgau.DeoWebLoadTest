using LoadTest.Engine.Model;
using LoadTest.Engine.Runtime;
using LoadTest.Plugins;
using LoadTest.Plugins.Compilation;
using LoadTest.Sdk;

namespace LoadTest.Engine.Tests;

public class PluginIntegrationTests
{
    private static WebTestContext NewCtx() => new()
    {
        TestName = "t", User = new UserContext { UserId = 1, ScenarioName = "s" }
    };

    private static string NewDir() => Directory.CreateTempSubdirectory("plgint").FullName;

    private static void WritePackage(string pluginsRoot, string name, string kinds, string code)
    {
        var d = Path.Combine(pluginsRoot, name);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "plugin.yaml"),
            $"name: {name}\nversion: 1.0.0\nsdk: \">=1.0 <2.0\"\nkinds: [ {kinds} ]\nentry: {name}.csx\nfiles: [ ]\n");
        File.WriteAllText(Path.Combine(d, name + ".csx"), code);
    }

    private static PluginCatalog NewCatalog(string root)
    {
        var cache = new PluginCache(Directory.CreateTempSubdirectory("cache").FullName);
        return PluginCatalog.FromDirectory(root, p => ScriptPackageCompiler.Compile(p, cache));
    }

    private const string DemoCode = """
        public sealed class AddHeader : IRequestPlugin
        {
            public void PreRequestDataBinding(RequestContext ctx) { }
            public void PreRequest(RequestContext ctx) => ctx.Headers["X-Demo"] = "1";
            public void PostRequest(RequestContext ctx, ResponseContext resp) { }
            public void PostRequestDataBinding(RequestContext ctx, ResponseContext resp) { }
        }
        public sealed class Upper : IExtractionRule
        {
            public ExtractionResult Extract(RequestContext req, ResponseContext resp, IReadOnlyDictionary<string, string> args)
                => ExtractionResult.Ok(resp.BodyText.ToUpperInvariant());
        }
        """;

    [Fact]
    public async Task Request_plugin_and_custom_rules_are_wired_into_the_runner()
    {
        var root = NewDir();
        WritePackage(root, "Demo", "request, extraction", DemoCode);
        using var host = new PluginHost(NewCatalog(root));

        var def = WebTestLoader.Load(new StringReader("""
            version: 1
            baseUrl: https://x.it
            plugins:
              request: [ { name: Demo } ]
            steps:
              - name: A
                request: { method: GET, url: / }
                extract:
                  - { name: up1, type: custom, rule: Demo/Upper }
                  - { name: up2, type: custom, rule: Upper }
                  - { name: up3, type: custom, rule: Demo }
            """));

        var http = new FakeHttp { Handler = _ => (200, "ciao") };
        var wiring = PluginWiring.Build(host, [def]);
        var ctx = NewCtx();
        var result = await new WebTestRunner(wiring.CreateServices(http)).RunAsync(def, ctx);

        Assert.True(result.Success, string.Join(" | ", result.Failures));
        Assert.Equal("1", http.Sent[0].Headers["X-Demo"]);
        Assert.Equal("CIAO", ctx.Get("up1"));
        Assert.Equal("CIAO", ctx.Get("up2"));
        Assert.Equal("CIAO", ctx.Get("up3"));
    }

    [Fact]
    public async Task Code_step_runs_a_single_csx_file()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "Custom.csx"), """
            public sealed class Custom : ICodedTest
            {
                public Task RunAsync(TestContext ctx) { ctx.Set("fatto", "1"); return Task.CompletedTask; }
            }
            """);
        var cache = new PluginCache(Directory.CreateTempSubdirectory("cache").FullName);
        using var host = new PluginHost(new PluginCatalog());

        var def = WebTestLoader.Load(new StringReader("version: 1\nsteps:\n  - code: Custom.csx\n"));
        def.SourcePath = Path.Combine(dir, "t.webtest.yaml");

        var wiring = PluginWiring.Build(host, [def], compileSingleScript: p => ScriptPackageCompiler.CompileSingleFile(p, cache));
        var ctx = NewCtx();
        var result = await new WebTestRunner(wiring.CreateServices(new FakeHttp())).RunAsync(def, ctx);

        Assert.True(result.Success, string.Join(" | ", result.Failures));
        Assert.Equal("1", ctx.Get("fatto"));
    }

    [Fact]
    public void Custom_load_profile_is_resolved_by_name()
    {
        var root = NewDir();
        WritePackage(root, "Prof", "loadProfile", """
            public sealed class Flat : ILoadProfile
            {
                public int GetUserCount(TimeSpan elapsed, LoadState state) => 7;
            }
            """);
        using var host = new PluginHost(NewCatalog(root));
        var wiring = PluginWiring.Build(host, []);

        Assert.Equal(7, wiring.CustomProfileResolver("Prof")!.GetUserCount(TimeSpan.Zero, new LoadState(0, null)));
        Assert.Equal(7, wiring.CustomProfileResolver("Prof/Flat")!.GetUserCount(TimeSpan.Zero, new LoadState(0, null)));
        Assert.Null(wiring.CustomProfileResolver("Inesistente"));
    }

    [Fact]
    public void Unknown_plugin_reference_fails_with_a_clear_error()
    {
        using var host = new PluginHost(new PluginCatalog());
        var def = WebTestLoader.Load(new StringReader("version: 1\nplugins:\n  request: [ { name: Boh } ]\nsteps: []\n"));
        var ex = Assert.Throws<PluginLoadException>(() => PluginWiring.Build(host, [def]));
        Assert.Contains("Boh", ex.Message);
    }

    [Fact]
    public void Directory_catalog_rejects_scripts_without_a_compiler_and_duplicate_names()
    {
        var root = NewDir();
        WritePackage(root, "Demo", "extraction", """
            public sealed class Upper : IExtractionRule
            {
                public ExtractionResult Extract(RequestContext req, ResponseContext resp, IReadOnlyDictionary<string, string> args) => ExtractionResult.Ok("x");
            }
            """);
        Assert.Throws<PluginLoadException>(() => PluginCatalog.FromDirectory(root));

        var catalog = NewCatalog(root);
        Assert.Throws<PluginLoadException>(() => catalog.Add(catalog.Get("Demo")));
    }
}
