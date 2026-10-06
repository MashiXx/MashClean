using CleanBoost.Core.Native;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CleanBoost.Core;

/// <summary>Mức an toàn của một mục đề xuất xoá (SYSTEM_DESIGN mục 6.1).</summary>
public enum SafetyLevel
{
    /// <summary>Tự chọn sẵn.</summary>
    Safe = 0,
    /// <summary>Hiển thị nhưng không tự chọn.</summary>
    Review = 1,
    /// <summary>Ẩn trong "Nâng cao", cần xác nhận.</summary>
    Risky = 2,
}

public static class SafetyLevelExtensions
{
    public static SafetyLevel? ParseRuleValue(string value) => value switch
    {
        "safe" => SafetyLevel.Safe,
        "review" => SafetyLevel.Review,
        "risky" => SafetyLevel.Risky,
        _ => null,
    };

    public static string RuleValue(this SafetyLevel s) => s switch
    {
        SafetyLevel.Safe => "safe",
        SafetyLevel.Review => "review",
        _ => "risky",
    };

    public static string LocalizedTitle(this SafetyLevel s) => s switch
    {
        SafetyLevel.Safe => Loc.T("An toàn"),
        SafetyLevel.Review => Loc.T("Cần xem lại"),
        _ => Loc.T("Rủi ro"),
    };

    public static SafetyLevel Raised(this SafetyLevel s, SafetyLevel other) => (SafetyLevel)Math.Max((int)s, (int)other);
}

public enum RemovalKind
{
    /// <summary>Mặc định cho file người dùng: chuyển vào Thùng rác.</summary>
    MoveToTrash,
    /// <summary>Cache, log: xoá thẳng (tái tạo được).</summary>
    Delete,
    /// <summary>Giữ thư mục, xoá nội dung bên trong.</summary>
    DeleteContents,
    /// <summary>Remover chuyên biệt.</summary>
    Custom,
}

/// <summary>Chiến lược xoá (mục 7.3): <c>moveToTrash</c> / <c>delete</c> / <c>deleteContents</c> / <c>custom:&lt;id&gt;</c>.</summary>
[JsonConverter(typeof(RemovalStrategyJsonConverter))]
public readonly record struct RemovalStrategy(RemovalKind Kind, string? CustomID = null)
{
    public static readonly RemovalStrategy MoveToTrash = new(RemovalKind.MoveToTrash);
    public static readonly RemovalStrategy Delete = new(RemovalKind.Delete);
    public static readonly RemovalStrategy DeleteContents = new(RemovalKind.DeleteContents);
    public static RemovalStrategy Custom(string removerID) => new(RemovalKind.Custom, removerID);

    public static RemovalStrategy? Parse(string value)
    {
        switch (value)
        {
            case "moveToTrash": return MoveToTrash;
            case "delete": return Delete;
            case "deleteContents": return DeleteContents;
        }
        if (!value.StartsWith("custom:", StringComparison.Ordinal)) return null;
        var id = value["custom:".Length..];
        return id.Length == 0 ? null : Custom(id);
    }

    public string RuleValue => Kind switch
    {
        RemovalKind.MoveToTrash => "moveToTrash",
        RemovalKind.Delete => "delete",
        RemovalKind.DeleteContents => "deleteContents",
        _ => "custom:" + CustomID,
    };

    /// <summary>Giá trị cột <c>clean_item_log.strategy</c>.</summary>
    public string LogValue => Kind switch
    {
        RemovalKind.MoveToTrash => "trash",
        RemovalKind.Delete => "delete",
        RemovalKind.DeleteContents => "deleteContents",
        _ => "custom:" + CustomID,
    };

    public bool IsRecoverable => Kind == RemovalKind.MoveToTrash;
    public bool IsCustom => Kind == RemovalKind.Custom;

    public override string ToString() => RuleValue;
}

internal sealed class RemovalStrategyJsonConverter : JsonConverter<RemovalStrategy>
{
    public override RemovalStrategy Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString() ?? "";
        return RemovalStrategy.Parse(raw) ?? throw new JsonException($"Unknown removal strategy {raw}");
    }

    public override void Write(Utf8JsonWriter writer, RemovalStrategy value, JsonSerializerOptions options) => writer.WriteStringValue(value.RuleValue);
}

/// <summary>Chế độ xoá gửi sang helper.</summary>
public enum RemoveMode
{
    Delete = 0,
    DeleteContents = 1,
}

public enum SkipReason
{
    /// <summary>File bị khoá hoặc app đang mở.</summary>
    [Description("inUse")] InUse,
    /// <summary>Không còn tồn tại: coi như thành công, giải phóng 0.</summary>
    [Description("notFound")] NotFound,
    [Description("blockedByPolicy")] BlockedByPolicy,
    /// <summary>Windows bảo vệ (TrustedInstaller) — tương đương SIP.</summary>
    [Description("protectedBySystem")] ProtectedBySystem,
    [Description("userDeclined")] UserDeclined,
    [Description("dryRun")] DryRun,
    [Description("appRunning")] AppRunning,
    [Description("unsupported")] Unsupported,
}

public enum RemoveOutcomeKind
{
    Ok,
    Skipped,
    Failed,
}

/// <summary>Kết quả xoá một đường dẫn, trả về từ remover hoặc helper. Mã lỗi là mã Win32.</summary>
public sealed record RemoveResult
{
    public required string Path { get; init; }
    public RemoveOutcomeKind Outcome { get; init; }
    public SkipReason? Reason { get; init; }
    public int ErrorCode { get; init; }
    public string? Message { get; init; }
    public long FreedBytes { get; init; }
    /// <summary>Đường dẫn trong Thùng rác (nếu chuyển vào Thùng rác), để khôi phục (mục 15.3).</summary>
    public string? TrashedPath { get; init; }

    [JsonIgnore]
    public bool IsSuccess => Outcome == RemoveOutcomeKind.Ok || (Outcome == RemoveOutcomeKind.Skipped && Reason == SkipReason.NotFound);

    [JsonIgnore]
    public bool IsFailure => Outcome == RemoveOutcomeKind.Failed;

    /// <summary>Giá trị cột <c>clean_item_log.result</c>: <c>ok | skipped:&lt;lý do&gt; | failed:&lt;mã&gt;</c>.</summary>
    [JsonIgnore]
    public string LogValue => Outcome switch
    {
        RemoveOutcomeKind.Ok => "ok",
        RemoveOutcomeKind.Skipped => "skipped:" + ReasonValue(Reason ?? SkipReason.Unsupported),
        _ => "failed:" + ErrorCode,
    };

    public static string ReasonValue(SkipReason r) => r switch
    {
        SkipReason.InUse => "inUse",
        SkipReason.NotFound => "notFound",
        SkipReason.BlockedByPolicy => "blockedByPolicy",
        SkipReason.ProtectedBySystem => "protectedBySystem",
        SkipReason.UserDeclined => "userDeclined",
        SkipReason.DryRun => "dryRun",
        SkipReason.AppRunning => "appRunning",
        _ => "unsupported",
    };

    public static RemoveResult Ok(string path, long freed, string? trashedPath = null) =>
        new() { Path = path, Outcome = RemoveOutcomeKind.Ok, FreedBytes = freed, TrashedPath = trashedPath };

    public static RemoveResult Skipped(string path, SkipReason reason, long freed = 0) =>
        new() { Path = path, Outcome = RemoveOutcomeKind.Skipped, Reason = reason, FreedBytes = freed };

    public static RemoveResult Failed(string path, int win32Error, string? message = null, long freed = 0) =>
        new() { Path = path, Outcome = RemoveOutcomeKind.Failed, ErrorCode = win32Error, Message = message ?? Win32Error.Describe(win32Error), FreedBytes = freed };

    public string LocalizedDescription => Outcome switch
    {
        RemoveOutcomeKind.Ok => Loc.T("Thành công"),
        RemoveOutcomeKind.Skipped => Reason switch
        {
            SkipReason.InUse => Loc.T("Đang được sử dụng"),
            SkipReason.NotFound => Loc.T("Không còn tồn tại"),
            SkipReason.BlockedByPolicy => Loc.T("Bị chặn vì lý do an toàn"),
            SkipReason.ProtectedBySystem => Loc.T("Windows bảo vệ mục này"),
            SkipReason.UserDeclined => Loc.T("Bạn đã không đồng ý xoá"),
            SkipReason.DryRun => Loc.T("Chế độ thử: không xoá"),
            SkipReason.AppRunning => Loc.T("Ứng dụng đang chạy"),
            _ => Loc.T("Không hỗ trợ"),
        },
        _ => Message ?? Win32Error.Describe(ErrorCode),
    };
}
