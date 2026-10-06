using CleanBoost.Core;
using CleanBoost.Core.FileSystem;
using CleanBoost.Engine.Apps;
using CleanBoost.Engine.FileSystem;
using CleanBoost.Engine.Nodes;
using CleanBoost.Engine.Rules;
using CleanBoost.Engine.Scanning;

namespace CleanBoost.Features.Uninstaller;

/// <summary>
/// Quyết định một thư mục trong AppData/ProgramData có phải của app đã gỡ không (OrphanFilter của bản Mac, spec mục 4.5).
/// Windows không có bundle ID nên so theo tên đã chuẩn hoá với mọi dấu vết của app đang cài: tên, nhà phát hành, tên các
/// thư mục trên đường dẫn cài đặt, tên exe, tên gói Store, tiến trình đang chạy. Cố ý bảo thủ: so "chứa nhau" cũng coi là
/// của app đang cài.
/// </summary>
public sealed class OrphanFilter
{
    /// <summary>Thư mục hãng phần mềm (nằm trong whitelist) được xét ở cấp <c>&lt;Hãng&gt;\&lt;App&gt;</c> khi không còn app nào của hãng.</summary>
    public static readonly string[] SoftwareVendorFolders = ["Google", "Mozilla", "Adobe", "Autodesk", "JetBrains", "Unity", "VMware", "Oracle"];

    private readonly HashSet<string> _claimed;
    private readonly HashSet<string> _vendors;
    private readonly Knowledge _knowledge;

    public OrphanFilter(IReadOnlyList<InstalledApp> installed, Knowledge knowledge, IEnumerable<string> runningProcessPaths)
    {
        _knowledge = knowledge;
        _claimed = new HashSet<string>(StringComparer.Ordinal);
        _vendors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var app in installed)
        {
            foreach (var k in LeftoverMatcher.NameKeys(app)) Claim(k);
            var pub = LeftoverMatcher.NormalizePublisher(app.Publisher);
            if (pub.Length >= LeftoverMatcher.MinExactKey)
            {
                Claim(pub);
                _vendors.Add(pub);
            }
            // Từ đầu tiên của nhà phát hành ("Valve Corporation" → "valve", "Riot Games" → "riot").
            if (app.Publisher?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { } first) Claim(LeftoverMatcher.LettersAndDigits(first));
            foreach (var exe in app.ExecutableNames) Claim(LeftoverMatcher.Normalize(exe));
            if (app.InstallLocation is { Length: > 3 } dir) ClaimPath(dir);
            if (AppScanner.ExecutableFrom(app.DisplayIcon) is { } icon) ClaimPath(Path.GetDirectoryName(icon));
            if (UninstallCommand.Split(app.UninstallString ?? "") is { } u && Path.IsPathFullyQualified(u.Exe)) ClaimPath(Path.GetDirectoryName(u.Exe));
            if (app.PackageFamilyName is { } pfn)
            {
                // "SpotifyAB.SpotifyMusic_zpdnekdrzrea0" → "spotifyab", "spotifymusic".
                foreach (var part in pfn.Split('_')[0].Split('.')) Claim(LeftoverMatcher.LettersAndDigits(part));
            }
        }
        foreach (var p in runningProcessPaths)
        {
            Claim(LeftoverMatcher.Normalize(Path.GetFileNameWithoutExtension(p)));
            ClaimPath(Path.GetDirectoryName(p));
        }
    }

    private void Claim(string key)
    {
        if (key.Length >= LeftoverMatcher.MinExactKey && !LeftoverMatcher.GenericNames.Contains(key)) _claimed.Add(key);
    }

    /// <summary>Mọi thành phần thư mục bên dưới gốc ổ / Program Files (vd "Mozilla Firefox", "Google", "Chrome").</summary>
    private void ClaimPath(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        foreach (var part in dir.Split('\\', StringSplitOptions.RemoveEmptyEntries).Skip(1)) Claim(LeftoverMatcher.Normalize(part));
    }

    /// <summary>Tên thuộc về app đang cài (bằng nhau, hoặc chứa nhau với khoá ≥ 4 ký tự).</summary>
    public bool IsClaimed(string normalized)
    {
        if (_claimed.Contains(normalized)) return true;
        foreach (var c in _claimed)
        {
            if (c.Length >= 4 && normalized.Contains(c, StringComparison.Ordinal)) return true;
            if (normalized.Length >= 4 && c.Contains(normalized, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>Tên thư mục (cấp trên cùng) có thể là file sót của app đã gỡ.</summary>
    public bool IsOrphanName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.') || name.StartsWith('{') || name.StartsWith('$')) return false;
        if (_knowledge.IsWhitelistedOrphan(name) || _knowledge.IsSystemPublisher(name)) return false;
        if (name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase)) return false;
        var key = LeftoverMatcher.Normalize(name);
        if (key.Length < LeftoverMatcher.MinExactKey || LeftoverMatcher.GenericNames.Contains(key)) return false;
        if (LooksLikeIdentifier(key)) return false;
        return !IsClaimed(key);
    }

    /// <summary>Thư mục hãng phần mềm có thể xét cấp con: trong danh sách, và không còn app nào của hãng.</summary>
    public bool IsDescendableVendor(string name)
    {
        if (!SoftwareVendorFolders.Any(v => v.Equals(name, StringComparison.OrdinalIgnoreCase))) return false;
        var key = LeftoverMatcher.Normalize(name);
        return !_vendors.Any(v => v == key || v.StartsWith(key, StringComparison.Ordinal)) && !IsClaimed(key);
    }

    /// <summary>Tên giống mã (GUID, chuỗi hex, toàn số): thường là cache bộ cài, không rõ chủ.</summary>
    private static bool LooksLikeIdentifier(string key)
    {
        if (key.All(char.IsAsciiDigit)) return true;
        return key.Length >= 16 && key.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
    }
}

/// <summary>
/// "File sót của app đã gỡ" (OrphanedLeftoversTask của bản Mac, spec mục 4.5): thư mục trong <c>%APPDATA%</c>, <c>%LOCALAPPDATA%</c>,
/// <c>%PROGRAMDATA%</c> (cấp trên cùng và <c>&lt;Hãng&gt;\&lt;App&gt;</c>) không khớp app nào đang cài, không trong whitelist,
/// không đổi trong 60 ngày, từ 1 MB, không chứa file chương trình (.exe). Mọi mục đều <c>review</c>. Rule <c>appLeftovers</c>
/// không bao giờ dùng ở đây.
/// </summary>
public sealed class OrphanedLeftoversTask : IScanTask
{
    public const string TaskID = "orphanedLeftovers";
    public const int MinAgeDays = 60;
    public static readonly long MinSize = 1_000_000;

    public string Id => TaskID;
    public IReadOnlyList<string> Dependencies => [InstalledAppsTask.TaskID];
    public double EstimatedWeight => 2;
    public string Title => Loc.T("File sót của app đã gỡ");

    public Task<ScanOutput> RunAsync(ScanContext context) => Task.Run(() => Run(context), context.Cancellation);

    private ScanOutput Run(ScanContext context)
    {
        var installed = context.InstalledApps();
        // Không có danh sách app thì mọi thứ đều trông như file sót: dừng cho an toàn.
        if (installed.Count == 0)
            return new ScanOutput { Warnings = { new ScanWarning(Id, ScanWarningKind.Skipped, Loc.T("Không đọc được danh sách app, bỏ qua tìm file sót")) } };

        var filter = new OrphanFilter(installed, context.Rules.Knowledge, context.Environment.RunningProcessPaths);
        var resolver = context.Rules.Resolver;
        var policy = context.Environment.Policy;
        var ignore = context.Environment.Ignore;
        var cutoff = context.Environment.NowUtc.AddDays(-MinAgeDays);
        string[] roots = [resolver.Resolve("%APPDATA%"), resolver.Resolve("%LOCALAPPDATA%"), resolver.Resolve("%PROGRAMDATA%")];
        var installDirs = installed.Select(a => a.InstallLocation).Where(d => d is { Length: > 3 }).Cast<string>().ToList();

        var groups = new Dictionary<string, (string Title, List<Node> Nodes)>(StringComparer.Ordinal);
        var order = new List<string>();

        void Consider(string root, string path, string groupKey, string groupTitle)
        {
            if (ignore.IgnoresPath(path) || policy.IsForbidden(path) is not null) return;
            if (installDirs.Any(d => PathPolicy.IsSameOrInside(d, path) || PathPolicy.IsSameOrInside(path, d))) return;
            if (context.Environment.RunningProcessPaths.Any(p => PathPolicy.IsSameOrInside(p, path))) return;
            if (ContainsProgram(context.FileSystem, path, policy, context.Cancellation)) return;
            PathMeasurement m;
            try
            {
                m = context.FileSystem.MeasureConcurrently(path, context.Progress.FileCounter, policy.ShouldSkipDescending, context.Cancellation);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return;
            }
            if (!m.Exists || m.AllocatedSize < MinSize || m.NewestModificationUtc > cutoff) return;
            var node = Node.ForPath(path, m.IsDirectory, path.AbbreviatingHome());
            node.Size = new ByteCount(m.AllocatedSize);
            node.ItemCount = m.ItemCount;
            node.Safety = SafetyLevel.Review;
            node.Reason = new LocalizedText(
                $"Không còn chương trình nào tên \"{groupTitle}\" trên máy, không thay đổi hơn {MinAgeDays} ngày",
                Loc.LookupEnglish("Không còn chương trình nào tên \"%@\" trên máy, không thay đổi hơn %lld ngày")
                    .Replace("%@", groupTitle, StringComparison.Ordinal).Replace("%lld", MinAgeDays.ToString(), StringComparison.Ordinal));
            node.Category = UninstallerCategory.OrphanedLeftovers;
            node.Removal = RemovalStrategy.MoveToTrash;
            node.AllowedRoots = [root];
            // Mốc "không thay đổi" (bộ lọc 60 ngày dựa trên ngày sửa, không dựa trên lần đọc của phần mềm diệt virus...).
            node.LastAccessUtc = m.NewestModificationUtc;
            if (policy.Validate(path, allowedRoots: [root]) is { IsOwnershipOnly: true }) node.Badges.Add(Loc.T("Cần quyền quản trị"));
            if (!groups.TryGetValue(groupKey, out var g))
            {
                groups[groupKey] = g = (groupTitle, []);
                order.Add(groupKey);
            }
            g.Nodes.Add(node);
            context.Progress.AddBytesFound(node.Size);
        }

        for (var i = 0; i < roots.Length; i++)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var root = roots[i];
            if (!Directory.Exists(root)) continue;
            foreach (var e in DirectoryReader.List(root))
            {
                context.Cancellation.ThrowIfCancellationRequested();
                if (!e.IsDirectory || e.IsLink || e.IsSystem) continue;
                var path = Path.Combine(root, e.Name);
                if (filter.IsDescendableVendor(e.Name))
                {
                    foreach (var c in DirectoryReader.List(path))
                    {
                        if (!c.IsDirectory || c.IsLink || c.IsSystem || !filter.IsOrphanName(c.Name)) continue;
                        Consider(root, Path.Combine(path, c.Name), LeftoverMatcher.Normalize(c.Name), $"{c.Name} ({e.Name})");
                    }
                    continue;
                }
                if (!filter.IsOrphanName(e.Name)) continue;
                Consider(root, path, LeftoverMatcher.Normalize(e.Name), e.Name);
            }
            context.Progress.Report((double)(i + 1) / roots.Length);
        }
        if (order.Count == 0) return ScanOutput.Empty;

        var children = new List<Node>();
        foreach (var key in order)
        {
            var (title, nodes) = groups[key];
            var group = Node.Group(new LocalizedText(title, title), "app", UninstallerCategory.OrphanedLeftovers,
                nodes.OrderByDescending(n => n.Size.Bytes), SafetyLevel.Review);
            // "Không thay đổi từ 12/3/2025" thay cho lặp lại tên nhóm gốc.
            var newest = nodes.Select(n => n.LastAccessUtc).Where(d => d is not null).DefaultIfEmpty(null).Max();
            group.Reason = newest is { } when
                ? new LocalizedText("Không thay đổi từ " + when.ToLocalTime().ToString("d", System.Globalization.CultureInfo.GetCultureInfo("vi-VN")),
                    Loc.LookupEnglish("Không thay đổi từ %@").Replace("%@", when.ToLocalTime().ToString("d", System.Globalization.CultureInfo.GetCultureInfo("en-US")), StringComparison.Ordinal))
                : LocalizedText.Source("File sót của app đã gỡ");
            children.Add(group);
        }
        var root2 = Node.Group(new LocalizedText("File sót của app đã gỡ", "Leftovers of Removed Apps"), "puzzlepiece.extension",
            UninstallerCategory.OrphanedLeftovers, children.OrderByDescending(c => c.Size.Bytes), SafetyLevel.Review);
        root2.Reason = LocalizedText.Source("App đã bị xoá nhưng còn để lại dữ liệu. Hãy xem lại trước khi xoá.");
        return new ScanOutput { Nodes = [root2] };
    }

    /// <summary>Thư mục có file chương trình (.exe): có thể là chương trình chưa đăng ký (portable, cài tay), không phải dữ liệu sót.</summary>
    internal static bool ContainsProgram(FileSystemService fs, string path, PathPolicy policy, CancellationToken ct)
    {
        var found = false;
        fs.Walk(path, (full, e) =>
        {
            if (e.IsDirectory)
            {
                if (e.IsLink || policy.ShouldSkipDescending(full)) return WalkDecision.SkipDescendants;
                return WalkDecision.Continue;
            }
            if (e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                return WalkDecision.Stop;
            }
            return WalkDecision.Continue;
        }, ct);
        return found;
    }
}
