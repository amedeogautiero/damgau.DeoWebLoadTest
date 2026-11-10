using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using LoadTest.Metrics;
using LoadTest.Protocol;

namespace LoadTest.Engine.Tests;

public class ProtocolTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly Version ControllerSdk = new(1, 1, 0, 0);

    private static Hello GoodHello(string sdk = "1.2.0.0", int protocol = ProtocolInfo.ProtocolVersion) => new()
    {
        AgentId = "a1", Name = "pi-1", ProtocolVersion = protocol, SdkVersion = sdk, Rid = "linux-arm64",
        Os = "linux", Cores = 4, RamMb = 4096, MaxUsers = 40, UtcNow = Timestamp.FromDateTimeOffset(T0)
    };

    [Fact]
    public void Handshake_accepts_compatible_agent_and_estimates_clock_offset()
    {
        var w = Handshake.Evaluate(GoodHello(), "ctrl", ControllerSdk, T0.AddSeconds(2));
        Assert.True(w.Accepted, w.RejectReason);
        Assert.Equal(2000, w.ClockOffsetMs);     // agente in ritardo di 2 s
        Assert.Equal("ctrl", w.ControllerName);
    }

    [Theory]
    [InlineData("1.0.0.0", 1)]    // SDK più vecchio del controller
    [InlineData("2.0.0.0", 1)]    // major diverso
    [InlineData("boh", 1)]        // non parsabile
    [InlineData("1.2.0.0", 2)]    // protocol_version diversa
    public void Handshake_rejects_incompatible_agent(string sdk, int protocol)
    {
        var w = Handshake.Evaluate(GoodHello(sdk, protocol), "ctrl", ControllerSdk, T0);
        Assert.False(w.Accepted);
        Assert.False(string.IsNullOrEmpty(w.RejectReason));
    }

    [Fact]
    public void Handshake_rejects_missing_fields()
    {
        var h = GoodHello();
        h.AgentId = "";
        Assert.False(Handshake.Evaluate(h, "c", ControllerSdk, T0).Accepted);
        var noClock = GoodHello();
        noClock.UtcNow = null;
        Assert.False(Handshake.Evaluate(noClock, "c", ControllerSdk, T0).Accepted);
    }

    [Fact]
    public void Envelopes_roundtrip_through_bytes()
    {
        var a = AgentMsg.Parser.ParseFrom(new AgentMsg { Hello = GoodHello() }.ToByteArray());
        Assert.Equal(AgentMsg.MOneofCase.Hello, a.MCase);
        Assert.Equal("pi-1", a.Hello.Name);

        var c = ControllerMsg.Parser.ParseFrom(new ControllerMsg
        {
            Start = new StartRun { RunId = "r1", StartAtUtc = Timestamp.FromDateTimeOffset(T0), UserShare = 0.25, MaxUsers = 40, Seed = 7, UserIdBase = 1000 }
        }.ToByteArray());
        Assert.Equal(ControllerMsg.MOneofCase.Start, c.MCase);
        Assert.Equal(0.25, c.Start.UserShare);
        Assert.Equal(T0, c.Start.StartAtUtc.ToDateTimeOffset());
    }

    [Fact]
    public void Metrics_batch_roundtrips_keys_counts_errors_and_percentiles()
    {
        var request = new MetricKey("Utenti", "ricerca", Page: "Home", Request: "https://x.it/");
        var tx = new MetricKey("Utenti", "ricerca", Transaction: "Auth");
        var test = new MetricKey("Utenti", "ricerca");
        var emptyPage = new MetricKey("Utenti", "ricerca", Page: "");   // pagina vuota ≠ pagina assente

        var rec = new MetricsRecorder();
        rec.Record(request, 1000, true, null, 100, 2000);
        rec.Record(request, 9000, false, "Timeout", 100, 0);
        rec.Record(request, 250_000, true, null, 100, 5000);
        rec.Record(tx, 3000, true);
        rec.Record(test, 5000, true);
        rec.Record(emptyPage, 500, true);
        var snap = rec.Snapshot(3, T0, T0.AddSeconds(5), 12);

        var wire = MetricsBatch.Parser.ParseFrom(MetricsWire.ToBatch("r1", snap).ToByteArray());
        var back = MetricsWire.FromBatch(wire);

        Assert.Equal(3, back.Index);
        Assert.Equal(12, back.ActiveUsers);
        Assert.Equal(T0, back.StartUtc);
        Assert.Equal(T0.AddSeconds(5), back.EndUtc);
        Assert.Equal(snap.Series.Keys.ToHashSet(), back.Series.Keys.ToHashSet());
        Assert.Contains(emptyPage, back.Series.Keys);

        var a = snap.Series[request];
        var b = back.Series[request];
        Assert.Equal(a.Count, b.Count);
        Assert.Equal(a.Failed, b.Failed);
        Assert.Equal(a.BytesSent, b.BytesSent);
        Assert.Equal(a.BytesReceived, b.BytesReceived);
        Assert.Equal(1, b.ErrorsByType["Timeout"]);
        Assert.Equal(a.Histogram.TotalCount, b.Histogram.TotalCount);
        foreach (var p in new[] { 50.0, 90.0, 99.0 })
            Assert.Equal(a.Histogram.GetValueAtPercentile(p), b.Histogram.GetValueAtPercentile(p));
    }

    [Fact]
    public void Series_without_histogram_travel_without_bytes()
    {
        var rec = new MetricsRecorder(recordHistograms: false);
        rec.Record(new MetricKey("S", "T"), 1000, true);
        var batch = MetricsWire.ToBatch("r1", rec.Snapshot(0, T0, T0.AddSeconds(5), 1));

        var sample = Assert.Single(batch.Series);
        Assert.Equal(HistogramFormat.None, sample.HistFormat);
        Assert.True(sample.Hist.IsEmpty);
        Assert.Equal(0, Assert.Single(MetricsWire.FromBatch(batch).Series).Value.Histogram.TotalCount);
    }

    [Fact]
    public void FromBatch_rejects_bad_data()
    {
        var rec = new MetricsRecorder();
        rec.Record(new MetricKey("S", "T"), 1000, true);
        var good = MetricsWire.ToBatch("r1", rec.Snapshot(0, T0, T0.AddSeconds(5), 1));

        var badVersion = good.Clone();
        var bytes = badVersion.Series[0].Hist.ToByteArray();
        bytes[0] = 9;
        badVersion.Series[0].Hist = ByteString.CopyFrom(bytes);
        Assert.Throws<InvalidDataException>(() => MetricsWire.FromBatch(badVersion));

        var reversed = good.Clone();
        reversed.EndUtc = Timestamp.FromDateTimeOffset(T0.AddSeconds(-1));
        Assert.Throws<InvalidDataException>(() => MetricsWire.FromBatch(reversed));

        var dup = good.Clone();
        dup.Series.Add(good.Series[0].Clone());
        Assert.Throws<InvalidDataException>(() => MetricsWire.FromBatch(dup));

        var v2 = good.Clone();
        v2.Series[0].HistFormat = HistogramFormat.HdrV2Compressed;
        Assert.Throws<NotSupportedException>(() => MetricsWire.FromBatch(v2));
    }
}
