using System.Globalization;
using System.Text.Json;
using LoadTest.Metrics;
using Microsoft.Data.Sqlite;

namespace LoadTest.Reports.Storage;

/// <summary>Repository SQLite dei risultati (schema: <see cref="SqliteSchema"/>). Una connessione, scritture serializzate.</summary>
public sealed class SqliteResultsRepository : IResultsRepository
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly SqliteConnection _db;
    private readonly object _gate = new();
    private readonly Dictionary<(string Run, MetricKey Key), long> _seriesIds = new();
    private SqliteTransaction? _tx;

    /// <param name="path">Percorso del file .db oppure ":memory:".</param>
    public SqliteResultsRepository(string path)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        _db = new SqliteConnection(cs);
        _db.Open();
        Exec("PRAGMA foreign_keys = ON;");
        if (path != ":memory:") Exec("PRAGMA journal_mode = WAL;");
        Initialize();
    }

    private void Initialize()
    {
        Exec(SqliteSchema.Ddl);
        var existing = Scalar("SELECT value FROM meta WHERE key = 'schema_version'");
        if (existing is null)
        {
            Exec("INSERT INTO meta(key, value) VALUES ('schema_version', $v)", ("$v", SqliteSchema.Version.ToString(Inv)));
            return;
        }
        if (!int.TryParse(Convert.ToString(existing, Inv), NumberStyles.Integer, Inv, out var found) || found > SqliteSchema.Version)
            throw new InvalidDataException($"schema del repository '{existing}' non supportato (massimo {SqliteSchema.Version})");
    }

    // ---------- scrittura ----------

    public void BeginRun(RunRecord run)
    {
        lock (_gate)
            Exec("INSERT INTO runs(run_id, name, started_utc, status, definition_yaml) VALUES ($id, $n, $s, 'Running', $y)",
                ("$id", run.RunId), ("$n", run.Name), ("$s", T(run.StartedUtc)), ("$y", run.DefinitionYaml));
    }

    public void WriteInterval(string runId, string agentId, IntervalSnapshot interval, bool agentSaturated = false)
    {
        lock (_gate)
        {
            _tx = _db.BeginTransaction();
            try
            {
                var p = new (string, object?)[] { ("$r", runId), ("$i", interval.Index), ("$a", agentId) };
                Exec("DELETE FROM intervals WHERE run_id = $r AND idx = $i AND agent_id = $a", p);   // CASCADE su histograms/error_counts/counters
                Exec("INSERT INTO intervals(run_id, idx, agent_id, start_utc, end_utc, active_users, agent_saturated) VALUES ($r, $i, $a, $s, $e, $u, $sat)",
                    ("$r", runId), ("$i", interval.Index), ("$a", agentId), ("$s", T(interval.StartUtc)), ("$e", T(interval.EndUtc)),
                    ("$u", interval.ActiveUsers), ("$sat", agentSaturated ? 1 : 0));

                foreach (var (key, s) in interval.Series)
                {
                    var sid = SeriesId(runId, key);
                    object? blob = s.Histogram.TotalCount == 0 ? null : HistogramCodec.Encode(s.Histogram);
                    Exec("INSERT INTO histograms(run_id, idx, agent_id, series_id, count, failed, bytes_sent, bytes_received, hist) VALUES ($r, $i, $a, $sid, $c, $f, $bs, $br, $h)",
                        ("$r", runId), ("$i", interval.Index), ("$a", agentId), ("$sid", sid), ("$c", s.Count), ("$f", s.Failed),
                        ("$bs", s.BytesSent), ("$br", s.BytesReceived), ("$h", blob));
                    foreach (var (type, n) in s.ErrorsByType)
                        Exec("INSERT INTO error_counts(run_id, idx, agent_id, series_id, error_type, count) VALUES ($r, $i, $a, $sid, $t, $n)",
                            ("$r", runId), ("$i", interval.Index), ("$a", agentId), ("$sid", sid), ("$t", type), ("$n", n));
                }
                _tx.Commit();
            }
            catch
            {
                _tx.Rollback();
                _seriesIds.Clear();   // gli id creati nella transazione annullata non valgono più
                throw;
            }
            finally
            {
                _tx.Dispose();
                _tx = null;
            }
        }
    }

    public void WriteErrorDetails(string runId, IEnumerable<ErrorDetail> errors)
    {
        lock (_gate)
        {
            _tx = _db.BeginTransaction();
            try
            {
                foreach (var e in errors)
                    Exec("INSERT INTO errors(run_id, scenario, test, page, url, transaction_name, status_code, error_type, message) VALUES ($r, $s, $t, $p, $u, $x, $c, $y, $m)",
                        ("$r", runId), ("$s", e.Scenario), ("$t", e.Test), ("$p", e.Page), ("$u", e.Url), ("$x", e.Transaction),
                        ("$c", e.StatusCode), ("$y", e.ErrorType), ("$m", e.Message));
                _tx.Commit();
            }
            catch
            {
                _tx.Rollback();
                throw;
            }
            finally
            {
                _tx.Dispose();
                _tx = null;
            }
        }
    }

    public void CompleteRun(string runId, RunCompletion c)
    {
        lock (_gate)
            Exec("UPDATE runs SET ended_utc = $e, status = $st, abort_reason = $ar, tests_completed = $tc, tests_failed = $tf, request_errors = $re, warnings_json = $w WHERE run_id = $r",
                ("$e", T(c.EndedUtc)), ("$st", c.Status), ("$ar", c.AbortReason), ("$tc", c.TestsCompleted), ("$tf", c.TestsFailed),
                ("$re", c.RequestErrors), ("$w", JsonSerializer.Serialize(c.Warnings)), ("$r", runId));
    }

    public void AddAnnotation(string runId, DateTimeOffset atUtc, string text)
    {
        lock (_gate)
            Exec("INSERT INTO annotations(run_id, at_utc, text, created_utc) VALUES ($r, $a, $t, $c)",
                ("$r", runId), ("$a", T(atUtc)), ("$t", text), ("$c", T(DateTimeOffset.UtcNow)));
    }

    private long SeriesId(string runId, MetricKey k)
    {
        if (_seriesIds.TryGetValue((runId, k), out var cached)) return cached;
        var args = new (string, object?)[]
        {
            ("$r", runId), ("$s", k.Scenario), ("$t", k.Test), ("$p", k.Page ?? ""), ("$q", k.Request ?? ""), ("$x", k.Transaction ?? "")
        };
        Exec("INSERT OR IGNORE INTO series(run_id, scenario, test, page, request, transaction_name, kind) VALUES ($r, $s, $t, $p, $q, $x, '" + k.Kind + "')", args);
        var id = Convert.ToInt64(Scalar("SELECT series_id FROM series WHERE run_id = $r AND scenario = $s AND test = $t AND page = $p AND request = $q AND transaction_name = $x", args), Inv);
        _seriesIds[(runId, k)] = id;
        return id;
    }

    // ---------- lettura ----------

    public RunInfo? GetRun(string runId) => ReadRuns("WHERE run_id = $r", ("$r", runId)).FirstOrDefault();

    public IReadOnlyList<RunInfo> ListRuns() => ReadRuns("ORDER BY started_utc DESC");

    private List<RunInfo> ReadRuns(string tail, params (string, object?)[] p)
    {
        var list = new List<RunInfo>();
        lock (_gate)
            Query("SELECT run_id, name, started_utc, ended_utc, status, abort_reason, definition_yaml, tests_completed, tests_failed, request_errors, warnings_json FROM runs " + tail,
                r =>
                {
                    List<string> warnings = r.IsDBNull(10) ? new List<string>() : JsonSerializer.Deserialize<List<string>>(r.GetString(10)) ?? new List<string>();
                    list.Add(new RunInfo(
                        r.GetString(0), r.GetString(1), ParseT(r.GetString(2)), r.IsDBNull(3) ? null : ParseT(r.GetString(3)), r.GetString(4),
                        r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
                        r.GetInt64(7), r.GetInt64(8), r.GetInt64(9), warnings));
                }, p);
        return list;
    }

    public IReadOnlyList<IntervalInfo> ReadIntervals(string runId)
    {
        var list = new List<IntervalInfo>();
        lock (_gate)
            Query("SELECT idx, agent_id, start_utc, end_utc, active_users, agent_saturated FROM intervals WHERE run_id = $r ORDER BY idx, agent_id",
                r => list.Add(new IntervalInfo(r.GetInt32(0), r.GetString(1), ParseT(r.GetString(2)), ParseT(r.GetString(3)), r.GetInt32(4), r.GetInt32(5) != 0)),
                ("$r", runId));
        return list;
    }

    public IReadOnlyList<SeriesSample> ReadSamples(string runId)
    {
        var errors = new Dictionary<(int Idx, string Agent, long Series), Dictionary<string, long>>();
        var list = new List<SeriesSample>();
        lock (_gate)
        {
            Query("SELECT idx, agent_id, series_id, error_type, count FROM error_counts WHERE run_id = $r",
                r =>
                {
                    var k = (r.GetInt32(0), r.GetString(1), r.GetInt64(2));
                    if (!errors.TryGetValue(k, out var d)) errors[k] = d = new Dictionary<string, long>();
                    d[r.GetString(3)] = r.GetInt64(4);
                }, ("$r", runId));

            Query("""
                SELECT h.idx, h.agent_id, h.series_id, i.start_utc, i.end_utc, i.agent_saturated,
                       s.scenario, s.test, s.page, s.request, s.transaction_name,
                       h.count, h.failed, h.bytes_sent, h.bytes_received, h.hist
                FROM histograms h
                JOIN intervals i ON i.run_id = h.run_id AND i.idx = h.idx AND i.agent_id = h.agent_id
                JOIN series s ON s.series_id = h.series_id
                WHERE h.run_id = $r
                ORDER BY h.idx, h.agent_id, s.series_id
                """,
                r =>
                {
                    var idx = r.GetInt32(0);
                    var agent = r.GetString(1);
                    var sid = r.GetInt64(2);
                    var key = new MetricKey(r.GetString(6), r.GetString(7), NullIfEmpty(r.GetString(8)), NullIfEmpty(r.GetString(9)), NullIfEmpty(r.GetString(10)));
                    var snap = new SeriesSnapshot
                    {
                        Histogram = HistogramCodec.Decode(r.IsDBNull(15) ? null : (byte[])r.GetValue(15)),
                        Count = r.GetInt64(11), Failed = r.GetInt64(12), BytesSent = r.GetInt64(13), BytesReceived = r.GetInt64(14),
                        ErrorsByType = errors.TryGetValue((idx, agent, sid), out var e) ? e : new Dictionary<string, long>()
                    };
                    list.Add(new SeriesSample(idx, agent, ParseT(r.GetString(3)), ParseT(r.GetString(4)), r.GetInt32(5) != 0, key, snap));
                }, ("$r", runId));
        }
        return list;
    }

    public IReadOnlyList<ErrorDetail> ReadErrorDetails(string runId)
    {
        var list = new List<ErrorDetail>();
        lock (_gate)
            Query("SELECT scenario, test, page, url, transaction_name, status_code, error_type, message FROM errors WHERE run_id = $r ORDER BY error_id",
                r => list.Add(new ErrorDetail(r.GetString(0), r.GetString(1), Str(r, 2), Str(r, 3), Str(r, 4), r.GetInt32(5), Str(r, 6), Str(r, 7))),
                ("$r", runId));
        return list;
    }

    public IReadOnlyList<Annotation> ReadAnnotations(string runId)
    {
        var list = new List<Annotation>();
        lock (_gate)
            Query("SELECT annotation_id, at_utc, text FROM annotations WHERE run_id = $r ORDER BY at_utc, annotation_id",
                r => list.Add(new Annotation(r.GetInt64(0), ParseT(r.GetString(1)), r.GetString(2))), ("$r", runId));
        return list;
    }

    public void Dispose() => _db.Dispose();

    // ---------- utilità ----------

    private static string T(DateTimeOffset d) => d.ToUniversalTime().ToString("O", Inv);
    private static DateTimeOffset ParseT(string s) => DateTimeOffset.Parse(s, Inv, DateTimeStyles.AssumeUniversal);
    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private SqliteCommand Cmd(string sql, (string Name, object? Value)[] p)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _tx;
        foreach (var (name, value) in p) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private int Exec(string sql, params (string Name, object? Value)[] p)
    {
        using var cmd = Cmd(sql, p);
        return cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string Name, object? Value)[] p)
    {
        using var cmd = Cmd(sql, p);
        return cmd.ExecuteScalar();
    }

    private void Query(string sql, Action<SqliteDataReader> row, params (string Name, object? Value)[] p)
    {
        using var cmd = Cmd(sql, p);
        using var r = cmd.ExecuteReader();
        while (r.Read()) row(r);
    }
}
