// Segnaposto minimi per i tipi citati da ILoadTestEvents / IDataSource / ILoadProfile.
// Proprietà reali da decidere con le chat 3 (load test) e 6 (controller) -> PROPOSTA-CONTRATTO.
namespace LoadTest.Sdk;

public sealed record LoadTestInfo(string Name, string RunId);
public sealed record TestEventArgs(string TestName, string ScenarioName, int UserId);
public sealed class TestSelectedArgs
{
    public required string ScenarioName { get; init; }
    public required int UserId { get; init; }
    public required string SelectedTest { get; set; }       // modificabile dai plugin
}
public sealed record HeartbeatArgs(DateTimeOffset UtcNow, int ActiveUsers);
public sealed record ThresholdArgs(string Counter, string Severity, double Value, double Limit);
public sealed record LoadState(int CurrentUsers, double? GoalCounterValue);

public enum DataAccessMode { Sequential, Random, Unique }
public sealed record DataSourceConfig(string Name, string Type, string? Path, DataAccessMode Access, IReadOnlyDictionary<string, string> Options);
public sealed record PartitionInfo(int Index, int Count);
