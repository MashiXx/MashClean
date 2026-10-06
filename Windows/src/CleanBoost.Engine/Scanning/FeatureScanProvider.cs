using CleanBoost.Core;
using CleanBoost.Core.Native;
using CleanBoost.Engine.Nodes;
using CleanBoost.Engine.Rules;

namespace CleanBoost.Engine.Scanning;

public enum SmartScanCard { Cleanup, Maintenance, Applications }

/// <summary>Một dòng tóm tắt + dung lượng cho màn tổng hợp Smart Scan.</summary>
public sealed record FeatureSummary(string FeatureID, SmartScanCard Card, string Title, string Subtitle, ByteCount Bytes, int ItemCount);

/// <summary>Feature cung cấp scan task cho màn riêng và cho Smart Scan (mục 11.1).</summary>
public interface IFeatureScanProvider
{
    string FeatureID { get; }
    bool IncludeInSmartScan { get; }
    IReadOnlyList<IScanTask> ScanTasks();
    IReadOnlyList<IScanTask> SmartScanTasks() => ScanTasks();
    FeatureSummary Summarize(IReadOnlyList<Node> nodes);
}

/// <summary>Tạo node từ kết quả rule (RuleNodes của bản Mac).</summary>
public static class RuleNodes
{
    public static Node MakeNode(this RuleMatch match, string? title = null, SafetyLevel? safety = null, IEnumerable<string>? badges = null)
    {
        var rule = match.Rule;
        var node = Node.ForPath(match.Path, match.Measurement.IsDirectory, title);
        node.Size = new ByteCount(match.Measurement.AllocatedSize);
        node.ItemCount = match.Measurement.ItemCount;
        node.Safety = safety ?? rule.Safety;
        node.Reason = rule.Reason ?? LocalizedText.Empty;
        node.Removal = rule.Removal;
        node.RequiresRoot = rule.RequiresRoot ?? false;
        node.Category = rule.Category;
        node.RuleID = rule.Id;
        node.AllowedRoots = [match.AllowedRoot];
        node.LastAccessUtc = match.Measurement.LastUseUtc;
        if (badges is not null) node.Badges.AddRange(badges);
        return node;
    }

    public static Node MakeVirtualNode(this Rule rule, VirtualItem item, string? title, ByteCount size, int itemCount = 1, IEnumerable<string>? badges = null)
    {
        var node = Node.ForVirtual(item, title);
        node.Size = size;
        node.ItemCount = itemCount;
        node.Safety = rule.Safety;
        node.Reason = rule.Reason ?? LocalizedText.Empty;
        node.Removal = rule.Removal;
        node.RequiresRoot = rule.RequiresRoot ?? false;
        node.Category = rule.Category;
        node.RuleID = rule.Id;
        if (badges is not null) node.Badges.AddRange(badges);
        return node;
    }

    public static Node MakeFileNode(this Rule rule, string path, FileSystem.PathMeasurement m, string? title = null, SafetyLevel? safety = null,
        string? allowedRoot = null, IEnumerable<string>? badges = null)
    {
        var node = Node.ForPath(path, m.IsDirectory, title);
        node.Size = new ByteCount(m.AllocatedSize);
        node.ItemCount = m.ItemCount;
        node.Safety = safety ?? rule.Safety;
        node.Reason = rule.Reason ?? LocalizedText.Empty;
        node.Removal = rule.Removal;
        node.RequiresRoot = rule.RequiresRoot ?? false;
        node.Category = rule.Category;
        node.RuleID = rule.Id;
        node.AllowedRoots = allowedRoot is null ? [] : [allowedRoot];
        node.LastAccessUtc = m.LastUseUtc;
        if (badges is not null) node.Badges.AddRange(badges);
        return node;
    }
}

/// <summary>
/// Phát hiện thư mục không đọc được vì thiếu quyền (tương đương kiểm tra TCC của bản Mac): trên Windows là thư mục
/// chỉ quản trị viên đọc được khi app chạy với quyền thường.
/// </summary>
public static class AccessProbe
{
    public static bool IsAccessDenied(string directory)
    {
        try
        {
            Core.FileSystem.DirectoryReader.Read(directory, _ => { });
            return false;
        }
        catch (Core.FileSystem.Win32IOException ex)
        {
            return ex.Code == Win32.ERROR_ACCESS_DENIED;
        }
        catch
        {
            return false;
        }
    }
}
