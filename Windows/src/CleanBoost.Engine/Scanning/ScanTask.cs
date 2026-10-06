using CleanBoost.Core;
using CleanBoost.Engine.FileSystem;
using CleanBoost.Engine.Nodes;
using CleanBoost.Engine.Rules;

namespace CleanBoost.Engine.Scanning;

public enum ScanPriority { Low = 0, Normal = 1, High = 2 }

/// <summary>Một đơn vị công việc quét (SYSTEM_DESIGN mục 5.1, 5.2).</summary>
public interface IScanTask
{
    string Id { get; }
    IReadOnlyList<string> Dependencies => [];
    ScanPriority Priority => ScanPriority.Normal;
    /// <summary>Trọng số dùng để chia thanh tiến độ tổng (ước lượng).</summary>
    double EstimatedWeight => 1;
    /// <summary>Tên hiển thị khi task đang chạy.</summary>
    string Title => Id;
    /// <summary>Timeout mềm riêng của task (<c>null</c> = mặc định của engine; <c>InfiniteTimeSpan</c> = không giới hạn, cho quét dài có nút Dừng).</summary>
    TimeSpan? Timeout => null;

    Task<ScanOutput> RunAsync(ScanContext context);
}

public enum ScanWarningKind { NeedsAdmin, Timeout, Failed, Skipped, Info }

public sealed record ScanWarning(string TaskID, ScanWarningKind Kind, string Message)
{
    public Guid Id { get; } = Guid.NewGuid();
}

/// <summary>Kết quả của một task (mục 5.2).</summary>
public sealed class ScanOutput
{
    public List<Node> Nodes { get; init; } = [];
    public Dictionary<string, object> Artifacts { get; init; } = [];
    public List<ScanWarning> Warnings { get; init; } = [];

    public static ScanOutput Empty => new();

    public T? Artifact<T>(string key) where T : class => Artifacts.TryGetValue(key, out var v) ? v as T : null;
}

/// <summary>Môi trường dùng chung cho mọi task trong một phiên quét.</summary>
public sealed class ScanEnvironment
{
    public PathPolicy Policy { get; init; } = PathPolicy.CurrentUser;
    public IgnoreList Ignore { get; init; } = IgnoreList.Empty;
    /// <summary>Tên tiến trình đang chạy (chữ thường, không đuôi .exe).</summary>
    public IReadOnlySet<string> RunningProcesses { get; init; } = new HashSet<string>();
    /// <summary>Đường dẫn đầy đủ của file exe đang chạy (đọc được), để biết app nào đang mở.</summary>
    public IReadOnlyList<string> RunningProcessPaths { get; init; } = [];
    /// <summary>App đang chạy với quyền quản trị (đọc được thư mục hệ thống).</summary>
    public bool IsElevated { get; init; }
    public bool ShowRiskyItems { get; init; }
    public DateTime NowUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>Truyền vào task: dịch vụ hệ thống tệp, rule, kết quả task phụ thuộc, reporter tiến độ (mục 5.1).</summary>
public sealed class ScanContext
{
    public required FileSystemService FileSystem { get; init; }
    public required RuleSnapshot Rules { get; init; }
    public required ProgressReporter Progress { get; init; }
    /// <summary>Kết quả của các task phụ thuộc (bắc cầu), tra theo id.</summary>
    public IReadOnlyDictionary<string, ScanOutput> Upstream { get; init; } = new Dictionary<string, ScanOutput>();
    public required ScanEnvironment Environment { get; init; }
    public CancellationToken Cancellation { get; init; }

    /// <summary>Tìm một artifact trong kết quả các task phụ thuộc.</summary>
    public T? UpstreamArtifact<T>(string key) where T : class
    {
        foreach (var output in Upstream.Values)
        {
            if (output.Artifact<T>(key) is { } v) return v;
        }
        return null;
    }

    /// <summary>Ngữ cảnh đánh giá rule, dùng chung bộ đếm file của phiên.</summary>
    public RuleEvaluationContext RuleContext(IReadOnlyList<RuleAppInfo>? apps = null)
    {
        var list = apps ?? [];
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in list)
        {
            if (app.InstallLocation is not { Length: > 3 } dir) continue;
            if (Environment.RunningProcessPaths.Any(p => PathPolicy.IsSameOrInside(p, dir))) running.Add(app.AppID);
        }
        return new RuleEvaluationContext
        {
            FileSystem = FileSystem,
            Policy = Environment.Policy,
            Apps = list,
            RunningProcesses = Environment.RunningProcesses,
            RunningAppIDs = running,
            Ignore = Environment.Ignore,
            NowUtc = Environment.NowUtc,
            Counter = Progress.FileCounter,
            Cancellation = Cancellation,
        };
    }
}
