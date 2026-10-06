using CleanBoost.Core;
using Microsoft.Data.Sqlite;

namespace CleanBoost.Engine.Persistence;

/// <summary>
/// Database SQLite (SYSTEM_DESIGN mục 12): <c>%LOCALAPPDATA%\CleanBoost\cleanboost.sqlite</c>, chế độ WAL, cùng schema
/// với bản Mac. Thời gian lưu dạng REAL (giây Unix). Mỗi thao tác mở một kết nối (có pool) để an toàn luồng.
/// </summary>
public sealed class Storage
{
    public string DatabasePath { get; }
    private readonly string _connectionString;

    public static string DefaultPath => Path.Combine(KnownPaths.AppData, "cleanboost.sqlite");

    public Storage(string path)
    {
        DatabasePath = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 5,
            ForeignKeys = true,
        }.ToString();
        using var c = Open();
        Exec(c, "PRAGMA journal_mode=WAL;");
        Migrate(c);
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        var r = cmd.ExecuteScalar();
        return r is DBNull ? null : r;
    }

    private static double Unix(DateTimeOffset d) => d.ToUnixTimeMilliseconds() / 1000.0;
    private static DateTimeOffset FromUnix(double s) => DateTimeOffset.FromUnixTimeMilliseconds((long)(s * 1000)).ToLocalTime();
    private static DateTimeOffset? FromUnixNullable(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : FromUnix(r.GetDouble(i));

    // MARK: Migration (không bao giờ sửa migration đã phát hành)

    private static void Migrate(SqliteConnection c)
    {
        var version = Convert.ToInt32(Scalar(c, "PRAGMA user_version;") ?? 0);
        if (version < 1)
        {
            using var tx = c.BeginTransaction();
            Exec(c, """
                CREATE TABLE IF NOT EXISTS scan_session (
                    id TEXT PRIMARY KEY, kind TEXT NOT NULL, started_at REAL NOT NULL, finished_at REAL,
                    status TEXT NOT NULL, found_bytes INTEGER NOT NULL DEFAULT 0, rules_version INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS clean_operation (
                    id TEXT PRIMARY KEY, session_id TEXT REFERENCES scan_session(id), started_at REAL NOT NULL, finished_at REAL,
                    planned_bytes INTEGER NOT NULL, freed_bytes INTEGER, items_ok INTEGER NOT NULL DEFAULT 0, items_failed INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE IF NOT EXISTS clean_item_log (
                    operation_id TEXT NOT NULL REFERENCES clean_operation(id) ON DELETE CASCADE, path TEXT NOT NULL, rule_id TEXT,
                    strategy TEXT NOT NULL, trashed_path TEXT, size INTEGER NOT NULL, result TEXT NOT NULL, PRIMARY KEY (operation_id, path));
                CREATE TABLE IF NOT EXISTS ignore_entry (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, value TEXT NOT NULL, created_at REAL NOT NULL, UNIQUE (kind, value));
                CREATE TABLE IF NOT EXISTS maintenance_run (
                    task TEXT NOT NULL, ran_at REAL NOT NULL, result TEXT NOT NULL, PRIMARY KEY (task, ran_at));
                CREATE TABLE IF NOT EXISTS app_usage_cache (
                    bundle_id TEXT PRIMARY KEY, path TEXT NOT NULL, team_id TEXT, version TEXT, size INTEGER,
                    last_used_at REAL, updated_at REAL NOT NULL);
                PRAGMA user_version = 1;
                """);
            tx.Commit();
        }
        if (version < 2)
        {
            using var tx = c.BeginTransaction();
            Exec(c, """
                ALTER TABLE app_usage_cache ADD COLUMN bundle_modified_at REAL;
                ALTER TABLE app_usage_cache ADD COLUMN name TEXT;
                ALTER TABLE app_usage_cache ADD COLUMN is_app_store BOOLEAN;
                CREATE INDEX IF NOT EXISTS clean_operation_started ON clean_operation(started_at);
                CREATE INDEX IF NOT EXISTS scan_session_started ON scan_session(started_at);
                PRAGMA user_version = 2;
                """);
            tx.Commit();
        }
    }

    // MARK: Phiên quét

    public ScanSessionRecord BeginSession(string kind, ulong rulesVersion)
    {
        var s = new ScanSessionRecord { Kind = kind, RulesVersion = rulesVersion };
        using var c = Open();
        Exec(c, "INSERT INTO scan_session(id, kind, started_at, status, found_bytes, rules_version) VALUES ($id,$kind,$start,'running',0,$rv)",
            ("$id", s.Id), ("$kind", kind), ("$start", Unix(s.StartedAt)), ("$rv", (long)rulesVersion));
        return s;
    }

    public void FinishSession(string id, string status, long foundBytes)
    {
        using var c = Open();
        Exec(c, "UPDATE scan_session SET finished_at=$end, status=$status, found_bytes=$found WHERE id=$id",
            ("$end", Unix(DateTimeOffset.Now)), ("$status", status), ("$found", foundBytes), ("$id", id));
    }

    public ScanSessionRecord? LastSession(string kind)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, kind, started_at, finished_at, status, found_bytes, rules_version FROM scan_session WHERE kind=$k ORDER BY started_at DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$k", kind);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadSession(r) : null;
    }

    public List<ScanSessionRecord> RecentSessions(int limit = 50)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, kind, started_at, finished_at, status, found_bytes, rules_version FROM scan_session ORDER BY started_at DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<ScanSessionRecord>();
        while (r.Read()) list.Add(ReadSession(r));
        return list;
    }

    private static ScanSessionRecord ReadSession(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Kind = r.GetString(1),
        StartedAt = FromUnix(r.GetDouble(2)),
        FinishedAt = FromUnixNullable(r, 3),
        Status = r.GetString(4),
        FoundBytes = r.GetInt64(5),
        RulesVersion = (ulong)r.GetInt64(6),
    };

    // MARK: Lần dọn

    public void Insert(CleanOperation op)
    {
        using var c = Open();
        Exec(c, """
            INSERT INTO clean_operation(id, session_id, started_at, finished_at, planned_bytes, freed_bytes, items_ok, items_failed)
            VALUES ($id, (SELECT id FROM scan_session WHERE id=$sid), $start, $end, $planned, $freed, $ok, $failed)
            """,
            ("$id", op.Id), ("$sid", op.SessionId), ("$start", Unix(op.StartedAt)), ("$end", op.FinishedAt is { } f ? Unix(f) : null),
            ("$planned", op.PlannedBytes), ("$freed", op.FreedBytes), ("$ok", op.ItemsOk), ("$failed", op.ItemsFailed));
    }

    public void Update(CleanOperation op)
    {
        using var c = Open();
        Exec(c, "UPDATE clean_operation SET finished_at=$end, freed_bytes=$freed, items_ok=$ok, items_failed=$failed WHERE id=$id",
            ("$end", op.FinishedAt is { } f ? Unix(f) : null), ("$freed", op.FreedBytes), ("$ok", op.ItemsOk), ("$failed", op.ItemsFailed), ("$id", op.Id));
    }

    public void LogItems(IEnumerable<CleanItemLog> items)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO clean_item_log(operation_id, path, rule_id, strategy, trashed_path, size, result)
            VALUES ($op, $path, $rule, $strategy, $trashed, $size, $result)
            """;
        var pOp = cmd.Parameters.Add("$op", SqliteType.Text);
        var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
        var pRule = cmd.Parameters.Add("$rule", SqliteType.Text);
        var pStrategy = cmd.Parameters.Add("$strategy", SqliteType.Text);
        var pTrashed = cmd.Parameters.Add("$trashed", SqliteType.Text);
        var pSize = cmd.Parameters.Add("$size", SqliteType.Integer);
        var pResult = cmd.Parameters.Add("$result", SqliteType.Text);
        foreach (var i in items)
        {
            pOp.Value = i.OperationId;
            pPath.Value = i.Path;
            pRule.Value = (object?)i.RuleId ?? DBNull.Value;
            pStrategy.Value = i.Strategy;
            pTrashed.Value = (object?)i.TrashedPath ?? DBNull.Value;
            pSize.Value = i.Size;
            pResult.Value = i.Result;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public List<CleanOperation> Operations(int limit = 100)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, session_id, started_at, finished_at, planned_bytes, freed_bytes, items_ok, items_failed FROM clean_operation ORDER BY started_at DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<CleanOperation>();
        while (r.Read())
        {
            list.Add(new CleanOperation
            {
                Id = r.GetString(0),
                SessionId = r.IsDBNull(1) ? null : r.GetString(1),
                StartedAt = FromUnix(r.GetDouble(2)),
                FinishedAt = FromUnixNullable(r, 3),
                PlannedBytes = r.GetInt64(4),
                FreedBytes = r.IsDBNull(5) ? null : r.GetInt64(5),
                ItemsOk = r.GetInt32(6),
                ItemsFailed = r.GetInt32(7),
            });
        }
        return list;
    }

    public List<CleanItemLog> Items(string operationId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT operation_id, path, rule_id, strategy, trashed_path, size, result FROM clean_item_log WHERE operation_id=$op ORDER BY size DESC";
        cmd.Parameters.AddWithValue("$op", operationId);
        using var r = cmd.ExecuteReader();
        var list = new List<CleanItemLog>();
        while (r.Read())
        {
            list.Add(new CleanItemLog
            {
                OperationId = r.GetString(0),
                Path = r.GetString(1),
                RuleId = r.IsDBNull(2) ? null : r.GetString(2),
                Strategy = r.GetString(3),
                TrashedPath = r.IsDBNull(4) ? null : r.GetString(4),
                Size = r.GetInt64(5),
                Result = r.GetString(6),
            });
        }
        return list;
    }

    public void MarkRestored(string operationId, string path)
    {
        using var c = Open();
        Exec(c, "UPDATE clean_item_log SET result='restored', trashed_path=NULL WHERE operation_id=$op AND path=$path", ("$op", operationId), ("$path", path));
    }

    public long FreedSince(DateTimeOffset start)
    {
        using var c = Open();
        return Convert.ToInt64(Scalar(c, "SELECT SUM(COALESCE(freed_bytes,0)) FROM clean_operation WHERE started_at >= $s", ("$s", Unix(start))) ?? 0L);
    }

    public long FreedThisMonth()
    {
        var now = DateTimeOffset.Now;
        return FreedSince(new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset));
    }

    public long TotalFreed()
    {
        using var c = Open();
        return Convert.ToInt64(Scalar(c, "SELECT SUM(COALESCE(freed_bytes,0)) FROM clean_operation") ?? 0L);
    }

    // MARK: Danh sách bỏ qua

    public void AddIgnore(string kind, string value)
    {
        using var c = Open();
        Exec(c, "INSERT OR IGNORE INTO ignore_entry(kind, value, created_at) VALUES ($k, $v, $t)", ("$k", kind), ("$v", value), ("$t", Unix(DateTimeOffset.Now)));
    }

    public void RemoveIgnore(long id)
    {
        using var c = Open();
        Exec(c, "DELETE FROM ignore_entry WHERE id=$id", ("$id", id));
    }

    public List<IgnoreEntry> IgnoreEntries()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, kind, value, created_at FROM ignore_entry ORDER BY created_at DESC";
        using var r = cmd.ExecuteReader();
        var list = new List<IgnoreEntry>();
        while (r.Read()) list.Add(new IgnoreEntry { Id = r.GetInt64(0), Kind = r.GetString(1), Value = r.GetString(2), CreatedAt = FromUnix(r.GetDouble(3)) });
        return list;
    }

    public IgnoreList IgnoreList()
    {
        var entries = IgnoreEntries();
        HashSet<string> Of(string kind) => entries.Where(e => e.Kind == kind).Select(e => e.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new IgnoreList(Of(IgnoreEntry.KindPath), Of(IgnoreEntry.KindRule), Of(IgnoreEntry.KindApp));
    }

    // MARK: Bảo trì

    public void RecordMaintenance(string task, string result)
    {
        using var c = Open();
        Exec(c, "INSERT OR REPLACE INTO maintenance_run(task, ran_at, result) VALUES ($t, $at, $r)", ("$t", task), ("$at", Unix(DateTimeOffset.Now)), ("$r", result));
    }

    /// <summary>Lần chạy gần nhất của từng tác vụ.</summary>
    public Dictionary<string, MaintenanceRun> LastMaintenanceRuns()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT task, MAX(ran_at), result FROM maintenance_run GROUP BY task";
        using var r = cmd.ExecuteReader();
        var dict = new Dictionary<string, MaintenanceRun>();
        while (r.Read()) dict[r.GetString(0)] = new MaintenanceRun(r.GetString(0), FromUnix(r.GetDouble(1)), r.GetString(2));
        return dict;
    }

    // MARK: Cache app

    public Dictionary<string, AppUsageCache> CachedApps()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT bundle_id, path, team_id, version, size, last_used_at, updated_at, name FROM app_usage_cache";
        using var r = cmd.ExecuteReader();
        var dict = new Dictionary<string, AppUsageCache>(StringComparer.OrdinalIgnoreCase);
        while (r.Read())
        {
            var e = new AppUsageCache
            {
                AppId = r.GetString(0),
                Path = r.GetString(1),
                Publisher = r.IsDBNull(2) ? null : r.GetString(2),
                Version = r.IsDBNull(3) ? null : r.GetString(3),
                Size = r.IsDBNull(4) ? null : r.GetInt64(4),
                LastUsedAt = FromUnixNullable(r, 5),
                UpdatedAt = FromUnix(r.GetDouble(6)),
                Name = r.IsDBNull(7) ? null : r.GetString(7),
            };
            dict[e.AppId] = e;
        }
        return dict;
    }

    public void UpsertApps(IEnumerable<AppUsageCache> apps)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var a in apps)
        {
            Exec(c, """
                INSERT OR REPLACE INTO app_usage_cache(bundle_id, path, team_id, version, size, last_used_at, updated_at, name)
                VALUES ($id, $path, $pub, $ver, $size, $used, $upd, $name)
                """,
                ("$id", a.AppId), ("$path", a.Path), ("$pub", a.Publisher), ("$ver", a.Version), ("$size", a.Size),
                ("$used", a.LastUsedAt is { } u ? Unix(u) : null), ("$upd", Unix(a.UpdatedAt)), ("$name", a.Name));
        }
        tx.Commit();
    }

    public void PruneApps(IReadOnlySet<string> keepIds)
    {
        var existing = CachedApps().Keys.Where(k => !keepIds.Contains(k)).ToList();
        if (existing.Count == 0) return;
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var id in existing) Exec(c, "DELETE FROM app_usage_cache WHERE bundle_id=$id", ("$id", id));
        tx.Commit();
    }

    // MARK: Dọn dữ liệu cũ

    /// <summary><c>clean_item_log</c> giữ 90 ngày, <c>scan_session</c>/<c>clean_operation</c>/<c>maintenance_run</c> giữ 1 năm.</summary>
    public void PruneOldData()
    {
        try
        {
            var now = DateTimeOffset.Now;
            var d90 = Unix(now.AddDays(-90));
            var d365 = Unix(now.AddDays(-365));
            using var c = Open();
            using var tx = c.BeginTransaction();
            Exec(c, "DELETE FROM clean_item_log WHERE operation_id IN (SELECT id FROM clean_operation WHERE started_at < $d)", ("$d", d90));
            Exec(c, "UPDATE clean_operation SET session_id=NULL WHERE session_id IN (SELECT id FROM scan_session WHERE started_at < $d)", ("$d", d365));
            Exec(c, "DELETE FROM clean_operation WHERE started_at < $d", ("$d", d365));
            Exec(c, "DELETE FROM scan_session WHERE started_at < $d", ("$d", d365));
            Exec(c, "DELETE FROM maintenance_run WHERE ran_at < $d", ("$d", d365));
            tx.Commit();
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Storage, "Không dọn được dữ liệu cũ", ex);
        }
    }
}
