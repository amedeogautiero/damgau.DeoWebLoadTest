using System.Globalization;
using LoadTest.Reports.Analysis;

namespace LoadTest.Reports.Export;

/// <summary>CSV del riassunto per serie. Separatore ',', numeri in cultura invariante (punto decimale), righe CRLF, tempi in millisecondi.</summary>
public static class CsvExporter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static void WriteSummary(TextWriter w, RunSummary summary)
    {
        w.Write("scenario,test,kind,page,request,transaction,count,failed,error_rate_pct,per_second,mean_ms,p50_ms,p90_ms,p95_ms,p99_ms,max_ms,bytes_sent,bytes_received,agent_saturated,errors_by_type\r\n");
        foreach (var s in summary.Series)
        {
            var errors = string.Join(";", s.ErrorsByType.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key}={e.Value.ToString(Inv)}"));
            var cells = new[]
            {
                Q(s.Key.Scenario), Q(s.Key.Test), s.Key.Kind.ToString(), Q(s.Key.Page), Q(s.Key.Request), Q(s.Key.Transaction),
                s.Count.ToString(Inv), s.Failed.ToString(Inv), N(s.ErrorRatePercent), N(s.PerSecond),
                N(s.MeanMs), N(s.P50Ms), N(s.P90Ms), N(s.P95Ms), N(s.P99Ms), N(s.MaxMs),
                s.BytesSent.ToString(Inv), s.BytesReceived.ToString(Inv), s.AgentSaturated ? "true" : "false", Q(errors)
            };
            w.Write(string.Join(",", cells));
            w.Write("\r\n");
        }
    }

    private static string N(double? v) => v is null ? "" : v.Value.ToString("0.###", Inv);

    /// <summary>Quota i campi con , " o a capo; neutralizza l'avvio di formula (= + - @) per l'apertura in Excel.</summary>
    private static string Q(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        if (v[0] is '=' or '+' or '-' or '@') v = "'" + v;
        return v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}
