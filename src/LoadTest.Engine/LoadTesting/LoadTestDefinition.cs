using LoadTest.Engine.Model;
using LoadTest.Sdk;

namespace LoadTest.Engine.LoadTesting;

public enum ConnectionModel { PerUser, Pool }
public enum TimingDetail { None, Statistics, All }
public enum ThinkMode { Recorded, Normal, Off }
public enum MixModel { Tests, Users, Pace, Sequential }
public enum LoadPatternType { Constant, Step, Goal, Custom }

/// <summary>Modello di *.loadtest.yaml (CONTRATTO sez. 5). Le sezioni counterSets, thresholds, monitoredMachines e rig sono di altre chat e qui sono ignorate.</summary>
public sealed class LoadTestDefinition
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "";
    public RunSettingsDef RunSettings { get; set; } = new();
    public List<ScenarioDef> Scenarios { get; set; } = [];
    public List<PluginRef> LoadTestPlugins { get; set; } = [];
    public string? SourcePath { get; set; }
}

public sealed class IpSwitchingDef { public bool Enabled { get; set; } public List<string> Addresses { get; set; } = []; }

public sealed class RunSettingsDef
{
    public int WarmupSec { get; set; }
    public int? DurationMin { get; set; }
    public int? Iterations { get; set; }
    public int CoolDownSec { get; set; }
    public int SamplingSec { get; set; } = 5;
    public ConnectionModel ConnectionModel { get; set; } = ConnectionModel.PerUser;
    public int ConnectionPoolSize { get; set; } = 100;
    public ValidationLevel ValidationLevel { get; set; } = ValidationLevel.High;
    public TimingDetail TimingDetail { get; set; } = TimingDetail.Statistics;
    public int MaxErrorDetails { get; set; } = 1000;
    public int? StopOnErrors { get; set; }
    public bool SaveLogOnFailure { get; set; }
    public bool SqlTracing { get; set; }
    public IpSwitchingDef IpSwitching { get; set; } = new();
}

public sealed class ThinkProfileDef
{
    public ThinkMode Mode { get; set; } = ThinkMode.Recorded;
    public double AvgMs { get; set; }
    public double StdDevMs { get; set; }
    public double Scale { get; set; } = 100;
}

public sealed class MixEntryDef { public string Test { get; set; } = ""; public double Percent { get; set; } }

public sealed class TestMixDef
{
    public MixModel Model { get; set; } = MixModel.Tests;
    public List<MixEntryDef> Tests { get; set; } = [];
    public double? TestsPerUserPerHour { get; set; }
}

public sealed class GoalDef
{
    public string Counter { get; set; } = "";
    public double Target { get; set; }
    public double Tolerance { get; set; }
    public int MinUsers { get; set; } = 1;
    public int MaxUsers { get; set; } = 1;
    public int Adjustment { get; set; } = 1;
    public int IntervalSec { get; set; } = 30;
}

public sealed class LoadPatternDef
{
    public LoadPatternType Type { get; set; } = LoadPatternType.Constant;
    public int Users { get; set; }
    public int Start { get; set; }
    public int Max { get; set; }
    public int Step { get; set; }
    public int StepSec { get; set; }
    public GoalDef? Goal { get; set; }
    public string? Plugin { get; set; }
}

public sealed class BrowserEntryDef { public string Browser { get; set; } = ""; public double Percent { get; set; } }

public sealed class NetworkEntryDef
{
    public string Network { get; set; } = "lan";          // lan | dsl | 3g | custom
    public double Percent { get; set; }
    public int? DownKbps { get; set; }
    public int? UpKbps { get; set; }
    public int? LatencyMs { get; set; }
}

public sealed class ScenarioDef
{
    public string Name { get; set; } = "";
    public ThinkProfileDef ThinkProfile { get; set; } = new();
    public TestMixDef TestMix { get; set; } = new();
    public LoadPatternDef LoadPattern { get; set; } = new();
    public double NewUsersPercent { get; set; } = 100;
    public List<BrowserEntryDef> BrowserMix { get; set; } = [];
    public List<NetworkEntryDef> NetworkMix { get; set; } = [];
}
