using LoadTest.Engine.Model;
using LoadTest.Plugins;
using LoadTest.Plugins.Compilation;
using LoadTest.Sdk;

namespace LoadTest.Engine.Tests;

public class PluginSigningTests
{
    private const string Manifest = """
        name: MioPlugin
        version: 1.0.0
        sdk: ">=1.0 <2.0"
        kinds: [ webTest ]
        entry: MioPlugin.dll
        files: [ ]
        """;

    private static PluginPackage NewPackage(string dll = "contenuto")
    {
        var d = Directory.CreateTempSubdirectory("sig").FullName;
        File.WriteAllText(Path.Combine(d, "plugin.yaml"), Manifest);
        File.WriteAllText(Path.Combine(d, "MioPlugin.dll"), dll);
        return PluginPackage.Open(d);
    }

    [Fact]
    public void Signed_package_verifies_with_trusted_key()
    {
        var (priv, pub) = PluginSigning.GenerateKeyPair();
        var pkg = NewPackage();
        var sig = PluginSigning.SignPackage(pkg, priv);

        Assert.Equal(PluginSigning.KeyIdOf(pub), sig.KeyId);
        new PluginTrust([pub], SignaturePolicy.Required).Check(pkg);
    }

    [Fact]
    public void Signature_does_not_change_the_package_hash()
    {
        var (priv, _) = PluginSigning.GenerateKeyPair();
        var pkg = NewPackage();
        PluginSigning.SignPackage(pkg, priv);
        Assert.Equal(pkg.Sha256, PluginPackage.Open(pkg.Directory).Sha256);
    }

    [Fact]
    public void Signature_of_another_package_is_rejected()
    {
        var (priv, pub) = PluginSigning.GenerateKeyPair();
        var a = NewPackage("a");
        var b = NewPackage("b");
        PluginSigning.SignPackage(a, priv);
        File.Copy(Path.Combine(a.Directory, PluginSignature.FileName), Path.Combine(b.Directory, PluginSignature.FileName));

        Assert.Throws<PluginLoadException>(() => new PluginTrust([pub], SignaturePolicy.Required).Check(b));
    }

    [Fact]
    public void Untrusted_key_is_rejected()
    {
        var (priv, _) = PluginSigning.GenerateKeyPair();
        var (_, otherPub) = PluginSigning.GenerateKeyPair();
        var pkg = NewPackage();
        PluginSigning.SignPackage(pkg, priv);

        Assert.Throws<PluginLoadException>(() => new PluginTrust([otherPub], SignaturePolicy.IfPresent).Check(pkg));
    }

    [Fact]
    public void Policy_decides_what_happens_to_unsigned_packages()
    {
        var (_, pub) = PluginSigning.GenerateKeyPair();
        var pkg = NewPackage();

        Assert.Throws<PluginLoadException>(() => new PluginTrust([pub], SignaturePolicy.Required).Check(pkg));
        new PluginTrust([pub], SignaturePolicy.IfPresent).Check(pkg);
        new PluginTrust([pub], SignaturePolicy.Off).Check(pkg);
    }

    [Fact]
    public void Catalog_from_cache_enforces_the_policy()
    {
        var (priv, pub) = PluginSigning.GenerateKeyPair();
        var cache = new PluginCache(Directory.CreateTempSubdirectory("cache").FullName);
        var signed = NewPackage("firmato");
        PluginSigning.SignPackage(signed, priv);
        var unsigned = NewPackage("non firmato");
        var cachedSigned = cache.Add(signed);
        var cachedUnsigned = cache.Add(unsigned);
        var trust = new PluginTrust([pub], SignaturePolicy.Required);

        Assert.Single(PluginCatalog.FromCache(cache, [cachedSigned.Sha256], trust).Packages);
        Assert.Throws<PluginLoadException>(() => PluginCatalog.FromCache(cache, [cachedUnsigned.Sha256], trust));
    }

    [Fact]
    public void Compiler_signs_the_compiled_package()
    {
        var (priv, pub) = PluginSigning.GenerateKeyPair();
        var path = Path.Combine(Directory.CreateTempSubdirectory("single").FullName, "Custom.csx");
        File.WriteAllText(path, """
            public sealed class Custom : ICodedTest
            {
                public Task RunAsync(TestContext ctx) => Task.CompletedTask;
            }
            """);
        var cache = new PluginCache(Directory.CreateTempSubdirectory("cache").FullName);

        var pkg = ScriptPackageCompiler.CompileSingleFile(path, cache, signingPrivateKey: priv);

        new PluginTrust([pub], SignaturePolicy.Required).Check(pkg);
    }
}

public class ExamplePluginTests
{
    // Stesso codice di examples/plugins/HeaderPlugin/HeaderPlugin.csx
    private const string Code = """
        public sealed class AddHeader : IRequestPlugin
        {
            private readonly string _name;
            private readonly string _value;

            public AddHeader(IReadOnlyDictionary<string, string> args)
            {
                _name = args.TryGetValue("name", out var n) ? n : "X-Load-Test";
                _value = args.TryGetValue("value", out var v) ? v : "1";
            }

            public void PreRequestDataBinding(RequestContext ctx) { }
            public void PreRequest(RequestContext ctx) => ctx.Headers[_name] = _value;
            public void PostRequest(RequestContext ctx, ResponseContext resp) { }
            public void PostRequestDataBinding(RequestContext ctx, ResponseContext resp) { }
        }
        """;

    [Fact]
    public void Params_reach_the_plugin_constructor()
    {
        var root = Directory.CreateTempSubdirectory("ex").FullName;
        var d = Path.Combine(root, "HeaderPlugin");
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "plugin.yaml"),
            "name: HeaderPlugin\nversion: 1.0.0\nsdk: \">=1.0 <2.0\"\nkinds: [ request ]\nentry: HeaderPlugin.csx\nfiles: [ ]\n");
        File.WriteAllText(Path.Combine(d, "HeaderPlugin.csx"), Code);

        var cache = new PluginCache(Directory.CreateTempSubdirectory("cache").FullName);
        using var host = new PluginHost(PluginCatalog.FromDirectory(root, p => ScriptPackageCompiler.Compile(p, cache)));

        var plugin = host.Create<IRequestPlugin>(PluginKind.Request,
            new PluginRef { Name = "HeaderPlugin", Params = new() { ["name"] = "X-Prova", ["value"] = "9" } }).Single();

        var ctx = new RequestContext
        {
            StepName = "s",
            WebTest = new WebTestContext { TestName = "t", User = new UserContext { UserId = 1 } }
        };
        plugin.PreRequest(ctx);
        Assert.Equal("9", ctx.Headers["X-Prova"]);
    }
}
