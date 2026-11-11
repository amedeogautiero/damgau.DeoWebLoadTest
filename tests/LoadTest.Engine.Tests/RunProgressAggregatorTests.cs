using LoadTest.Contracts;
using LoadTest.Metrics;

namespace LoadTest.Engine.Tests;

public sealed class RunProgressAggregatorTests
{
    [Fact]
    public void CreateSnapshot_aggregates_intervals_and_merges_histograms()
    {
        var started = DateTimeOffset.Parse("2026-11-11T10:00:00Z");
        var aggregator = new RunProgressAggregator("run-123");

        aggregator.AddInterval(CreateInterval(
            0, started, started.AddSeconds(5), activeUsers: 3,
            requestCount: 10, requestFailures: 1, testCount: 2, testFailures: 0,
            responseTimeMicros: 100_000));

        aggregator.AddInterval(CreateInterval(
            1, started.AddSeconds(5), started.AddSeconds(10), activeUsers: 4,
            requestCount: 20, requestFailures: 2, testCount: 4, testFailures: 1,
            responseTimeMicros: 200_000));

        var snapshot = aggregator.CreateSnapshot(
            RunProgressStatus.Running, started, started.AddSeconds(10));

        Assert.Equal("run-123", snapshot.RunId);
        Assert.Equal(1, snapshot.Sequence);
        Assert.Equal(RunProgressStatus.Running, snapshot.Status);
        Assert.Equal(10, snapshot.ElapsedSeconds);
        Assert.Equal(4, snapshot.ActiveUsers);
        Assert.Equal(6, snapshot.CompletedTests);
        Assert.Equal(1, snapshot.FailedTests);
        Assert.Equal(30, snapshot.CompletedRequests);
        Assert.Equal(3, snapshot.FailedRequests);
        Assert.Equal(3, snapshot.RequestsPerSecond);

        var request = Assert.Single(snapshot.Series.Where(x => x.Kind == RunProgressSeriesKind.Request));
        Assert.Equal(30, request.Count);
        Assert.Equal(3, request.Failed);
        Assert.Equal(10, request.ErrorRatePercent, 5);
        Assert.Equal(3, request.OperationsPerSecond);
        Assert.True(request.P50Milliseconds > 0);
        Assert.True(request.P95Milliseconds >= request.P50Milliseconds);
        Assert.True(request.MaxMilliseconds >= request.P95Milliseconds);
    }

    [Fact]
    public void CreateSnapshot_increments_sequence_and_handles_empty_intervals()
    {
        var aggregator = new RunProgressAggregator("empty-run");
        var first = aggregator.CreateSnapshot(RunProgressStatus.Preparing);
        var second = aggregator.CreateSnapshot(RunProgressStatus.Running);

        Assert.Equal(1, first.Sequence);
        Assert.Equal(2, second.Sequence);
        Assert.Empty(second.Series);
        Assert.Equal(0, second.RequestsPerSecond);
        Assert.Equal(0, second.CompletedRequests);
    }

    private static IntervalSnapshot CreateInterval(
        int index,
        DateTimeOffset start,
        DateTimeOffset end,
        int activeUsers,
        long requestCount,
        long requestFailures,
        long testCount,
        long testFailures,
        long responseTimeMicros)
    {
        var requestHistogram = HistogramSettings.Create();
        for (var i = 0; i < requestCount; i++)
            requestHistogram.RecordValue(responseTimeMicros);

        var testHistogram = HistogramSettings.Create();
        for (var i = 0; i < testCount; i++)
            testHistogram.RecordValue(responseTimeMicros * 2);

        return new IntervalSnapshot
        {
            Index = index,
            StartUtc = start,
            EndUtc = end,
            ActiveUsers = activeUsers,
            Series = new Dictionary<MetricKey, SeriesSnapshot>
            {
                [new MetricKey("Scenario A", "Test A", "Home", "GET /")] = new()
                {
                    Histogram = requestHistogram,
                    Count = requestCount,
                    Failed = requestFailures,
                    BytesSent = requestCount * 10,
                    BytesReceived = requestCount * 100
                },
                [new MetricKey("Scenario A", "Test A")] = new()
                {
                    Histogram = testHistogram,
                    Count = testCount,
                    Failed = testFailures
                }
            }
        };
    }
}
