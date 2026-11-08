namespace LoadTest.Metrics;

public enum MetricKind { Test, Transaction, Request }

/// <summary>Chiave di misura del CONTRATTO sez. 8: (scenario, test, pagina, richiesta, transazione).</summary>
public readonly record struct MetricKey(
    string Scenario, string Test, string? Page = null, string? Request = null, string? Transaction = null)
{
    /// <summary>Test: nessun campo opzionale. Transazione: solo Transaction. Richiesta: Page/Request valorizzati.</summary>
    public MetricKind Kind =>
        Page is not null || Request is not null ? MetricKind.Request
        : Transaction is not null ? MetricKind.Transaction
        : MetricKind.Test;
}
