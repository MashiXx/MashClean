using CleanBoost.Core;
using CleanBoost.Engine.Nodes;

namespace CleanBoost.Engine.Cleaning;

/// <summary>
/// Kế hoạch dọn (mục 7.2). Lập kế hoạch tách khỏi thực thi để hiển thị chính xác những gì sắp xảy ra,
/// kiểm tra lại PathPolicy ngay trước khi xoá và ghi kế hoạch vào database.
/// </summary>
public sealed class CleanPlan
{
    public sealed record Item
    {
        public required Guid NodeID { get; init; }
        public string? Path { get; init; }
        public required string Title { get; init; }
        public required RemovalStrategy Strategy { get; init; }
        public bool RequiresRoot { get; init; }
        public ByteCount ExpectedSize { get; init; }
        public string? RuleID { get; init; }
        public string? Category { get; init; }
        public SafetyLevel Safety { get; init; } = SafetyLevel.Safe;
        public IReadOnlyList<string> AllowedRoots { get; init; } = [];
        public VirtualItem? Virtual { get; init; }

        /// <summary>Remover chịu trách nhiệm cho mục này.</summary>
        public string RemoverID => Strategy.IsCustom ? Strategy.CustomID! : RequiresRoot ? RemoverIDs.PrivilegedFile : RemoverIDs.File;

        public string Label => Path ?? Title;
    }

    public sealed record Blocked(string Path, PathPolicy.Violation Violation, string Title);

    public Guid Id { get; } = Guid.NewGuid();
    public IReadOnlyList<Item> Items { get; }
    public IReadOnlyList<Blocked> BlockedItems { get; }
    public ByteCount TotalSize { get; }
    /// <summary>Hộp xác nhận nếu có mục review/risky hoặc mục cần quyền quản trị (mục 7.1, 15.2).</summary>
    public bool RequiresConfirmation { get; }
    public string? SessionID { get; }

    public CleanPlan(IReadOnlyList<Item> items, IReadOnlyList<Blocked> blocked, string? sessionID = null)
    {
        Items = items;
        BlockedItems = blocked;
        SessionID = sessionID;
        TotalSize = items.Sum(i => i.ExpectedSize);
        RequiresConfirmation = items.Any(i => i.Safety != SafetyLevel.Safe || i.RequiresRoot);
    }

    public IEnumerable<Item> RootItems => Items.Where(i => i.RequiresRoot);
    public IEnumerable<Item> ReviewItems => Items.Where(i => i.Safety != SafetyLevel.Safe);
    /// <summary>Mục file bị xoá vĩnh viễn (không qua Thùng rác); remover chuyên biệt (bảo trì, khởi động…) không tính.</summary>
    public IEnumerable<Item> PermanentItems => Items.Where(i => !i.Strategy.IsRecoverable && !i.Strategy.IsCustom && i.Path is not null);
    public bool IsEmpty => Items.Count == 0;
}

/// <summary>Báo cáo sau khi dọn.</summary>
public sealed class CleanReport
{
    public sealed record Entry(CleanPlan.Item Item, RemoveResult Result);

    public required string OperationID { get; init; }
    public required IReadOnlyList<Entry> Entries { get; init; }
    /// <summary>Tổng ước tính (cộng từ kết quả xoá).</summary>
    public ByteCount EstimatedFreed { get; init; }
    /// <summary>Đo thực tế từ dung lượng trống của ổ trước/sau (mục 7.5).</summary>
    public ByteCount? MeasuredFreed { get; init; }
    public TimeSpan Duration { get; init; }
    public bool DryRun { get; init; }

    public IEnumerable<Entry> Succeeded => Entries.Where(e => e.Result.IsSuccess);
    public IEnumerable<Entry> Failed => Entries.Where(e => e.Result.IsFailure);
    public IEnumerable<Entry> Skipped => Entries.Where(e => e.Result.Outcome == RemoveOutcomeKind.Skipped && e.Result.Reason != SkipReason.NotFound);
    public IEnumerable<Entry> InUse => Entries.Where(e => e.Result.Reason is SkipReason.InUse or SkipReason.AppRunning);
}

public sealed record CleanProgress(double Fraction, string CurrentItem, ByteCount FreedSoFar);
