using LoadTest.Cli.Args;
using LoadTest.Cli.Ci;

namespace LoadTest.Cli.Commands;

internal static class Specs
{
    public static readonly OptionSpec[] Gate =
    [
        new("max-error-rate"), new("max-p95-ms"), new("max-p99-ms"), new("min-rps"), new("min-requests"), new("max-failed-tests")
    ];
    public static readonly OptionSpec[] Output = [new("json"), new("csv"), new("junit"), new("quiet", false), new("top")];
    public static readonly OptionSpec[] Remote = [new("controller"), new("token")];
}

internal static class CommonOptions
{
    public static GateOptions ParseGate(ParsedArgs p) => new()
    {
        MaxErrorRatePercent = p.GetDouble("max-error-rate"),
        MaxP95Ms = p.GetDouble("max-p95-ms"),
        MaxP99Ms = p.GetDouble("max-p99-ms"),
        MinRequestsPerSecond = p.GetDouble("min-rps"),
        MinRequests = p.GetLong("min-requests"),
        MaxFailedTests = p.GetLong("max-failed-tests")
    };

    public static OutputOptions ParseOutput(ParsedArgs p) => new()
    {
        JsonPath = p.Get("json"), CsvPath = p.Get("csv"), JunitPath = p.Get("junit"),
        Quiet = p.Flag("quiet"), Top = (int)(p.GetLong("top") ?? 30)
    };

    /// <summary>URL del controller: --controller, altrimenti LOADTEST_CONTROLLER solo se <paramref name="allowEnv"/> (per `run` serve l'opzione esplicita).</summary>
    public static string? ControllerUrl(ParsedArgs p, bool allowEnv) =>
        p.Get("controller") ?? (allowEnv ? Environment.GetEnvironmentVariable("LOADTEST_CONTROLLER") : null);

    public static string? Token(ParsedArgs p) => p.Get("token") ?? Environment.GetEnvironmentVariable("LOADTEST_TOKEN");
}
