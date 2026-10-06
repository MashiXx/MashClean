namespace CleanBoost.Engine.Persistence;

/// <summary>Một phiên quét (bảng <c>scan_session</c>).</summary>
public sealed record ScanSessionRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public required string Kind { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; set; }
    /// <summary><c>running | succeeded | failed | cancelled</c>.</summary>
    public string Status { get; set; } = "running";
    public long FoundBytes { get; set; }
    public ulong RulesVersion { get; init; }
}

/// <summary>Một lần dọn (bảng <c>clean_operation</c>).</summary>
public sealed record CleanOperation
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string? SessionId { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; set; }
    public long PlannedBytes { get; init; }
    /// <summary>Đo thực tế từ dung lượng ổ.</summary>
    public long? FreedBytes { get; set; }
    public int ItemsOk { get; set; }
    public int ItemsFailed { get; set; }
}

/// <summary>Một mục trong lần dọn (bảng <c>clean_item_log</c>).</summary>
public sealed record CleanItemLog
{
    public required string OperationId { get; init; }
    public required string Path { get; init; }
    public string? RuleId { get; init; }
    /// <summary><c>trash | delete | deleteContents | custom:&lt;id&gt;</c>.</summary>
    public required string Strategy { get; init; }
    /// <summary>Đường dẫn trong Thùng rác, để khôi phục.</summary>
    public string? TrashedPath { get; init; }
    public long Size { get; init; }
    /// <summary><c>ok | restored | skipped:&lt;lý do&gt; | failed:&lt;mã&gt;</c>.</summary>
    public required string Result { get; init; }

    public string Key => OperationId + "\0" + Path;
    public bool IsRestorable => TrashedPath is not null && Result == "ok";
}

/// <summary>Mục "không bao giờ đề xuất" (bảng <c>ignore_entry</c>).</summary>
public sealed record IgnoreEntry
{
    public long Id { get; init; }
    /// <summary><c>path | rule | app</c>.</summary>
    public required string Kind { get; init; }
    public required string Value { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public const string KindPath = "path";
    public const string KindRule = "rule";
    public const string KindApp = "bundleID";
}

/// <summary>Lần chạy tác vụ bảo trì (bảng <c>maintenance_run</c>).</summary>
public sealed record MaintenanceRun(string Task, DateTimeOffset RanAt, string Result);

/// <summary>Cache thông tin app (bảng <c>app_usage_cache</c>).</summary>
public sealed record AppUsageCache
{
    public required string AppId { get; init; }
    public required string Path { get; init; }
    public string? Publisher { get; init; }
    public string? Version { get; init; }
    public long? Size { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
    public string? Name { get; init; }
}
