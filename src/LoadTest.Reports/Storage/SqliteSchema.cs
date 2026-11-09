namespace LoadTest.Reports.Storage;

/// <summary>
/// Schema del repository dei risultati (CONTRATTO sez. 8: runs, intervals, histograms, errors, thresholds, counters, machines).
/// PROPOSTA-CONTRATTO: colonne, tabelle aggiuntive (meta, series, error_counts, annotations) e versione dello schema.
/// </summary>
public static class SqliteSchema
{
    public const int Version = 1;

    public const string Ddl = """
        CREATE TABLE IF NOT EXISTS meta (
          key TEXT PRIMARY KEY,
          value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS runs (
          run_id TEXT PRIMARY KEY,
          name TEXT NOT NULL,
          started_utc TEXT NOT NULL,
          ended_utc TEXT,
          status TEXT NOT NULL,
          abort_reason TEXT,
          definition_yaml TEXT,
          tests_completed INTEGER NOT NULL DEFAULT 0,
          tests_failed INTEGER NOT NULL DEFAULT 0,
          request_errors INTEGER NOT NULL DEFAULT 0,
          warnings_json TEXT
        );

        -- una riga per chiave di misura (sez. 8); le parti assenti sono '' (in SQLite i NULL non rispettano UNIQUE)
        CREATE TABLE IF NOT EXISTS series (
          series_id INTEGER PRIMARY KEY AUTOINCREMENT,
          run_id TEXT NOT NULL REFERENCES runs(run_id) ON DELETE CASCADE,
          scenario TEXT NOT NULL,
          test TEXT NOT NULL,
          page TEXT NOT NULL DEFAULT '',
          request TEXT NOT NULL DEFAULT '',
          transaction_name TEXT NOT NULL DEFAULT '',
          kind TEXT NOT NULL,
          UNIQUE (run_id, scenario, test, page, request, transaction_name)
        );

        -- un intervallo di campionamento per agente
        CREATE TABLE IF NOT EXISTS intervals (
          run_id TEXT NOT NULL REFERENCES runs(run_id) ON DELETE CASCADE,
          idx INTEGER NOT NULL,
          agent_id TEXT NOT NULL,
          start_utc TEXT NOT NULL,
          end_utc TEXT NOT NULL,
          active_users INTEGER NOT NULL,
          agent_saturated INTEGER NOT NULL DEFAULT 0,
          PRIMARY KEY (run_id, idx, agent_id)
        );

        -- istogramma (BLOB, vedi HistogramCodec) e conteggi per intervallo, agente e serie
        CREATE TABLE IF NOT EXISTS histograms (
          run_id TEXT NOT NULL,
          idx INTEGER NOT NULL,
          agent_id TEXT NOT NULL,
          series_id INTEGER NOT NULL REFERENCES series(series_id) ON DELETE CASCADE,
          count INTEGER NOT NULL,
          failed INTEGER NOT NULL,
          bytes_sent INTEGER NOT NULL,
          bytes_received INTEGER NOT NULL,
          hist BLOB,
          PRIMARY KEY (run_id, idx, agent_id, series_id),
          FOREIGN KEY (run_id, idx, agent_id) REFERENCES intervals(run_id, idx, agent_id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_histograms_series ON histograms(series_id);

        CREATE TABLE IF NOT EXISTS error_counts (
          run_id TEXT NOT NULL,
          idx INTEGER NOT NULL,
          agent_id TEXT NOT NULL,
          series_id INTEGER NOT NULL REFERENCES series(series_id) ON DELETE CASCADE,
          error_type TEXT NOT NULL,
          count INTEGER NOT NULL,
          PRIMARY KEY (run_id, idx, agent_id, series_id, error_type),
          FOREIGN KEY (run_id, idx, agent_id) REFERENCES intervals(run_id, idx, agent_id) ON DELETE CASCADE
        );

        -- dettaglio degli errori (limitato da runSettings.maxErrorDetails)
        CREATE TABLE IF NOT EXISTS errors (
          error_id INTEGER PRIMARY KEY AUTOINCREMENT,
          run_id TEXT NOT NULL REFERENCES runs(run_id) ON DELETE CASCADE,
          scenario TEXT NOT NULL,
          test TEXT NOT NULL,
          page TEXT,
          url TEXT,
          transaction_name TEXT,
          status_code INTEGER NOT NULL DEFAULT 0,
          error_type TEXT,
          message TEXT
        );

        -- contatori, soglie e macchine: popolati dalla chat Monitoraggio (colonne PROPOSTA)
        CREATE TABLE IF NOT EXISTS counters (
          run_id TEXT NOT NULL,
          idx INTEGER NOT NULL,
          agent_id TEXT NOT NULL,
          name TEXT NOT NULL,
          value REAL NOT NULL,
          PRIMARY KEY (run_id, idx, agent_id, name),
          FOREIGN KEY (run_id, idx, agent_id) REFERENCES intervals(run_id, idx, agent_id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS thresholds (
          threshold_id INTEGER PRIMARY KEY AUTOINCREMENT,
          run_id TEXT NOT NULL REFERENCES runs(run_id) ON DELETE CASCADE,
          at_utc TEXT NOT NULL,
          counter TEXT NOT NULL,
          severity TEXT NOT NULL,
          value REAL NOT NULL,
          limit_value REAL NOT NULL
        );

        CREATE TABLE IF NOT EXISTS machines (
          run_id TEXT NOT NULL REFERENCES runs(run_id) ON DELETE CASCADE,
          name TEXT NOT NULL,
          details_json TEXT,
          PRIMARY KEY (run_id, name)
        );

        CREATE TABLE IF NOT EXISTS annotations (
          annotation_id INTEGER PRIMARY KEY AUTOINCREMENT,
          run_id TEXT NOT NULL REFERENCES runs(run_id) ON DELETE CASCADE,
          at_utc TEXT NOT NULL,
          text TEXT NOT NULL,
          created_utc TEXT NOT NULL
        );
        """;
}
