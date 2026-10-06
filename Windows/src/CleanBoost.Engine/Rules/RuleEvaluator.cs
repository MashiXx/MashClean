using System.Collections.Concurrent;
using CleanBoost.Core;
using CleanBoost.Core.FileSystem;
using CleanBoost.Engine.FileSystem;

namespace CleanBoost.Engine.Rules;

/// <summary>Thông tin app tối thiểu để mở rộng biến của rule (<c>${appID}</c>, <c>${appName}</c>, <c>${publisher}</c>).</summary>
public sealed record RuleAppInfo(string AppID, string AppName, string? Publisher, string? InstallLocation, IReadOnlyList<string> ProcessNames);

/// <summary>Một đường dẫn rule khớp, kèm số đo.</summary>
public sealed record RuleMatch(string Path, PathMeasurement Measurement, Rule Rule, RuleAppInfo? App, string AllowedRoot);

public sealed class RuleEvaluationContext
{
    public required FileSystemService FileSystem { get; init; }
    public PathPolicy Policy { get; init; } = PathPolicy.CurrentUser;
    public IReadOnlyList<RuleAppInfo> Apps { get; init; } = [];
    /// <summary>Tên tiến trình đang chạy (không đuôi .exe, chữ thường).</summary>
    public IReadOnlySet<string> RunningProcesses { get; init; } = new HashSet<string>();
    /// <summary>Định danh app đang chạy (có tiến trình nằm trong thư mục cài đặt).</summary>
    public IReadOnlySet<string> RunningAppIDs { get; init; } = new HashSet<string>();
    public IgnoreList Ignore { get; init; } = IgnoreList.Empty;
    public DateTime NowUtc { get; init; } = DateTime.UtcNow;
    public OSVersion OSVersion { get; init; } = OSVersion.Current;
    public VisitCounter Counter { get; init; } = new();
    public CancellationToken Cancellation { get; init; }
}

/// <summary>Đánh giá rule khi chạy (mục 8.6).</summary>
public static class RuleEvaluator
{
    /// <summary>Trả về các mục khớp của một rule. Rule có <c>provider</c> do scan task chuyên biệt xử lý, ở đây trả rỗng.</summary>
    public static List<RuleMatch> Evaluate(CompiledRule compiled, RuleSnapshot snapshot, RuleEvaluationContext context)
    {
        var rule = compiled.Rule;
        if (rule.Match.Provider is not null) return [];
        if (snapshot.Disabled.Contains(rule.Id) || context.Ignore.IgnoresRule(rule.Id)) return [];
        if (compiled.MinOS is { } minOS && context.OSVersion < minOS) return [];

        var resolver = snapshot.Resolver;
        var candidates = new List<(string Path, RuleAppInfo? App, string Root, GlobSet Excludes)>();

        if (compiled.StaticGlobs is { } globs)
        {
            if (IsBlockedByRunningApp(rule, null, context)) return [];
            foreach (var glob in globs)
            {
                foreach (var path in GlobExpander.Expand(glob, context.Policy)) candidates.Add((path, null, glob.LiteralPrefix, compiled.StaticExcludes));
            }
        }
        else
        {
            foreach (var app in context.Apps)
            {
                if (!compiled.AppliesTo(app)) continue;
                if (context.Ignore.IgnoresApp(app.AppID)) continue;
                if (IsBlockedByRunningApp(rule, app, context)) continue;
                var excludes = new GlobSet((rule.Exclude ?? []).Select(e => Substitute(e, app)).Where(e => e is not null).Select(e => resolver.Glob(e!)));
                foreach (var template in rule.Match.Paths)
                {
                    if (Substitute(template, app) is not { } pattern) continue;
                    var glob = resolver.Glob(pattern);
                    foreach (var path in GlobExpander.Expand(glob, context.Policy)) candidates.Add((path, app, glob.LiteralPrefix, excludes));
                }
            }
        }

        // Lọc trước (trùng, exclude, ignore, vùng cấm), rồi mới đo vì đo là phần tốn I/O.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<(string Path, RuleAppInfo? App, string Root, GlobSet Excludes)>();
        foreach (var c in candidates)
        {
            if (!seen.Add(c.Path)) continue;
            if (c.Excludes.MatchesSelfOrAncestor(c.Path)) continue;
            if (context.Ignore.IgnoresPath(c.Path)) continue;
            // Mọi đường dẫn sau khi mở rộng đều đi qua PathPolicy; trỏ vào vùng cấm thì loại và ghi log (mục 8.6 bước 4).
            if (context.Policy.IsForbidden(c.Path) is { } violation)
            {
                Log.Warning(LogCategory.Rules, $"Rule {rule.Id} trỏ vào vùng cấm, bỏ qua: {violation.Kind}");
                continue;
            }
            pending.Add(c);
        }
        context.Cancellation.ThrowIfCancellationRequested();

        // Đo song song, giới hạn đồng thời theo loại ổ (mục 16.3). Mục không đọc được thì bỏ qua.
        var results = new ConcurrentDictionary<int, RuleMatch>();
        var limit = pending.Count > 1 ? context.FileSystem.RecommendedConcurrency(context.FileSystem.Home) : 1;
        Parallel.For(0, pending.Count, new ParallelOptions { MaxDegreeOfParallelism = limit, CancellationToken = context.Cancellation }, i =>
        {
            var item = pending[i];
            PathMeasurement m;
            try
            {
                m = context.FileSystem.MeasureConcurrently(item.Path, context.Counter,
                    p => item.Excludes.Matches(p) || context.Policy.ShouldSkipDescending(p), context.Cancellation);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                Log.DebugPath(LogCategory.Rules, "Bỏ qua mục không đọc được", item.Path);
                return;
            }
            if (!m.Exists) return;
            if (m.IsDirectory && m.ItemCount == 0 && rule.Removal != RemovalStrategy.Delete) return; // không còn gì để dọn
            if (rule.Match.MinSizeBytes is { } minSize && m.AllocatedSize < minSize) return;
            if (rule.Match.MinAgeDays is { } days && days > 0)
            {
                var cutoff = context.NowUtc.AddDays(-days);
                if (m.LastUseUtc > cutoff) return;
            }
            results[i] = new RuleMatch(item.Path, m, rule, item.App, item.Root);
        });
        context.Cancellation.ThrowIfCancellationRequested();
        return results.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
    }

    /// <summary>
    /// Thay biến theo app. Giá trị biến được kiểm tra để không chèn được <c>\</c>, <c>/</c> hay <c>..</c>
    /// (chống path traversal từ dữ liệu registry do app khác ghi).
    /// </summary>
    public static string? Substitute(string template, RuleAppInfo app)
    {
        var result = template;
        (string Name, string? Value)[] vars = [("${appID}", app.AppID), ("${appName}", app.AppName), ("${publisher}", app.Publisher)];
        foreach (var (name, value) in vars)
        {
            if (!result.Contains(name, StringComparison.Ordinal)) continue;
            if (value is null || !IsSafeComponent(value)) return null;
            result = result.Replace(name, value, StringComparison.Ordinal);
        }
        return result;
    }

    public static bool IsSafeComponent(string value) =>
        value.Length > 0 && value.Trim().Length > 0 && value != "." && value != ".." && value.IndexOfAny(['\\', '/', ':', '\0', '"', '<', '>', '|']) < 0
        && !Glob.HasMagic(value) && !value.EndsWith('.') && !value.EndsWith(' ');

    internal static bool IsBlockedByRunningApp(Rule rule, RuleAppInfo? app, RuleEvaluationContext context)
    {
        if (rule.Conditions?.AppNotRunning is not { Count: > 0 } names) return false;
        foreach (var n in names)
        {
            if (n == "${appID}")
            {
                if (app is not null && context.RunningAppIDs.Contains(app.AppID)) return true;
                continue;
            }
            var resolved = app is not null ? Substitute(n, app) ?? n : n;
            var process = resolved.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? resolved[..^4] : resolved;
            if (context.RunningProcesses.Contains(process.ToLowerInvariant())) return true;
        }
        return false;
    }
}

/// <summary>Mở rộng glob thành đường dẫn có thật trên ổ.</summary>
public static class GlobExpander
{
    /// <summary>Giới hạn độ sâu cho <c>**</c> để không duyệt cả ổ.</summary>
    public const int MaxGlobstarDepth = 8;

    public static List<string> Expand(Glob glob, PathPolicy? policy = null)
    {
        var segments = glob.Segments;
        if (segments.Count == 0) return [];
        var first = segments[0];
        List<string> current;
        if (first.Kind == Glob.SegmentKind.Literal)
        {
            if (first.Text.Length != 2 || first.Text[1] != ':') return [];
            current = [first.Text.ToUpperInvariant() + "\\"];
        }
        else if (first.Kind == Glob.SegmentKind.Wildcard)
        {
            current = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .Select(d => d.Name).Where(n => Glob.ComponentMatches(first.Text, n.TrimEnd('\\'))).ToList();
        }
        else
        {
            return [];
        }
        if (segments.Count == 1) return current;

        for (var i = 1; i < segments.Count; i++)
        {
            var seg = segments[i];
            var isLast = i == segments.Count - 1;
            var next = new List<string>();
            foreach (var basePath in current)
            {
                switch (seg.Kind)
                {
                    case Glob.SegmentKind.Literal:
                    {
                        var p = Join(basePath, seg.Text);
                        // Phần tử cuối có thể là liên kết (vẫn liệt kê để xoá chính liên kết), phần giữa phải là thư mục.
                        if (isLast ? DirectoryReader.Exists(p) : Directory.Exists(p)) next.Add(p);
                        break;
                    }
                    case Glob.SegmentKind.Wildcard:
                        foreach (var child in DirectoryReader.List(basePath))
                        {
                            if (!Glob.ComponentMatches(seg.Text, child.Name)) continue;
                            if (!isLast && !child.IsDirectory) continue;
                            var p = Join(basePath, child.Name);
                            if (policy is not null && child.IsDirectory && policy.ShouldSkipDescending(p)) continue;
                            next.Add(p);
                        }
                        break;
                    default:
                        next.Add(basePath);
                        var frontier = new Stack<(string Dir, int Depth)>();
                        frontier.Push((basePath, 0));
                        while (frontier.Count > 0)
                        {
                            var (dir, depth) = frontier.Pop();
                            if (depth >= MaxGlobstarDepth) continue;
                            foreach (var child in DirectoryReader.List(dir))
                            {
                                if (!child.IsDirectory || child.IsLink) continue;
                                var p = Join(dir, child.Name);
                                if (policy is not null && policy.ShouldSkipDescending(p)) continue;
                                next.Add(p);
                                frontier.Push((p, depth + 1));
                            }
                        }
                        break;
                }
            }
            current = next;
            if (current.Count == 0) break;
        }
        return current.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Join(string basePath, string name) => basePath.EndsWith('\\') ? basePath + name : basePath + "\\" + name;
}
