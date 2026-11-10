using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading.Channels;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using LoadTest.Agent;
using LoadTest.Agent.Transfer;
using LoadTest.Engine.LoadTesting;
using LoadTest.Plugins;
using LoadTest.Protocol;
using LoadTest.Sdk;
using Microsoft.Extensions.Logging.Abstractions;

namespace LoadTest.Engine.Tests;

internal sealed class MemoryLink : IControllerLink
{
    public Channel<ControllerMsg> ToAgent { get; } = Channel.CreateUnbounded<ControllerMsg>();
    public Channel<AgentMsg> FromAgent { get; } = Channel.CreateUnbounded<AgentMsg>();
    public IAsyncEnumerable<ControllerMsg> ReadAllAsync(CancellationToken ct) => ToAgent.Reader.ReadAllAsync(ct);
    public Task WriteAsync(AgentMsg message, CancellationToken ct) => FromAgent.Writer.WriteAsync(message, ct).AsTask();
    public Task CompleteWritesAsync() { FromAgent.Writer.TryComplete(); return Task.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class AgentTests
{
    private static string TempDir() => Directory.CreateTempSubdirectory("agt").FullName;
    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static byte[] Gzip(byte[] b)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(b);
        return ms.ToArray();
    }

    private static ManifestEntry FileEntry(string path, byte[] content, EntryKind kind = EntryKind.Data) => new()
    {
        Kind = kind, Name = path, Sha256 = Sha(content), Size = content.Length, Path = path, Encoding = ContentEncoding.FileGzip
    };

    private static IEnumerable<PackageChunk> Chunks(string runId, string sha, byte[] data, int size = 7)
    {
        var n = Math.Max(1, (data.Length + size - 1) / size);
        for (var i = 0; i < n; i++)
            yield return new PackageChunk
            {
                RunId = runId, Sha256 = sha, Sequence = i, Last = i == n - 1,
                Data = ByteString.CopyFrom(data, i * size, Math.Min(size, data.Length - i * size))
            };
    }

    // ---------- SafePath ----------

    [Theory]
    [InlineData("../x")]
    [InlineData("/abs")]
    [InlineData("a/../../b")]
    [InlineData("C:\\x")]
    [InlineData("")]
    public void SafePath_rejects_unsafe_paths(string p) => Assert.False(SafePath.IsSafeRelative(p, out _));

    [Fact]
    public void SafePath_normalizes_and_combines_inside_root()
    {
        Assert.True(SafePath.IsSafeRelative("a/b\\c.txt", out var n));
        Assert.Equal("a/b/c.txt", n);
        var root = TempDir();
        Assert.StartsWith(Path.GetFullPath(root), SafePath.Combine(root, "a/b/c.txt"));
        Assert.Throws<InvalidDataException>(() => SafePath.Combine(root, "../evil"));
    }

    // ---------- TransferReceiver ----------

    private static TransferReceiver NewReceiver(AgentCaches caches, params ManifestEntry[] entries) =>
        new(entries, caches.Files, caches.Plugins, caches.TempRoot);

    [Fact]
    public void Receiver_stores_a_gzip_file_after_verifying_hash()
    {
        var caches = new AgentCaches(TempDir());
        var content = "utente;password\nmario;x\n"u8.ToArray();
        var e = FileEntry("data/utenti.csv", content);
        using var rx = NewReceiver(caches, e);

        var done = false;
        foreach (var c in Chunks("r1", e.Sha256, Gzip(content))) done = rx.Accept(c);

        Assert.True(done);
        Assert.Equal(0, rx.Pending);
        Assert.Equal(content, File.ReadAllBytes(caches.Files.PathFor(e.Sha256)));
    }

    [Fact]
    public void Receiver_rejects_wrong_hash_wrong_size_and_bad_sequence()
    {
        var caches = new AgentCaches(TempDir());
        var content = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

        var badHash = FileEntry("a.bin", content);
        badHash.Sha256 = Sha([9, 9, 9]);
        using (var rx = NewReceiver(caches, badHash))
            Assert.Throws<InvalidDataException>(() => Chunks("r1", badHash.Sha256, Gzip(content)).ToList().ForEach(c => rx.Accept(c)));

        var tooBig = FileEntry("b.bin", content);
        tooBig.Size = 3;
        using (var rx = NewReceiver(caches, tooBig))
            Assert.Throws<InvalidDataException>(() => Chunks("r1", tooBig.Sha256, Gzip(content)).ToList().ForEach(c => rx.Accept(c)));

        var ok = FileEntry("c.bin", content);
        using (var rx = NewReceiver(caches, ok))
            Assert.Throws<InvalidDataException>(() => rx.Accept(new PackageChunk { RunId = "r1", Sha256 = ok.Sha256, Sequence = 1, Data = ByteString.CopyFrom(1), Last = true }));
        Assert.False(caches.Files.Contains(badHash.Sha256));
    }

    [Fact]
    public void Receiver_rejects_unrequested_hashes()
    {
        var caches = new AgentCaches(TempDir());
        using var rx = NewReceiver(caches, FileEntry("a.bin", [1, 2, 3]));
        Assert.Throws<InvalidDataException>(() => rx.Accept(new PackageChunk { RunId = "r1", Sha256 = new string('a', 64), Data = ByteString.CopyFrom(1), Last = true }));
    }

    private static string WritePluginFolder(out long totalBytes)
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "plugin.yaml"),
            "name: MioPlugin\nversion: 1.0.0\nsdk: \">=1.0 <2.0\"\nkinds: [ webTest ]\nentry: MioPlugin.dll\nfiles: [ ]\n");
        File.WriteAllText(Path.Combine(dir, "MioPlugin.dll"), "contenuto");
        totalBytes = Directory.GetFiles(dir).Sum(f => new FileInfo(f).Length);
        return dir;
    }

    [Fact]
    public void Receiver_stores_a_zipped_plugin_package_by_package_hash()
    {
        var dir = WritePluginFolder(out var bytes);
        var zip = Path.Combine(TempDir(), "p.zip");
        ZipFile.CreateFromDirectory(dir, zip);
        var e = new ManifestEntry
        {
            Kind = EntryKind.Plugin, Name = "MioPlugin", Sha256 = PluginPackage.Open(dir).Sha256, Size = bytes,
            Path = "plugins/MioPlugin", Encoding = ContentEncoding.PackageZip
        };

        var caches = new AgentCaches(TempDir());
        using var rx = NewReceiver(caches, e);
        var done = false;
        foreach (var c in Chunks("r1", e.Sha256, File.ReadAllBytes(zip), 64)) done = rx.Accept(c);

        Assert.True(done);
        Assert.True(caches.Plugins.Contains(e.Sha256));
    }

    [Fact]
    public void Receiver_rejects_zip_slip()
    {
        var zipPath = Path.Combine(TempDir(), "evil.zip");
        using (var fs = File.Create(zipPath))
        using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(z.CreateEntry("../evil.txt").Open());
            w.Write("x");
        }
        var e = new ManifestEntry
        {
            Kind = EntryKind.Plugin, Name = "x", Sha256 = new string('a', 64), Size = 100, Path = "plugins/x", Encoding = ContentEncoding.PackageZip
        };
        var caches = new AgentCaches(TempDir());
        using var rx = NewReceiver(caches, e);
        Assert.Throws<InvalidDataException>(() => Chunks("r1", e.Sha256, File.ReadAllBytes(zipPath), 4096).ToList().ForEach(c => rx.Accept(c)));
    }

    // ---------- manifest ----------

    [Fact]
    public void Manifest_validation_normalizes_and_rejects_bad_entries()
    {
        var yaml = "version: 1\n"u8.ToArray();
        var good = new PrepareRun { RunId = "run1", LoadTestPath = "t\\run.loadtest.yaml" };
        good.Manifest.Add(FileEntry("t\\run.loadtest.yaml", yaml, EntryKind.LoadTest));
        var entries = RunWorkspace.ValidateManifest(good, new AgentOptions());
        Assert.Equal("t/run.loadtest.yaml", Assert.Single(entries).Path);

        var traversal = new PrepareRun { RunId = "run1", LoadTestPath = "a.yaml" };
        traversal.Manifest.Add(FileEntry("../a.yaml", yaml, EntryKind.LoadTest));
        Assert.Throws<InvalidDataException>(() => RunWorkspace.ValidateManifest(traversal, new AgentOptions()));

        var badId = new PrepareRun { RunId = "../x", LoadTestPath = "a.yaml" };
        badId.Manifest.Add(FileEntry("a.yaml", yaml, EntryKind.LoadTest));
        Assert.Throws<InvalidDataException>(() => RunWorkspace.ValidateManifest(badId, new AgentOptions()));

        var noLoad = new PrepareRun { RunId = "run1", LoadTestPath = "a.yaml" };
        noLoad.Manifest.Add(FileEntry("a.yaml", yaml));
        Assert.Throws<InvalidDataException>(() => RunWorkspace.ValidateManifest(noLoad, new AgentOptions()));
    }

    // ---------- carico ----------

    [Fact]
    public void Coordinator_applies_share_overrides_and_total_cap()
    {
        var st = new LoadState(0, null);
        var t = TimeSpan.FromSeconds(1);

        var half = new LoadCoordinator(["A", "B"], [new ConstantProfile(100), new ConstantProfile(50)], [0, 0], 0.5, 0);
        Assert.Equal(50, half.GetUserCount(0, t, st));
        Assert.Equal(25, half.GetUserCount(1, t, st));

        var capped = new LoadCoordinator(["A", "B"], [new ConstantProfile(100), new ConstantProfile(50)], [0, 0], 0.5, 30);
        capped.GetUserCount(0, t, st); capped.GetUserCount(1, t, st);               // primo tick: il tetto converge dal secondo
        Assert.Equal(20, capped.GetUserCount(0, t, st));
        Assert.Equal(10, capped.GetUserCount(1, t, st));

        Assert.True(half.SetTarget("A", 7));
        Assert.False(half.SetTarget("zzz", 1));
        Assert.Equal(7, half.GetUserCount(0, t, st));
        half.ZeroAll();
        Assert.Equal(0, half.GetUserCount(1, t, st));
    }

    [Fact]
    public void Coordinator_goal_profile_starts_at_scaled_min_users_until_controller_updates()
    {
        var c = new LoadCoordinator(["G"], [null], [10], 0.5, 0);
        Assert.Equal(5, c.GetUserCount(0, TimeSpan.FromSeconds(30), new LoadState(0, 70)));
        c.SetTarget("G", 12);
        Assert.Equal(12, c.GetUserCount(0, TimeSpan.FromSeconds(60), new LoadState(5, 70)));
    }

    [Fact]
    public void ProcStat_parsing_gives_busy_and_total()
    {
        Assert.True(SystemSampler.TryParseProcStat("cpu  100 0 50 800 50 0 0 0 0 0", out var v));
        Assert.Equal((150L, 1000L), v);
        Assert.False(SystemSampler.TryParseProcStat("cpu0 1 2 3 4", out _));
    }

    // ---------- sessione completa in memoria ----------

    private const string LoadYaml = """
        version: 1
        name: t
        runSettings: { warmupSec: 0, iterations: 6, coolDownSec: 2, samplingSec: 1 }
        scenarios:
          - name: S
            thinkProfile: { mode: off }
            testMix: { model: users, tests: [ { test: a.webtest.yaml, percent: 100 } ] }
            loadPattern: { type: constant, users: 2 }
        """;

    private const string WebYaml = """
        version: 1
        name: A
        baseUrl: https://x.it
        steps:
          - name: Home
            request: { method: GET, url: / }
        """;

    private static async Task<AgentMsg> ReadUntil(MemoryLink l, Func<AgentMsg, bool> match, CancellationToken ct)
    {
        while (true)
        {
            var m = await l.FromAgent.Reader.ReadAsync(ct);
            if (match(m)) return m;
        }
    }

    [Fact]
    public async Task Session_runs_two_runs_end_to_end_and_reuses_the_cache()
    {
        var work = TempDir();
        var link = new MemoryLink();
        var options = new AgentOptions { WorkDirectory = work, HeartbeatSec = 1, Name = "test-agent" };
        var session = new AgentSession(options, "agent-1", link, () => new FakeHttp(), NullLogger.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var sessionTask = session.RunAsync(cts.Token);

        var hello = (await ReadUntil(link, m => m.MCase == AgentMsg.MOneofCase.Hello, cts.Token)).Hello;
        Assert.Equal("agent-1", hello.AgentId);
        Assert.Equal(ProtocolInfo.ProtocolVersion, hello.ProtocolVersion);
        await link.ToAgent.Writer.WriteAsync(new ControllerMsg { Welcome = new Welcome { Accepted = true, ControllerName = "ctrl" } }, cts.Token);

        var load = System.Text.Encoding.UTF8.GetBytes(LoadYaml);
        var web = System.Text.Encoding.UTF8.GetBytes(WebYaml);
        var contents = new Dictionary<string, byte[]>();

        for (var round = 1; round <= 2; round++)
        {
            var runId = "run" + round;
            var prepare = new PrepareRun { RunId = runId, RunName = "t", ControllerName = "ctrl", LoadTestPath = "t/run.loadtest.yaml" };
            var le = FileEntry("t/run.loadtest.yaml", load, EntryKind.LoadTest);
            var we = FileEntry("t/a.webtest.yaml", web, EntryKind.WebTest);
            contents[le.Sha256] = load; contents[we.Sha256] = web;
            prepare.Manifest.Add(le); prepare.Manifest.Add(we);
            await link.ToAgent.Writer.WriteAsync(new ControllerMsg { Prepare = prepare }, cts.Token);

            var next = await ReadUntil(link, m => m.MCase is AgentMsg.MOneofCase.Need or AgentMsg.MOneofCase.Ready, cts.Token);
            if (round == 1)
            {
                Assert.Equal(AgentMsg.MOneofCase.Need, next.MCase);
                Assert.Equal(2, next.Need.Sha256.Count);
                foreach (var sha in next.Need.Sha256)
                    foreach (var c in Chunks(runId, sha, Gzip(contents[sha]), 16))
                        await link.ToAgent.Writer.WriteAsync(new ControllerMsg { Chunk = c }, cts.Token);
                next = await ReadUntil(link, m => m.MCase == AgentMsg.MOneofCase.Ready, cts.Token);
            }
            else Assert.Equal(AgentMsg.MOneofCase.Ready, next.MCase);   // tutto in cache: nessun PackageRequest
            Assert.True(next.Ready.Ok, next.Ready.Error);

            await link.ToAgent.Writer.WriteAsync(new ControllerMsg
            {
                Start = new StartRun { RunId = runId, StartAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow), UserShare = 1.0, Seed = 1 }
            }, cts.Token);

            long requests = 0;
            AgentMsg last;
            while ((last = await ReadUntil(link, m => m.MCase is AgentMsg.MOneofCase.Metrics or AgentMsg.MOneofCase.Finished, cts.Token)).MCase == AgentMsg.MOneofCase.Metrics)
                requests += last.Metrics.Series.Where(s => s.Key.HasPage).Sum(s => s.Count);

            Assert.Equal(RunStatus.Completed, last.Finished.Status);
            Assert.Equal(6, last.Finished.TestsCompleted);
            Assert.Equal(6, requests);
            Assert.False(Directory.Exists(Path.Combine(work, "runs", runId)));   // cartella di lavoro ripulita
        }

        link.ToAgent.Writer.Complete();
        await sessionTask;
    }

    [Fact]
    public async Task Session_reports_a_failed_preparation_and_rejection_throws()
    {
        var link = new MemoryLink();
        var session = new AgentSession(new AgentOptions { WorkDirectory = TempDir() }, "a", link, () => new FakeHttp(), NullLogger.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var task = session.RunAsync(cts.Token);
        await ReadUntil(link, m => m.MCase == AgentMsg.MOneofCase.Hello, cts.Token);
        await link.ToAgent.Writer.WriteAsync(new ControllerMsg { Welcome = new Welcome { Accepted = true } }, cts.Token);

        var bad = new PrepareRun { RunId = "r1", LoadTestPath = "../x.yaml" };
        bad.Manifest.Add(FileEntry("../x.yaml", [1], EntryKind.LoadTest));
        await link.ToAgent.Writer.WriteAsync(new ControllerMsg { Prepare = bad }, cts.Token);
        var ready = await ReadUntil(link, m => m.MCase == AgentMsg.MOneofCase.Ready, cts.Token);
        Assert.False(ready.Ready.Ok);
        Assert.False(string.IsNullOrEmpty(ready.Ready.Error));
        link.ToAgent.Writer.Complete();
        await task;

        var link2 = new MemoryLink();
        var s2 = new AgentSession(new AgentOptions { WorkDirectory = TempDir() }, "a", link2, () => new FakeHttp(), NullLogger.Instance);
        var t2 = s2.RunAsync(cts.Token);
        await link2.ToAgent.Writer.WriteAsync(new ControllerMsg { Welcome = new Welcome { Accepted = false, RejectReason = "sdk" } }, cts.Token);
        await Assert.ThrowsAsync<AgentRejectedException>(() => t2);
    }
}
