using System.Text.Json;
using System.Xml.Linq;
using LoadTest.Controller.Api;

namespace LoadTest.Cli.Ci;

public sealed class OutputOptions
{
    public string? JsonPath { get; init; }
    public string? CsvPath { get; init; }
    public string? JunitPath { get; init; }
    public bool Quiet { get; init; }
    public int Top { get; init; } = 30;
}

/// <summary>Riassunto a console, JSON, CSV, JUnit e codice di uscita. Usato da `run` e da `report`.</summary>
public static class ResultOutput
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static int Emit(RunSummaryDto summary, GateOptions gate, OutputOptions output, Func<string> csv, TextWriter stdout, TextWriter stderr, bool interrupted)
    {
        var checks = ThresholdEvaluator.Evaluate(summary, gate);
        var passed = checks.All(c => c.Passed);
        var jsonToStdout = output.JsonPath == "-";

        if (output.JsonPath is { } jp)
        {
            var text = JsonSerializer.Serialize(new { passed, summary, checks }, Json);
            if (jsonToStdout) stdout.WriteLine(text);
            else WriteFile(jp, text);
        }
        if (output.CsvPath is { } cp) WriteFile(cp, csv());
        if (output.JunitPath is { } xp) WriteFile(xp, JUnit(summary, checks));

        if (!jsonToStdout)
        {
            if (!output.Quiet) PrintSummary(summary, output.Top, stdout);
            PrintChecks(checks, stdout, stderr, output.Quiet);
        }
        return interrupted ? ExitCodes.Interrupted : passed ? ExitCodes.Success : ExitCodes.Failed;
    }

    private static void WriteFile(string path, string text)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    public static string JUnit(RunSummaryDto s, IReadOnlyList<CheckResult> checks)
    {
        var suite = new XElement("testsuite",
            new XAttribute("name", s.Name),
            new XAttribute("tests", checks.Count),
            new XAttribute("failures", checks.Count(c => !c.Passed)),
            new XAttribute("time", s.MeasuredSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
        foreach (var c in checks)
        {
            var tc = new XElement("testcase", new XAttribute("classname", "loadtest"), new XAttribute("name", c.Name));
            if (!c.Passed) tc.Add(new XElement("failure", new XAttribute("message", c.Detail)));
            suite.Add(tc);
        }
        return new XDocument(new XElement("testsuites", suite)).ToString();
    }

    private static void PrintChecks(IReadOnlyList<CheckResult> checks, TextWriter stdout, TextWriter stderr, bool quiet)
    {
        foreach (var c in checks)
        {
            if (c.Passed) { if (!quiet) stdout.WriteLine($"  ok     {c.Name}: {c.Detail}"); }
            else stderr.WriteLine($"  FALLITO {c.Name}: {c.Detail}");
        }
    }

    private static void PrintSummary(RunSummaryDto s, int top, TextWriter w)
    {
        w.WriteLine();
        w.WriteLine($"Run '{s.Name}' ({s.RunId}): {s.Status}" + (string.IsNullOrEmpty(s.Reason) ? "" : $" — {s.Reason}"));
        w.WriteLine($"Misurata per {ThresholdEvaluator.F(s.MeasuredSeconds)} s · test completati {s.TestsCompleted}, falliti {s.TestsFailed} · errori di richiesta {s.RequestErrors}");
        foreach (var warning in s.Warnings) w.WriteLine("avviso: " + warning);

        var order = new Dictionary<string, int> { ["Test"] = 0, ["Transaction"] = 1, ["Request"] = 2 };
        var rows = s.Series.OrderBy(x => order.GetValueOrDefault(x.Kind, 9)).ThenByDescending(x => x.Count).Take(Math.Max(1, top)).ToList();
        if (rows.Count == 0) return;

        w.WriteLine();
        w.WriteLine($"{"tipo",-12}{"nome",-40}{"n",9}{"falliti",9}{"err%",8}{"req/s",9}{"p50",9}{"p95",9}{"p99",9}{"max",9}");
        foreach (var x in rows)
        {
            var name = ThresholdEvaluator.Label(x);
            if (name.Length > 38) name = name[..37] + "…";
            w.WriteLine($"{x.Kind,-12}{name,-40}{x.Count,9}{x.Failed,9}{ThresholdEvaluator.F(x.ErrorRatePercent),8}{ThresholdEvaluator.F(x.PerSecond),9}{Ms(x.P50Ms),9}{Ms(x.P95Ms),9}{Ms(x.P99Ms),9}{Ms(x.MaxMs),9}");
        }
        if (s.Series.Count > rows.Count) w.WriteLine($"… altre {s.Series.Count - rows.Count} serie (usa --top o --csv)");
        w.WriteLine();
    }

    private static string Ms(double? v) => v is null ? "-" : ThresholdEvaluator.F(v.Value);
}
