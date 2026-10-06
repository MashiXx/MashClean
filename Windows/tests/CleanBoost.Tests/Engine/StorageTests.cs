using CleanBoost.Core;
using CleanBoost.Engine.Persistence;
using Microsoft.Data.Sqlite;

namespace CleanBoost.Tests.Engine;

/// <summary>Database SQLite (mục 12): migration, phiên quét, lần dọn, nhật ký mục, danh sách bỏ qua, dọn dữ liệu cũ.</summary>
public sealed class StorageTests : IDisposable
{
    private readonly TempDir _tmp = new("storage");
    private readonly string _path;
    private readonly Storage _storage;

    public StorageTests()
    {
        _path = _tmp.P(@"nested\dir\cleanboost.sqlite");
        _storage = new Storage(_path);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _tmp.Dispose();
    }

    private SqliteConnection Raw()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    private object? Scalar(string sql, params (string, object)[] args)
    {
        using var c = Raw();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        return cmd.ExecuteScalar();
    }

    private void Exec(string sql, params (string, object?)[] args)
    {
        using var c = Raw();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static double Unix(DateTimeOffset d) => d.ToUnixTimeMilliseconds() / 1000.0;

    private HashSet<string> Columns(string table)
    {
        using var c = Raw();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var r = cmd.ExecuteReader();
        var set = new HashSet<string>();
        while (r.Read()) set.Add(r.GetString(1));
        return set;
    }

    [Fact]
    public void FreshDatabaseIsMigratedToLatestInWal()
    {
        Assert.True(File.Exists(_path));
        Assert.Equal(2L, Scalar("PRAGMA user_version"));
        Assert.Equal("wal", Scalar("PRAGMA journal_mode"));
        Assert.Contains("name", Columns("app_usage_cache"));
        Assert.Contains("is_app_store", Columns("app_usage_cache"));
        Assert.Contains("bundle_modified_at", Columns("app_usage_cache"));
        foreach (var t in new[] { "scan_session", "clean_operation", "clean_item_log", "ignore_entry", "maintenance_run", "app_usage_cache" })
            Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n", ("$n", t)));
    }

    [Fact]
    public void ReopeningIsIdempotentAndKeepsData()
    {
        _storage.AddIgnore(IgnoreEntry.KindRule, "rule.x");
        var again = new Storage(_path);
        Assert.Equal(2L, Scalar("PRAGMA user_version"));
        Assert.Single(again.IgnoreEntries());
    }

    [Fact]
    public void MigratesVersion1Database()
    {
        var old = _tmp.P(@"v1\old.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = old, Pooling = false }.ToString()))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE app_usage_cache (bundle_id TEXT PRIMARY KEY, path TEXT NOT NULL, team_id TEXT, version TEXT, size INTEGER,
                    last_used_at REAL, updated_at REAL NOT NULL);
                CREATE TABLE scan_session (id TEXT PRIMARY KEY, kind TEXT NOT NULL, started_at REAL NOT NULL, finished_at REAL,
                    status TEXT NOT NULL, found_bytes INTEGER NOT NULL DEFAULT 0, rules_version INTEGER NOT NULL);
                CREATE TABLE clean_operation (id TEXT PRIMARY KEY, session_id TEXT REFERENCES scan_session(id), started_at REAL NOT NULL,
                    finished_at REAL, planned_bytes INTEGER NOT NULL, freed_bytes INTEGER, items_ok INTEGER NOT NULL DEFAULT 0,
                    items_failed INTEGER NOT NULL DEFAULT 0);
                INSERT INTO app_usage_cache(bundle_id, path, updated_at) VALUES ('old.app', 'C:\x', 1.0);
                PRAGMA user_version = 1;
                """;
            cmd.ExecuteNonQuery();
        }

        var storage = new Storage(old);

        var apps = storage.CachedApps();
        Assert.Equal(@"C:\x", apps["OLD.APP"].Path); // tra không phân biệt hoa thường
        Assert.Null(apps["old.app"].Name);
        storage.UpsertApps([new AppUsageCache { AppId = "old.app", Path = @"C:\y", Name = "Old App" }]);
        Assert.Equal("Old App", storage.CachedApps()["old.app"].Name);
    }

    [Fact]
    public void SessionsRoundTrip()
    {
        var a = _storage.BeginSession("systemJunk", 2026100501);
        Thread.Sleep(20);
        var b = _storage.BeginSession("smartScan", 7);
        _storage.FinishSession(a.Id, "succeeded", 123_456);

        var last = _storage.LastSession("systemJunk");
        Assert.NotNull(last);
        Assert.Equal(a.Id, last.Id);
        Assert.Equal("succeeded", last.Status);
        Assert.Equal(123_456, last.FoundBytes);
        Assert.Equal(2026100501ul, last.RulesVersion);
        Assert.NotNull(last.FinishedAt);
        Assert.True(Math.Abs((last.StartedAt - a.StartedAt).TotalMilliseconds) < 2);
        Assert.Equal("running", _storage.LastSession("smartScan")?.Status);
        Assert.Null(_storage.LastSession("nope"));
        Assert.Equal(new[] { b.Id, a.Id }, _storage.RecentSessions().Select(s => s.Id));
        Assert.Single(_storage.RecentSessions(limit: 1));
    }

    [Fact]
    public void OperationsAndItemLogs()
    {
        var session = _storage.BeginSession("systemJunk", 1);
        var op = new CleanOperation { SessionId = session.Id, PlannedBytes = 1000 };
        _storage.Insert(op);
        // Phiên không tồn tại: session_id thành NULL thay vì vi phạm khoá ngoại.
        var orphan = new CleanOperation { SessionId = "ghost", PlannedBytes = 1, StartedAt = DateTimeOffset.Now.AddMinutes(-1) };
        _storage.Insert(orphan);

        op.FinishedAt = DateTimeOffset.Now;
        op.FreedBytes = 900;
        op.ItemsOk = 2;
        op.ItemsFailed = 1;
        _storage.Update(op);

        _storage.LogItems([
            new CleanItemLog { OperationId = op.Id, Path = @"C:\a", Strategy = "delete", Size = 10, Result = "ok" },
            new CleanItemLog { OperationId = op.Id, Path = @"C:\b", Strategy = "trash", TrashedPath = @"C:\$Recycle.Bin\S\$Rb", Size = 500, Result = "ok", RuleId = "r" },
            new CleanItemLog { OperationId = op.Id, Path = @"C:\c", Strategy = "delete", Size = 90, Result = "failed:5" },
        ]);
        // Ghi lại cùng khoá: thay thế, không nhân đôi.
        _storage.LogItems([new CleanItemLog { OperationId = op.Id, Path = @"C:\a", Strategy = "delete", Size = 11, Result = "ok" }]);

        var ops = _storage.Operations();
        Assert.Equal(new[] { op.Id, orphan.Id }, ops.Select(o => o.Id));
        Assert.Equal(session.Id, ops[0].SessionId);
        Assert.Null(ops[1].SessionId);
        Assert.Equal(900, ops[0].FreedBytes);
        Assert.Null(ops[1].FreedBytes);
        Assert.Equal((2, 1), (ops[0].ItemsOk, ops[0].ItemsFailed));
        Assert.NotNull(ops[0].FinishedAt);

        var items = _storage.Items(op.Id);
        Assert.Equal(new[] { @"C:\b", @"C:\c", @"C:\a" }, items.Select(i => i.Path)); // theo dung lượng giảm dần
        Assert.Equal(11, items[2].Size);
        Assert.True(items[0].IsRestorable);
        Assert.False(items[1].IsRestorable);
        Assert.Equal("r", items[0].RuleId);

        _storage.MarkRestored(op.Id, @"C:\b");
        var restored = _storage.Items(op.Id).Single(i => i.Path == @"C:\b");
        Assert.Equal("restored", restored.Result);
        Assert.Null(restored.TrashedPath);
        Assert.False(restored.IsRestorable);

        Assert.Equal(900, _storage.TotalFreed());
        Assert.Equal(900, _storage.FreedSince(DateTimeOffset.Now.AddHours(-1)));
        Assert.Equal(0, _storage.FreedSince(DateTimeOffset.Now.AddHours(1)));
        Assert.Equal(900, _storage.FreedThisMonth());
    }

    [Fact]
    public void IgnoreListEntries()
    {
        _storage.AddIgnore(IgnoreEntry.KindPath, @"C:\Users\t\AppData\Local\Keep");
        _storage.AddIgnore(IgnoreEntry.KindPath, @"C:\Users\t\AppData\Local\Keep"); // trùng: bỏ qua
        _storage.AddIgnore(IgnoreEntry.KindRule, "rule.a");
        _storage.AddIgnore(IgnoreEntry.KindApp, "Vendor.App");

        var entries = _storage.IgnoreEntries();
        Assert.Equal(3, entries.Count);
        var list = _storage.IgnoreList();
        Assert.True(list.IgnoresPath(@"c:\users\t\appdata\local\keep\sub"));
        Assert.True(list.IgnoresRule("rule.a"));
        Assert.True(list.IgnoresApp("vendor.app"));

        _storage.RemoveIgnore(entries.Single(e => e.Kind == IgnoreEntry.KindRule).Id);
        Assert.False(_storage.IgnoreList().IgnoresRule("rule.a"));
        Assert.Equal(2, _storage.IgnoreEntries().Count);
    }

    [Fact]
    public void MaintenanceRunsKeepLatestPerTask()
    {
        _storage.RecordMaintenance("flushDNS", "failed");
        Thread.Sleep(20);
        _storage.RecordMaintenance("flushDNS", "ok");
        _storage.RecordMaintenance("freeRAM", "ok");
        var runs = _storage.LastMaintenanceRuns();
        Assert.Equal(2, runs.Count);
        Assert.Equal("ok", runs["flushDNS"].Result);
    }

    [Fact]
    public void AppCacheUpsertAndPrune()
    {
        var used = DateTimeOffset.Now.AddDays(-3);
        _storage.UpsertApps([
            new AppUsageCache { AppId = "a", Path = @"C:\A", Publisher = "Acme", Version = "1.0", Size = 10, LastUsedAt = used, Name = "App A" },
            new AppUsageCache { AppId = "b", Path = @"C:\B" },
        ]);
        var apps = _storage.CachedApps();
        Assert.Equal(2, apps.Count);
        Assert.Equal("Acme", apps["a"].Publisher);
        Assert.Equal(10, apps["a"].Size);
        Assert.True(Math.Abs((apps["a"].LastUsedAt!.Value - used).TotalMilliseconds) < 2);
        Assert.Null(apps["b"].LastUsedAt);

        _storage.PruneApps(new HashSet<string> { "a" });
        Assert.Equal(new[] { "a" }, _storage.CachedApps().Keys);
    }

    [Fact]
    public void PruneOldDataHonorsRetention()
    {
        var now = DateTimeOffset.Now;
        Exec("INSERT INTO scan_session(id, kind, started_at, status, found_bytes, rules_version) VALUES ('old-session','x',$t,'succeeded',0,1)",
            ("$t", Unix(now.AddDays(-400))));
        Exec("INSERT INTO clean_operation(id, session_id, started_at, planned_bytes) VALUES ('recent-op','old-session',$t,0)", ("$t", Unix(now.AddDays(-10))));
        Exec("INSERT INTO clean_operation(id, session_id, started_at, planned_bytes) VALUES ('mid-op',NULL,$t,0)", ("$t", Unix(now.AddDays(-100))));
        Exec("INSERT INTO clean_operation(id, session_id, started_at, planned_bytes) VALUES ('ancient-op',NULL,$t,0)", ("$t", Unix(now.AddDays(-500))));
        _storage.LogItems([
            new CleanItemLog { OperationId = "recent-op", Path = @"C:\r", Strategy = "delete", Result = "ok" },
            new CleanItemLog { OperationId = "mid-op", Path = @"C:\m", Strategy = "delete", Result = "ok" },
            new CleanItemLog { OperationId = "ancient-op", Path = @"C:\a", Strategy = "delete", Result = "ok" },
        ]);
        Exec("INSERT INTO maintenance_run(task, ran_at, result) VALUES ('old', $t, 'ok')", ("$t", Unix(now.AddDays(-400))));
        _storage.RecordMaintenance("new", "ok");

        _storage.PruneOldData();

        Assert.Equal(new[] { "recent-op", "mid-op" }, _storage.Operations().Select(o => o.Id));
        Assert.Null(_storage.Operations().Single(o => o.Id == "recent-op").SessionId); // phiên cũ bị xoá, liên kết bị gỡ
        Assert.Empty(_storage.RecentSessions());
        Assert.Single(_storage.Items("recent-op"));
        Assert.Empty(_storage.Items("mid-op"));       // nhật ký mục giữ 90 ngày
        Assert.Empty(_storage.Items("ancient-op"));
        Assert.Equal(new[] { "new" }, _storage.LastMaintenanceRuns().Keys);
    }

    [Fact]
    public void ConcurrentWritersDoNotCorruptTheDatabase()
    {
        Parallel.For(0, 40, i =>
        {
            _storage.AddIgnore(IgnoreEntry.KindPath, @"C:\p" + i);
            _storage.RecordMaintenance("task" + i, "ok");
        });
        Assert.Equal(40, _storage.IgnoreEntries().Count);
        Assert.Equal(40, _storage.LastMaintenanceRuns().Count);
    }
}

/// <summary>Cài đặt JSON (<c>settings.json</c>) với đường dẫn tạm: không bao giờ đụng file cài đặt thật.</summary>
public sealed class AppSettingsTests : IDisposable
{
    private readonly TempDir _tmp = new("settings");
    private string SettingsPath => _tmp.P(@"CleanBoost\settings.json");

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void DefaultsMatchMacKeys()
    {
        var s = new AppSettings(SettingsPath);
        Assert.Equal("stable", s.UpdateChannel);
        Assert.True(s.MenuBarEnabled);
        Assert.False(s.AnalyticsEnabled);
        Assert.False(s.ShowRiskyItems);
        Assert.Equal(5, s.TrashAlertGB);
        Assert.Equal(100, s.LargeFileThresholdMB);
        Assert.Equal(365, s.OldFileDays);
        Assert.Equal(AppLanguage.System, s.Language);
        Assert.Null(s.RulesLastCheck);
        Assert.False(File.Exists(SettingsPath)); // đọc không tạo file
    }

    [Fact]
    public void ValuesPersistAcrossInstancesWithAtomicWrite()
    {
        var s = new AppSettings(SettingsPath);
        var changed = new List<string>();
        s.Changed += changed.Add;
        var check = new DateTimeOffset(2026, 10, 5, 12, 30, 0, TimeSpan.FromHours(7));

        s.ShowRiskyItems = true;
        s.TrashAlertGB = 12;
        s.Language = AppLanguage.English;
        s.UpdateChannel = "beta";
        s.RulesLastCheck = check;
        s.RulesManifestETag = "\"abc\"";

        Assert.Equal(new[] { "scan.showRisky", "alerts.trashGB", "app.language", "updates.channel", "rules.lastCheck", "rules.manifestETag" }, changed);
        Assert.False(File.Exists(SettingsPath + ".tmp"));

        var reread = new AppSettings(SettingsPath);
        Assert.True(reread.ShowRiskyItems);
        Assert.Equal(12, reread.TrashAlertGB);
        Assert.Equal(AppLanguage.English, reread.Language);
        Assert.Equal("beta", reread.UpdateChannel);
        Assert.Equal(check, reread.RulesLastCheck);
        Assert.Equal("\"abc\"", reread.RulesManifestETag);
        Assert.Contains("\"scan.showRisky\": true", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void CorruptOrWrongTypedValuesFallBackToDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, "{ not json");
        Assert.Equal(5, new AppSettings(SettingsPath).TrashAlertGB);

        File.WriteAllText(SettingsPath, """{"alerts.trashGB": "many", "scan.showRisky": true, "app.language": "klingon"}""");
        var s = new AppSettings(SettingsPath);
        Assert.Equal(5, s.TrashAlertGB);
        Assert.True(s.ShowRiskyItems);
        Assert.Equal(AppLanguage.System, s.Language);

        File.WriteAllText(SettingsPath, "[1, 2, 3]");
        Assert.True(new AppSettings(SettingsPath).MenuBarEnabled);
    }

    [Fact]
    public void ReloadPicksUpExternalChanges()
    {
        var a = new AppSettings(SettingsPath);
        var b = new AppSettings(SettingsPath);
        string? key = null;
        b.Changed += k => key = k;
        a.OldFileDays = 30;
        Assert.Equal(365, b.OldFileDays);
        b.Reload();
        Assert.Equal(30, b.OldFileDays);
        Assert.Equal("*", key);
    }

    [Fact]
    public void AlertsAreThrottledPerKind()
    {
        var s = new AppSettings(SettingsPath);
        Assert.True(s.ShouldAlert("lowDisk"));
        s.MarkAlerted("lowDisk");
        Assert.False(s.ShouldAlert("lowDisk"));
        Assert.True(s.ShouldAlert("trash"));
        Assert.NotNull(new AppSettings(SettingsPath).LastAlert("lowDisk"));
    }
}
