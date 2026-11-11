using System.Globalization;
using LoadTest.Contracts;

namespace LoadTest.Cli.Live;

/// <summary>
/// Renderizza gli snapshot di avanzamento su una console interattiva.
/// Non conosce runner, controller o trasporto: può essere riutilizzato da altri consumer.
/// </summary>
public sealed class CliLiveRenderer : IDisposable
{
    private readonly TextWriter _output;
    private readonly bool _interactive;
    private readonly object _gate = new();
    private int _renderedLines;
    private bool _disposed;

    public CliLiveRenderer(TextWriter output, bool interactive)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _interactive = interactive;
    }

    public void Render(RunProgressSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!_interactive || _disposed) return;

        var lines = BuildLines(snapshot);
        lock (_gate)
        {
            if (_disposed) return;

            if (_renderedLines > 0)
                _output.Write($"\u001b[{_renderedLines}A");

            foreach (var line in lines)
            {
                _output.Write("\r\u001b[2K");
                _output.WriteLine(line);
            }

            _renderedLines = lines.Count;
            _output.Flush();
        }
    }

    private static List<string> BuildLines(RunProgressSnapshot snapshot)
    {
        var elapsed = TimeSpan.FromSeconds(Math.Max(0, snapshot.ElapsedSeconds));
        var duration = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
            : elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
        var errorRate = snapshot.CompletedRequests > 0
            ? 100.0 * snapshot.FailedRequests / snapshot.CompletedRequests
            : 0;

        var lines = new List<string>
        {
            "LOAD TEST - LIVE",
            $"Stato: {snapshot.Status,-12} Durata: {duration}   Utenti attivi: {snapshot.ActiveUsers}",
            $"Richieste: {snapshot.CompletedRequests,12:N0}   Errori: {snapshot.FailedRequests,8:N0} ({errorRate:0.00}%)",
            $"Test completati: {snapshot.CompletedTests,8:N0}   Test falliti: {snapshot.FailedTests,6:N0}",
            $"Throughput: {snapshot.RequestsPerSecond,9:N2} req/s",
            "",
            "DETTAGLIO RICHIESTE"
        };

        var requests = snapshot.Series
            .Where(x => x.Kind == RunProgressSeriesKind.Request)
            .OrderByDescending(x => x.Count)
            .Take(8)
            .ToArray();

        if (requests.Length == 0)
        {
            lines.Add("Nessuna richiesta misurata.");
        }
        else
        {
            lines.Add($"{"Scenario",-18} {"Test",-18} {"Richiesta",-22} {"Count",8} {"P95",9} {"Errori",8}");
            foreach (var item in requests)
            {
                var requestName = item.RequestName ?? item.PageName ?? item.TransactionName ?? "-";
                lines.Add($"{Clip(item.ScenarioName, 18),-18} {Clip(item.TestName, 18),-18} {Clip(requestName, 22),-22} {item.Count,8:N0} {item.P95Milliseconds,8:N1}ms {item.Failed,8:N0}");
            }
        }

        lines.Add($"Snapshot #{snapshot.Sequence} · aggiornato {snapshot.CapturedUtc.ToLocalTime():HH:mm:ss}");
        return lines;
    }

    private static string Clip(string value, int length) =>
        value.Length <= length ? value : value[..(length - 1)] + "…";

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_interactive) _output.Flush();
        }
    }
}
