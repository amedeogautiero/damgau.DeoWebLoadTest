using LoadTest.Plugins;

namespace LoadTest.Plugins.Tests;

public class PluginsTests
{
    private const string Sample = """
        name: MioPlugin
        version: 1.0.0
        sdk: ">=1.0 <2.0"
        kinds: [ webTest, request ]
        entry: MioPlugin.dll
        files: [ ]
        """;

    private static string NewPackage(string yaml = Sample, string dll = "contenuto")
    {
        var d = Directory.CreateTempSubdirectory("plg").FullName;
        File.WriteAllText(Path.Combine(d, "plugin.yaml"), yaml);
        File.WriteAllText(Path.Combine(d, "MioPlugin.dll"), dll);
        return d;
    }

    [Fact]
    public void Manifest_parses_contract_sample()
    {
        var m = PluginManifest.Load(new StringReader(Sample));
        Assert.Equal("MioPlugin", m.Name);
        Assert.Equal([PluginKind.WebTest, PluginKind.Request], m.Kinds);
        Assert.False(m.IsScript);
        Assert.True(m.IsCompatibleWith(new Version(1, 3, 0)));
        Assert.False(m.IsCompatibleWith(new Version(2, 0, 0)));
    }

    [Fact]
    public void Manifest_reports_all_errors()
    {
        var ex = Assert.Throws<PluginManifestException>(() =>
            PluginManifest.Load(new StringReader("version: x\nsdk: pippo\nkinds: [ boh ]\nentry: a.txt")));
        Assert.Contains("name", ex.Message);
        Assert.Contains("kind 'boh'", ex.Message);
        Assert.Contains("entry", ex.Message);
    }

    [Fact]
    public void Hash_is_stable_and_changes_with_content()
    {
        var a = PluginPackage.Open(NewPackage());
        var b = PluginPackage.Open(NewPackage());
        var c = PluginPackage.Open(NewPackage(dll: "altro"));
        Assert.Equal(a.Sha256, b.Sha256);
        Assert.NotEqual(a.Sha256, c.Sha256);
        Assert.Equal(64, a.Sha256.Length);
    }

    [Fact]
    public void Cache_adds_once_and_reports_missing()
    {
        var cache = new PluginCache(Directory.CreateTempSubdirectory("cache").FullName);
        var pkg = PluginPackage.Open(NewPackage());
        Assert.Equal([pkg.Sha256], cache.Missing([pkg.Sha256, pkg.Sha256]));
        var cached = cache.Add(pkg);
        Assert.Equal(pkg.Sha256, cached.Sha256);
        Assert.Empty(cache.Missing([pkg.Sha256]));
        Assert.Equal(pkg.Sha256, cache.Add(pkg).Sha256);   // idempotente
    }

    [Fact]
    public void Cache_rejects_hash_mismatch()
    {
        var cache = new PluginCache(Directory.CreateTempSubdirectory("cache").FullName);
        Assert.Throws<InvalidDataException>(() => cache.Add(PluginPackage.Open(NewPackage()), new string('a', 64)));
    }
}
