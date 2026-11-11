using LoadTest.Cli.Args;
using LoadTest.Controller.Api;
using LoadTest.Reports.Analysis;
using LoadTest.Reports.Export;
using LoadTest.Reports.Storage;

namespace LoadTest.Cli.Commands;

public static class ReportCommand
{
    private static readonly OptionSpec[] Spec = [.. Specs.Gate, .. Specs.Output, new("db"), new("run")];

    public static int Execute(string[] args, CliContext ctx)
    {
        var p = ArgParser.Parse(args, Spec);
        var db = p.Get("db") ?? throw new CliException(ExitCodes.Usage, "manca --db <file>");
        if (!File.Exists(db)) throw new CliException(ExitCodes.Usage, $"database non trovato: {db}");

        using var repo = new SqliteResultsRepository(db);
        var runs = repo.ListRuns();
        var info = p.Get("run") is { } id
            ? repo.GetRun(id) ?? throw new CliException(ExitCodes.Usage, $"run '{id}' non trovata in {db}")
            : runs.FirstOrDefault() ?? throw new CliException(ExitCodes.Usage, $"nessuna run in {db}");

        var summary = RunAnalyzer.Summarize(repo.ReadSamples(info.RunId));
        var dto = SummaryMapper.From(info, summary);
        var csv = new StringWriter();
        CsvExporter.WriteSummary(csv, summary);
        return Ci.ResultOutput.Emit(dto, CommonOptions.ParseGate(p), CommonOptions.ParseOutput(p), csv.ToString, ctx.Out, ctx.Err, interrupted: false);
    }
}
