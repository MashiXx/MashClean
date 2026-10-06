using System.Text.Json;
using System.Text.Json.Serialization;
using CleanBoost.Core;

namespace CleanBoost.Engine.Rules;

/// <summary>Một rule dọn dẹp (SYSTEM_DESIGN mục 8.2, 8.3), bản Windows.</summary>
public sealed record Rule
{
    public sealed record MatchSpec
    {
        /// <summary>
        /// Glob: hỗ trợ <c>~</c>, <c>*</c>, <c>**</c>, token thư mục (<c>%LOCALAPPDATA%</c>, <c>%WINDIR%</c>...) và biến theo app
        /// <c>${appID}</c>, <c>${appName}</c>, <c>${publisher}</c>, <c>${userHome}</c>.
        /// </summary>
        public List<string> Paths { get; init; } = [];
        /// <summary>Áp rule cho từng app trong <c>installedApps</c>.</summary>
        public bool? ForEachInstalledApp { get; init; }
        /// <summary>Chỉ lấy mục không được sửa/truy cập trong N ngày.</summary>
        public int? MinAgeDays { get; init; }
        /// <summary>Bỏ qua mục nhỏ hơn ngưỡng.</summary>
        public long? MinSizeBytes { get; init; }
        /// <summary>Chỉ áp cho app có định danh khớp (glob).</summary>
        public List<string>? AppIDs { get; init; }
        /// <summary>Chỉ áp cho app có tên hiển thị khớp (glob).</summary>
        public List<string>? AppNames { get; init; }
        /// <summary>Chỉ áp cho app có nhà phát hành khớp (glob).</summary>
        public List<string>? Publishers { get; init; }
        /// <summary>Nguồn dữ liệu đặc biệt cài trong code (vd <c>recycleBin</c>), khi đường dẫn không đủ diễn tả.</summary>
        public string? Provider { get; init; }
    }

    public sealed record ConditionSpec
    {
        /// <summary>Không đề xuất xoá khi một trong các tiến trình này đang chạy (tên exe không đuôi, vd <c>chrome</c>; <c>${appID}</c> = chính app).</summary>
        public List<string>? AppNotRunning { get; init; }
        /// <summary>Phiên bản Windows tối thiểu, ví dụ <c>10.0.19041</c>.</summary>
        public string? MinOS { get; init; }
    }

    public required string Id { get; init; }
    public int? Version { get; init; }
    public required string Category { get; init; }
    public LocalizedText? Title { get; init; }
    public LocalizedText? Reason { get; init; }
    [JsonConverter(typeof(SafetyJsonConverter))]
    public SafetyLevel Safety { get; init; }
    public RemovalStrategy Removal { get; init; }
    public bool? RequiresRoot { get; init; }
    public required MatchSpec Match { get; init; }
    public ConditionSpec? Conditions { get; init; }
    /// <summary>Loại trừ, luôn thắng <c>match</c>.</summary>
    public List<string>? Exclude { get; init; }
    /// <summary>Ghi chú cho người review rule (không hiển thị).</summary>
    public string? Notes { get; init; }

    public string DisplayTitle => Title?.Resolved is { Length: > 0 } t ? t : Id;

    public static readonly string[] AppVariables = ["${appID}", "${appName}", "${publisher}"];

    public bool UsesAppVariables =>
        (Match.ForEachInstalledApp ?? false)
        || Match.Paths.Any(p => AppVariables.Any(v => p.Contains(v, StringComparison.Ordinal)))
        || Match.AppIDs is { Count: > 0 } || Match.AppNames is { Count: > 0 } || Match.Publishers is { Count: > 0 };
}

internal sealed class SafetyJsonConverter : JsonConverter<SafetyLevel>
{
    public override SafetyLevel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString() ?? "";
        return SafetyLevelExtensions.ParseRuleValue(raw) ?? throw new JsonException($"safety phải là safe/review/risky, nhận {raw}");
    }

    public override void Write(Utf8JsonWriter writer, SafetyLevel value, JsonSerializerOptions options) => writer.WriteStringValue(value.RuleValue());
}

/// <summary>Danh mục rule (mục 8.4) cho Windows. Tên hiển thị và icon dùng cho nhóm trên UI.</summary>
public static class RuleCategory
{
    public const string UserTemp = "userTemp";
    public const string SystemTemp = "systemTemp";
    public const string UserCaches = "userCaches";
    public const string BrowserCaches = "browserCaches";
    public const string SystemCaches = "systemCaches";
    public const string WindowsUpdate = "windowsUpdate";
    public const string UserLogs = "userLogs";
    public const string SystemLogs = "systemLogs";
    public const string CrashReports = "crashReports";
    public const string DevToolCaches = "devToolCaches";
    public const string IosBackups = "iosBackups";
    public const string IosSoftwareUpdates = "iosSoftwareUpdates";
    public const string Trash = "trash";
    public const string OldDownloads = "oldDownloads";
    public const string AppLeftovers = "appLeftovers";

    public static readonly string[] All =
    [
        UserTemp, SystemTemp, UserCaches, BrowserCaches, SystemCaches, WindowsUpdate, UserLogs, SystemLogs, CrashReports,
        DevToolCaches, IosBackups, IosSoftwareUpdates, Trash, OldDownloads, AppLeftovers,
    ];

    public static LocalizedText Title(string category) => category switch
    {
        UserTemp => new("Tệp tạm", "Temporary Files"),
        SystemTemp => new("Tệp tạm của hệ thống", "System Temporary Files"),
        UserCaches => new("Cache người dùng", "User Cache Files"),
        BrowserCaches => new("Cache trình duyệt", "Browser Caches"),
        SystemCaches => new("Cache hệ thống", "System Cache Files"),
        WindowsUpdate => new("File cập nhật Windows", "Windows Update Files"),
        UserLogs => new("Log người dùng", "User Log Files"),
        SystemLogs => new("Log hệ thống", "System Log Files"),
        CrashReports => new("Báo cáo crash", "Crash Reports"),
        DevToolCaches => new("Cache công cụ lập trình", "Developer Tool Caches"),
        IosBackups => new("Bản sao lưu iOS", "iOS Device Backups"),
        IosSoftwareUpdates => new("Bản cài iOS cũ", "Old iOS Software Updates"),
        Trash => new("Thùng rác", "Recycle Bin"),
        OldDownloads => new("Bản tải về cũ", "Old Downloads"),
        AppLeftovers => new("File sót của app", "App Leftovers"),
        _ => new LocalizedText(category, category),
    };

    public static string Icon(string category) => category switch
    {
        UserTemp or SystemTemp => "clock",
        UserCaches => "archivebox",
        BrowserCaches => "globe",
        SystemCaches => "gearshape.2",
        WindowsUpdate => "arrow.down.app",
        UserLogs or SystemLogs => "doc.text",
        CrashReports => "exclamationmark.triangle",
        DevToolCaches => "chevron.left.forwardslash.chevron.right",
        IosBackups => "iphone",
        IosSoftwareUpdates => "arrow.down.app",
        Trash => "trash",
        OldDownloads => "arrow.down.circle",
        AppLeftovers => "puzzlepiece.extension",
        _ => "folder",
    };
}

/// <summary>Tri thức đi kèm bộ rule (thay cho <c>info.cmmkb</c> của CleanMyMac).</summary>
public sealed record Knowledge
{
    /// <summary>Nhà phát hành của chính Windows: app của họ không bao giờ coi là có file sót để xoá.</summary>
    public List<string> SystemPublishers { get; init; } = ["Microsoft Corporation"];
    /// <summary>Tên thư mục không bao giờ coi là file sót của app đã gỡ (glob).</summary>
    public List<string> OrphanWhitelist { get; init; } = [];
    /// <summary>Tên thư mục cache không bao giờ đề xuất xoá (glob).</summary>
    public List<string> ProtectedCacheNames { get; init; } = [];
    /// <summary>Tên tiến trình hệ thống không bao giờ đề xuất tắt khi gỡ app.</summary>
    public List<string> ProtectedProcesses { get; init; } = [];

    public bool IsSystemPublisher(string? publisher) =>
        !string.IsNullOrEmpty(publisher) && SystemPublishers.Any(p => Glob.ComponentMatches(p, publisher));

    public bool IsWhitelistedOrphan(string name) => OrphanWhitelist.Any(p => Glob.ComponentMatches(p, name));
}

/// <summary>Payload JSON bên trong <c>rules.bundle</c>.</summary>
public sealed record RulePayload
{
    public ulong Version { get; init; }
    public DateTimeOffset? GeneratedAt { get; init; }
    public string Platform { get; init; } = "windows";
    public List<Rule> Rules { get; init; } = [];
    public Knowledge Knowledge { get; init; } = new();
}

public static class RuleJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = false,
    };

    /// <summary>Dùng cho <c>rulepack lint</c>: trường lạ là lỗi (app khi chạy thì bỏ qua để tương thích bộ rule mới hơn).</summary>
    public static readonly JsonSerializerOptions StrictOptions = new(Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}
