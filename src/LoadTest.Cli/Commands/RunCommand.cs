using LoadTest.Cli.Args;
using LoadTest.Cli.Local;
using LoadTest.Cli.Remote;

namespace LoadTest.Cli.Commands;

public static class RunCommand
{
    private static readonly OptionSpec[] Spec =
    [
        .. Specs.Gate, .. Specs.Output, .. Specs.Remote,
        new("db"), new("name"), new("seed"),
        new("upload", false), new("no-upload", false), new("remote-path"), new("max-upload-mb")
    ];

    public static async Task<int> ExecuteAsync(string[] args, CliContext ctx)
    {
        var p = ArgParser.Parse(args, Spec);
        if (p.Positional.Count != 1) throw new CliException(ExitCodes.Usage, "indicare un solo file *.loadtest.yaml");
        if (p.Flag("upload") && p.Flag("no-upload")) throw new CliException(ExitCodes.Usage, "--upload e --no-upload sono incompatibili");
        var gate = CommonOptions.ParseGate(p);
        var output = CommonOptions.ParseOutput(p);
        var seed = p.GetLong("seed") is { } s ? checked((int)s) : (int?)null;

        Controller.Api.RunSummaryDto summary;
        Func<string> csv;
        if (CommonOptions.ControllerUrl(p, allowEnv: false) is { } url)
            (summary, csv) = await RemoteRunner.RunAsync(p.Positional[0], url, p, seed, output.CsvPath is not null, ctx);
        else
            (summary, csv) = await LocalRunner.RunAsync(p.Positional[0], p.Get("db") ?? "loadtest-results.db", p.Get("name"), seed, output.Quiet || output.JsonPath == "-", ctx);

        return Ci.ResultOutput.Emit(summary, gate, output, csv, ctx.Out, ctx.Err, ctx.Stop.IsCancellationRequested);
    }
}
