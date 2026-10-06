using CleanBoost.Core;
using CleanBoost.Core.FileSystem;
using CleanBoost.Engine.Apps;
using CleanBoost.Engine.FileSystem;
using CleanBoost.Engine.Nodes;
using CleanBoost.Engine.Rules;
using CleanBoost.Engine.Scanning;
using Microsoft.Win32;

namespace CleanBoost.Features.Uninstaller;

/// <summary>Tìm file sót trước khi gỡ (xem trước / đặt lại app) hay sau khi trình gỡ đã chạy xong.</summary>
public enum LeftoverScanMode
{
    /// <summary>App còn cài: không đề xuất thư mục cài đặt và lối tắt (trình gỡ sẽ tự xoá).</summary>
    Preview,
    /// <summary>Trình gỡ đã chạy: thêm thư mục cài đặt còn sót, dữ liệu gói Store, lối tắt hỏng.</summary>
    AfterUninstall,
}

/// <summary>
/// Thuật toán tìm file sót của một app (LeftoverFinder.find của bản Mac, spec mục 4.2), bản Windows:
/// <list type="number">
/// <item>Chắc chắn: thư mục cài đặt còn lại, <c>%LOCALAPPDATA%\Packages\&lt;PackageFamilyName&gt;</c>, lối tắt trỏ vào thư mục cài;</item>
/// <item>Cao: thư mục trong AppData/ProgramData trùng tên app hoặc <c>&lt;Hãng&gt;\&lt;App&gt;</c>, lối tắt hỏng trùng tên, rule <c>appLeftovers</c>;</item>
/// <item>Trung bình: khoá <c>HKCU\Software\&lt;App&gt;</c>, <c>HKCU\Software\&lt;Hãng&gt;\&lt;App&gt;</c>, thư mục hãng không rõ nội dung;</item>
/// <item>Thấp: tên gần giống.</item>
/// </list>
/// Không bao giờ đề xuất: thứ thuộc app khác còn cài, thư mục hãng dùng chung khi hãng còn app khác, tên trong
/// <c>knowledge.orphanWhitelist</c>, vùng PathPolicy cấm. App của nhà phát hành hệ thống (Microsoft) chỉ dùng khớp chính xác.
/// </summary>
public sealed class LeftoverFinder
{
    private readonly FileSystemService _fs;
    private readonly RuleSnapshot _rules;
    private readonly ScanEnvironment _env;

    public LeftoverFinder(FileSystemService fileSystem, RuleSnapshot rules, ScanEnvironment environment)
    {
        _fs = fileSystem;
        _rules = rules;
        _env = environment;
    }

    private Knowledge Know => _rules.Knowledge;
    private PathPolicy Policy => _env.Policy;

    private sealed record Candidate(LeftoverConfidence Confidence, string? Path, RegistryKeyItem? Registry, string? Title, Node? Prebuilt, bool FromRule,
        bool RequiresRoot = false);

    /// <summary>Trạng thái của một lần tìm (một app).</summary>
    private sealed class Scope
    {
        public required InstalledApp App { get; init; }
        public required LeftoverScanMode Mode { get; init; }
        public required List<InstalledApp> Others { get; init; }
        /// <summary>Khoá tên của app, chỉ giữ khoá không trùng app khác.</summary>
        public required HashSet<string> AppKeys { get; init; }
        /// <summary>Chỉ tên đầy đủ (không bỏ tiền tố hãng): dùng ở cấp trên cùng, nơi tên ngắn như "hub", "player" quá chung.</summary>
        public required HashSet<string> FullKeys { get; init; }
        public required string PublisherKey { get; init; }
        /// <summary>Còn app khác cùng nhà phát hành: thư mục hãng là dùng chung.</summary>
        public required bool PublisherShared { get; init; }
        public required bool SystemPublisher { get; init; }
        public required HashSet<string> OtherKeys { get; init; }
        public required HashSet<string> OtherPublisherKeys { get; init; }
        public readonly Dictionary<string, Candidate> Collected = new(StringComparer.Ordinal);
    }

    public LeftoverReport Find(InstalledApp app, IReadOnlyList<InstalledApp> installedApps, LeftoverScanMode mode, CancellationToken ct = default)
    {
        if (_env.Ignore.IgnoresApp(app.AppID)) return new LeftoverReport(app, []);
        var others = installedApps.Where(a => a.Id != app.Id).ToList();
        var otherKeys = new HashSet<string>(others.SelectMany(LeftoverMatcher.NameKeys), StringComparer.Ordinal);
        var otherPublishers = new HashSet<string>(others.Select(a => LeftoverMatcher.NormalizePublisher(a.Publisher)).Where(k => k.Length >= LeftoverMatcher.MinExactKey),
            StringComparer.Ordinal);
        var system = Know.IsSystemPublisher(app.Publisher);
        var publisherKey = LeftoverMatcher.NormalizePublisher(app.Publisher);
        if (publisherKey.Length < LeftoverMatcher.MinExactKey) publisherKey = "";
        // App của nhà phát hành hệ thống: chỉ tên đầy đủ (không bỏ tiền tố "Microsoft" để thành tên chung chung).
        var keys = system ? LeftoverMatcher.NameKeys(app).Take(1) : LeftoverMatcher.NameKeys(app);
        var appKeys = new HashSet<string>(keys.Where(k => !otherKeys.Contains(k) && !otherPublishers.Contains(k) && !LeftoverMatcher.GenericNames.Contains(k)),
            StringComparer.Ordinal);
        var scope = new Scope
        {
            App = app,
            Mode = mode,
            Others = others,
            AppKeys = appKeys,
            FullKeys = new HashSet<string>(LeftoverMatcher.NameKeys(app).Take(1).Where(appKeys.Contains), StringComparer.Ordinal),
            PublisherKey = publisherKey,
            PublisherShared = publisherKey.Length > 0 && otherPublishers.Contains(publisherKey),
            SystemPublisher = system,
            OtherKeys = otherKeys,
            OtherPublisherKeys = otherPublishers,
        };

        if (mode == LeftoverScanMode.AfterUninstall)
        {
            InstallDirectoryPass(scope);
            StorePackagePass(scope);
            ct.ThrowIfCancellationRequested();
            ShortcutPass(scope, ct);
        }
        ct.ThrowIfCancellationRequested();
        AppDataNamePass(scope, ct);
        ct.ThrowIfCancellationRequested();
        if (!system) SimilarNamePass(scope);
        RegistryPass(scope);
        ct.ThrowIfCancellationRequested();
        RulePass(scope, ct);
        ct.ThrowIfCancellationRequested();
        return new LeftoverReport(app, BuildItems(scope, ct));
    }

    // MARK: Thư mục gốc

    private string Roaming => _rules.Resolver.Resolve("%APPDATA%");
    private string Local => _rules.Resolver.Resolve("%LOCALAPPDATA%");
    private string LocalLow => _rules.Resolver.Resolve("%LOCALAPPDATALOW%");
    private string ProgramData => _rules.Resolver.Resolve("%PROGRAMDATA%");
    private string LocalPrograms => Path.Combine(Local, "Programs");

    // MARK: Chắc chắn

    /// <summary>Thư mục cài đặt còn lại sau khi gỡ. Tên thư mục không liên quan tới app (vd "D:\Games") thì chỉ ở mức Thấp.</summary>
    private void InstallDirectoryPass(Scope s)
    {
        if (s.App.IsStore || s.App.InstallLocation is not { Length: > 3 } dir || !Directory.Exists(dir)) return;
        var leaf = LeftoverMatcher.Normalize(Path.GetFileName(dir.TrimEnd('\\')));
        var names = LeftoverMatcher.NameKeys(s.App).Concat(s.App.ExecutableNames.Select(LeftoverMatcher.Normalize)).Where(k => k.Length >= 4).ToList();
        var related = leaf.Length >= LeftoverMatcher.MinExactKey && !LeftoverMatcher.GenericNames.Contains(leaf)
                      && (names.Any(k => k == leaf || (leaf.Length >= 4 && k.Contains(leaf, StringComparison.Ordinal)) || leaf.Contains(k, StringComparison.Ordinal))
                          || (s.PublisherKey.Length > 0 && leaf == s.PublisherKey && !s.PublisherShared));
        Add(s, related ? LeftoverConfidence.Certain : LeftoverConfidence.Low, dir);
    }

    /// <summary>Dữ liệu của gói Store (Windows thường tự xoá khi gỡ): đề xuất từng mục con vì chính thư mục gói được PathPolicy bảo vệ.</summary>
    private void StorePackagePass(Scope s)
    {
        if (!s.App.IsStore || s.App.PackageFamilyName is not { Length: > 0 } pfn || !RuleEvaluator.IsSafeComponent(pfn)) return;
        var dir = Path.Combine(Local, "Packages", pfn);
        if (!Directory.Exists(dir)) return;
        foreach (var e in DirectoryReader.List(dir))
        {
            if (e.IsLink) continue;
            var child = Path.Combine(dir, e.Name);
            // Thư mục con rỗng (AC, TempState...) chỉ làm rối danh sách.
            if (e.IsDirectory && DirectoryReader.List(child).Count == 0) continue;
            Add(s, LeftoverConfidence.Certain, child);
        }
    }

    /// <summary>
    /// Lối tắt trong Start Menu và Desktop: trỏ vào thư mục cài đặt / trình gỡ của app → Chắc chắn; trùng tên app mà đích không còn
    /// → Cao. Thư mục Start Menu trùng tên app chỉ chứa lối tắt như vậy → đề xuất cả thư mục.
    /// </summary>
    private void ShortcutPass(Scope s, CancellationToken ct)
    {
        var installDir = s.App.InstallLocation is { Length: > 3 } d && !s.App.IsStore ? d : null;
        var uninstallerExe = UninstallCommand.Split(s.App.UninstallString ?? "")?.Exe;
        if (installDir is null && s.AppKeys.Count == 0) return;

        LeftoverConfidence? Judge(string lnk)
        {
            var target = ShellLink.Target(lnk);
            if (target is not null)
            {
                if (installDir is not null && PathPolicy.IsSameOrInside(target, installDir)) return LeftoverConfidence.Certain;
                if (uninstallerExe is not null && Path.IsPathFullyQualified(uninstallerExe) && target.Equals(uninstallerExe, StringComparison.OrdinalIgnoreCase))
                    return LeftoverConfidence.Certain;
            }
            var name = LeftoverMatcher.Normalize(Path.GetFileNameWithoutExtension(lnk));
            if (name.StartsWith("uninstall", StringComparison.Ordinal)) name = name["uninstall".Length..];
            if (!s.FullKeys.Contains(name)) return null;
            // Đích còn tồn tại ở nơi khác: lối tắt của chương trình khác cùng tên.
            if (target is not null && (File.Exists(target) || Directory.Exists(target))) return null;
            return LeftoverConfidence.High;
        }

        string[] startMenus = [Path.Combine(KnownPaths.StartMenuUser, "Programs"), Path.Combine(KnownPaths.StartMenuCommon, "Programs")];
        foreach (var root in startMenus.Where(Directory.Exists))
        {
            foreach (var e in DirectoryReader.List(root))
            {
                ct.ThrowIfCancellationRequested();
                var path = Path.Combine(root, e.Name);
                if (e.IsLink) continue;
                if (!e.IsDirectory)
                {
                    if (Path.GetExtension(e.Name).Equals(".lnk", StringComparison.OrdinalIgnoreCase) && Judge(path) is { } c) Add(s, c, path);
                    continue;
                }
                // Thư mục nhóm trong Start Menu.
                var links = new List<(string Path, LeftoverConfidence? Conf)>();
                var onlyLinks = true;
                CollectLinks(path, 0, links, ref onlyLinks);
                foreach (var (lnk, conf) in links)
                {
                    if (conf is { } c) Add(s, c, lnk);
                }
                var folderKey = LeftoverMatcher.Normalize(e.Name);
                var folderMatches = s.FullKeys.Contains(folderKey) || (folderKey == s.PublisherKey && !s.PublisherShared && !s.SystemPublisher);
                if (folderMatches && onlyLinks && links.Count > 0 && links.All(l => l.Conf is not null))
                    Add(s, links.Min(l => l.Conf!.Value), path);
            }
        }

        void CollectLinks(string dir, int depth, List<(string, LeftoverConfidence?)> links, ref bool onlyLinks)
        {
            if (depth > 3) { onlyLinks = false; return; }
            foreach (var e in DirectoryReader.List(dir))
            {
                var p = Path.Combine(dir, e.Name);
                if (e.IsLink) { onlyLinks = false; continue; }
                if (e.IsDirectory)
                {
                    CollectLinks(p, depth + 1, links, ref onlyLinks);
                    continue;
                }
                var ext = Path.GetExtension(e.Name).ToLowerInvariant();
                // Lối tắt web (.url) và desktop.ini của hãng đi kèm nhóm: không làm thư mục thành "của người dùng".
                if (ext == ".lnk") links.Add((p, Judge(p)));
                else if (ext is not (".url" or ".ini")) onlyLinks = false;
            }
        }

        string[] desktops = [KnownPaths.Desktop, Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)];
        foreach (var root in desktops.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var e in DirectoryReader.List(root))
            {
                if (e.IsDirectory || e.IsLink || !Path.GetExtension(e.Name).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) continue;
                var path = Path.Combine(root, e.Name);
                if (Judge(path) is { } c) Add(s, c, path);
            }
        }
    }

    // MARK: Cao — tên app trong thư mục dữ liệu chuẩn

    private void AppDataNamePass(Scope s, CancellationToken ct)
    {
        if (s.AppKeys.Count == 0 && s.PublisherKey.Length == 0) return;
        var roots = new List<string> { Roaming, Local, LocalLow, ProgramData };
        if (s.Mode == LeftoverScanMode.AfterUninstall) roots.Add(LocalPrograms);
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var e in DirectoryReader.List(root))
            {
                ct.ThrowIfCancellationRequested();
                if (!e.IsDirectory || e.IsLink) continue;
                var path = Path.Combine(root, e.Name);
                var key = LeftoverMatcher.Normalize(e.Name);
                if (key.Length < LeftoverMatcher.MinExactKey || LeftoverMatcher.GenericNames.Contains(key)) continue;
                var whitelisted = Know.IsWhitelistedOrphan(e.Name);
                if (s.FullKeys.Contains(key))
                {
                    if (!whitelisted) Add(s, LeftoverConfidence.High, path);
                    continue;
                }
                // Thư mục hãng: <Hãng>\<App>. Không bao giờ đi vào thư mục của nhà phát hành hệ thống (Microsoft).
                if (s.SystemPublisher || s.PublisherKey.Length == 0 || key != s.PublisherKey) continue;
                var children = DirectoryReader.List(path).Where(c => !c.IsLink).ToList();
                var matched = new List<string>();
                foreach (var c in children.Where(c => c.IsDirectory))
                {
                    var ck = LeftoverMatcher.Normalize(c.Name);
                    if (!s.AppKeys.Contains(ck) || Know.IsWhitelistedOrphan(c.Name)) continue;
                    matched.Add(Path.Combine(path, c.Name));
                }
                // Thư mục hãng chỉ thuộc app này khi không còn app nào khác của hãng và không nằm trong whitelist.
                var vendorOwned = !s.PublisherShared && !whitelisted;
                if (vendorOwned && matched.Count > 0 && matched.Count == children.Count)
                    Add(s, LeftoverConfidence.High, path);
                else if (vendorOwned && matched.Count == 0 && children.Count > 0)
                    Add(s, LeftoverConfidence.Medium, path);
                else
                    foreach (var m in matched) Add(s, LeftoverConfidence.High, m);
            }
        }
    }

    // MARK: Thấp — tên gần giống

    private void SimilarNamePass(Scope s)
    {
        var longKeys = s.FullKeys.Where(k => k.Length >= LeftoverMatcher.MinSimilarKey).ToList();
        if (longKeys.Count == 0) return;
        foreach (var root in new[] { Roaming, Local, LocalLow }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var e in DirectoryReader.List(root))
            {
                if (!e.IsDirectory || e.IsLink || Know.IsWhitelistedOrphan(e.Name)) continue;
                var path = Path.Combine(root, e.Name);
                if (s.Collected.ContainsKey(NodeTree.PathKey(path))) continue;
                var key = LeftoverMatcher.Normalize(e.Name);
                if (LeftoverMatcher.GenericNames.Contains(key) || !longKeys.Any(k => LeftoverMatcher.IsSimilar(key, k))) continue;
                // Trông giống app khác (hoặc hãng khác) thì bỏ.
                if (s.OtherKeys.Any(o => o.Length >= 4 && (key.Contains(o, StringComparison.Ordinal) || o.Contains(key, StringComparison.Ordinal)))) continue;
                if (s.OtherPublisherKeys.Any(p => p.Length >= 4 && p != s.PublisherKey && key.Contains(p, StringComparison.Ordinal))) continue;
                Add(s, LeftoverConfidence.Low, path);
            }
        }
    }

    // MARK: Trung bình — registry HKCU\Software

    private static readonly HashSet<string> s_registryNeverTop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Classes", "Policies", "Wow6432Node", "Clients", "RegisteredApplications", "AppDataLow", "ODBC", "Windows", "CleanBoost",
    };

    /// <summary>Khoá registry trùng tên chính xác: <c>HKCU\Software\&lt;App&gt;</c> và <c>HKCU\Software\&lt;Hãng&gt;\&lt;App&gt;</c> (xem lại).</summary>
    private void RegistryPass(Scope s)
    {
        if (s.AppKeys.Count == 0) return;
        try
        {
            using var software = Registry.CurrentUser.OpenSubKey("Software");
            if (software is null) return;
            foreach (var name in software.GetSubKeyNames())
            {
                if (s_registryNeverTop.Contains(name) || name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                var key = LeftoverMatcher.Normalize(name);
                if (key.Length < LeftoverMatcher.MinExactKey) continue;
                if (s.FullKeys.Contains(key))
                {
                    if (!Know.IsWhitelistedOrphan(name)) AddRegistry(s, @"Software\" + name);
                    continue;
                }
                if (s.SystemPublisher || s.PublisherKey.Length == 0 || key != s.PublisherKey) continue;
                using var vendor = software.OpenSubKey(name);
                if (vendor is null) continue;
                foreach (var child in vendor.GetSubKeyNames())
                {
                    if (s.AppKeys.Contains(LeftoverMatcher.Normalize(child)) && RuleEvaluator.IsSafeComponent(child))
                        AddRegistry(s, $@"Software\{name}\{child}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory.Scan, "Không đọc được HKCU\\Software: " + ex.Message);
        }
    }

    private void AddRegistry(Scope s, string keyPath)
    {
        var item = new RegistryKeyItem("HKCU", keyPath);
        if (!RegistryKeyRemover.IsAllowed(item, out _)) return;
        var dedupe = "REG:" + keyPath.ToUpperInvariant();
        if (!s.Collected.ContainsKey(dedupe)) s.Collected[dedupe] = new Candidate(LeftoverConfidence.Medium, null, item, item.DisplayPath, null, false);
    }

    // MARK: Rule appLeftovers

    /// <summary>
    /// Rule <c>appLeftovers</c> chỉ đánh giá cho đúng app đang gỡ (rule cố ý khớp cả thư mục dữ liệu như <c>Google\Chrome</c>).
    /// Đường dẫn mà rule cũng khớp cho một app khác còn cài (rule dùng chung của hãng) thì bỏ.
    /// </summary>
    private void RulePass(Scope s, CancellationToken ct)
    {
        var rules = _rules.RulesIn(UninstallerCategory.AppLeftovers);
        if (rules.Count == 0) return;
        var info = s.App.RuleInfo;
        var context = new RuleEvaluationContext
        {
            FileSystem = _fs,
            Policy = Policy,
            Apps = [info],
            RunningProcesses = _env.RunningProcesses,
            RunningAppIDs = IsRunning(s.App) ? new HashSet<string> { s.App.AppID } : new HashSet<string>(),
            Ignore = _env.Ignore,
            NowUtc = _env.NowUtc,
            Cancellation = ct,
        };
        foreach (var compiled in rules)
        {
            ct.ThrowIfCancellationRequested();
            // Rule tĩnh (không lọc theo app) không phải file sót của riêng app nào.
            if (compiled.StaticGlobs is not null || !compiled.AppliesTo(info)) continue;
            var shared = SharedRulePaths(compiled, s);
            List<RuleMatch> matches;
            try
            {
                matches = RuleEvaluator.Evaluate(compiled, _rules, context);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warning(LogCategory.Rules, $"Rule {compiled.Id} lỗi: {ex.Message}");
                continue;
            }
            foreach (var m in matches)
            {
                if (shared.Contains(NodeTree.PathKey(m.Path))) continue;
                if (Accept(s, m.Path) is not { } requiresRoot) continue;
                var node = m.MakeNode(FriendlyTitle(m.Path), badges: [LeftoverConfidence.High.Title()]);
                node.Category = UninstallerCategory.AppLeftovers;
                if (requiresRoot)
                {
                    node.RequiresRoot = true;
                    if (node.Removal.Kind == RemovalKind.MoveToTrash) node.Removal = RemovalStrategy.Delete;
                    // Xoá vĩnh viễn qua helper: không chọn sẵn.
                    node.Safety = node.Safety.Raised(SafetyLevel.Review);
                    node.Badges.Add(Loc.T("Cần quyền quản trị"));
                }
                if (node.Reason.IsEmpty) node.Reason = Reason(LeftoverConfidence.High, s.App);
                Collect(s, new Candidate(LeftoverConfidence.High, m.Path, null, null, node, true));
            }
        }
    }

    /// <summary>Đường dẫn rule cũng khớp cho app khác còn cài.</summary>
    private HashSet<string> SharedRulePaths(CompiledRule compiled, Scope s)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var other in s.Others)
        {
            var info = other.RuleInfo;
            if (!compiled.AppliesTo(info)) continue;
            foreach (var template in compiled.Rule.Match.Paths)
            {
                if (RuleEvaluator.Substitute(template, info) is not { } pattern) continue;
                foreach (var p in GlobExpander.Expand(_rules.Resolver.Glob(pattern), Policy)) result.Add(NodeTree.PathKey(p));
            }
        }
        return result;
    }

    private bool IsRunning(InstalledApp app) =>
        app.InstallLocation is { Length: > 3 } dir && _env.RunningProcessPaths.Any(p => PathPolicy.IsSameOrInside(p, dir));

    // MARK: Thu thập và lọc

    private void Add(Scope s, LeftoverConfidence confidence, string path)
    {
        if (Accept(s, path) is not { } requiresRoot) return;
        Collect(s, new Candidate(confidence, path, null, null, null, false, requiresRoot));
    }

    /// <summary>Cùng đường dẫn: mức tin cậy tốt hơn thắng; bằng nhau thì rule thắng (tác giả rule biết rõ mức an toàn).</summary>
    private static void Collect(Scope s, Candidate c)
    {
        var key = NodeTree.PathKey(c.Path!);
        if (s.Collected.TryGetValue(key, out var existing))
        {
            var better = c.Confidence < existing.Confidence || (c.Confidence == existing.Confidence && c.FromRule && !existing.FromRule);
            if (!better) return;
        }
        s.Collected[key] = c;
    }

    /// <summary>
    /// Đường dẫn có được đề xuất không. Trả về null nếu không; ngược lại cho biết có cần quyền quản trị (qua helper) không.
    /// </summary>
    private bool? Accept(Scope s, string path)
    {
        if (Policy.IsForbidden(path) is not null || _env.Ignore.IgnoresPath(path)) return null;
        foreach (var other in s.Others)
        {
            if (other.InstallLocation is not { Length: > 3 } d) continue;
            if (PathPolicy.IsSameOrInside(path, d) || PathPolicy.IsSameOrInside(d, path)) return null;
        }
        if (s.Mode == LeftoverScanMode.Preview && s.App.InstallLocation is { Length: > 3 } own
            && (PathPolicy.IsSameOrInside(path, own) || PathPolicy.IsSameOrInside(own, path))) return null;
        // Có chương trình (không phải của app này) đang chạy từ bên trong: đang được dùng.
        var ownDir = s.App.InstallLocation;
        if (_env.RunningProcessPaths.Any(p => PathPolicy.IsSameOrInside(p, path) && !(ownDir is { Length: > 3 } && PathPolicy.IsSameOrInside(p, ownDir)))) return null;
        var parent = Path.GetDirectoryName(path.TrimEnd('\\'));
        IReadOnlyCollection<string>? roots = parent is null ? null : [parent];
        var violation = Policy.Validate(path, PathPolicy.Intent.RemoveItem, roots);
        if (violation is null) return false;
        if (violation.IsOwnershipOnly && PathPolicy.Root.Validate(path, PathPolicy.Intent.RemoveItem, roots) is null) return true;
        return null;
    }

    private List<LeftoverItem> BuildItems(Scope s, CancellationToken ct)
    {
        // Thư mục cha và con cùng được đề xuất: giữ mục có mức tin cậy tốt hơn (bằng nhau thì giữ thư mục cha).
        var paths = s.Collected.Values.Where(c => c.Path is not null).ToList();
        var dropped = new HashSet<Candidate>();
        foreach (var a in paths)
        {
            foreach (var b in paths)
            {
                if (ReferenceEquals(a, b) || dropped.Contains(a) || dropped.Contains(b)) continue;
                if (!PathPolicy.IsSameOrInside(b.Path!, a.Path!) || NodeTree.PathKey(a.Path!) == NodeTree.PathKey(b.Path!)) continue;
                // a là tổ tiên của b.
                if (a.Confidence <= b.Confidence) dropped.Add(b);
                else dropped.Add(a);
            }
        }
        var regs = s.Collected.Values.Where(c => c.Registry is not null).ToList();
        foreach (var a in regs)
        {
            foreach (var b in regs)
            {
                if (ReferenceEquals(a, b)) continue;
                if (b.Registry!.KeyPath.StartsWith(a.Registry!.KeyPath + "\\", StringComparison.OrdinalIgnoreCase)) dropped.Add(b);
            }
        }

        var items = new List<LeftoverItem>();
        foreach (var c in s.Collected.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (dropped.Contains(c)) continue;
            var node = c.Prebuilt ?? (c.Registry is { } reg ? RegistryNode(reg, c.Confidence, s.App) : PathNode(c, s, ct));
            if (node is not null) items.Add(new LeftoverItem(c.Confidence, node));
        }
        return items.OrderBy(i => i.Confidence).ThenByDescending(i => i.Node.Size.Bytes).ToList();
    }

    private Node? PathNode(Candidate c, Scope s, CancellationToken ct)
    {
        var path = c.Path!;
        var requiresRoot = c.RequiresRoot;
        PathMeasurement m;
        try
        {
            m = _fs.MeasureConcurrently(path, skip: Policy.ShouldSkipDescending, ct: ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
        if (!m.Exists) return null;
        var node = Node.ForPath(path, m.IsDirectory, c.Title ?? FriendlyTitle(path));
        node.Size = new ByteCount(m.AllocatedSize);
        node.ItemCount = m.ItemCount;
        node.Safety = c.Confidence.Safety();
        node.Reason = Reason(c.Confidence, s.App);
        node.Category = UninstallerCategory.AppLeftovers;
        node.RequiresRoot = requiresRoot;
        node.Removal = requiresRoot ? RemovalStrategy.Delete : RemovalStrategy.MoveToTrash;
        // Mục cần quyền quản trị bị xoá vĩnh viễn (không qua Thùng rác) và có thể chứa dữ liệu người dùng (save game, cấu hình):
        // không chọn sẵn, trừ lối tắt.
        if (requiresRoot && !Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            node.Safety = node.Safety.Raised(SafetyLevel.Review);
        node.AllowedRoots = Path.GetDirectoryName(path.TrimEnd('\\')) is { } parent ? [parent] : [];
        node.LastAccessUtc = m.LastUseUtc;
        node.Badges.Add(c.Confidence.Title());
        if (requiresRoot) node.Badges.Add(Loc.T("Cần quyền quản trị"));
        if (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) node.Badges.Add(Loc.T("Lối tắt"));
        return node;
    }

    private static Node RegistryNode(RegistryKeyItem item, LeftoverConfidence confidence, InstalledApp app)
    {
        var node = Node.ForVirtual(item, item.DisplayPath);
        node.Size = new ByteCount(EstimateRegistrySize(item.KeyPath));
        node.ItemCount = 1;
        node.Safety = confidence.Safety();
        node.Reason = Reason(confidence, app);
        node.Category = UninstallerCategory.AppLeftovers;
        node.Removal = RemovalStrategy.Custom(RemoverIDs.RegistryKey);
        node.Badges.Add(confidence.Title());
        node.Badges.Add(Loc.T("Khoá registry, có sao lưu .reg"));
        node.Icon = "key";
        return node;
    }

    /// <summary>Ước lượng dung lượng khoá registry: tổng tên và dữ liệu giá trị (tối đa 2.000 khoá con).</summary>
    internal static long EstimateRegistrySize(string keyPath)
    {
        long total = 0;
        var visited = 0;
        void Visit(RegistryKey key, int depth)
        {
            if (visited++ > 2000 || depth > 12) return;
            foreach (var name in key.GetValueNames())
            {
                total += name.Length * 2 + 16;
                try
                {
                    total += key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch
                    {
                        byte[] b => b.Length,
                        string str => str.Length * 2,
                        string[] arr => arr.Sum(x => x.Length * 2 + 2),
                        int => 4,
                        long => 8,
                        _ => 0,
                    };
                }
                catch
                {
                    // giá trị không đọc được
                }
            }
            foreach (var sub in key.GetSubKeyNames())
            {
                total += sub.Length * 2 + 32;
                try
                {
                    using var child = key.OpenSubKey(sub);
                    if (child is not null) Visit(child, depth + 1);
                }
                catch
                {
                    // không có quyền đọc
                }
            }
        }
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(keyPath);
            if (root is not null) Visit(root, 0);
        }
        catch
        {
            return 0;
        }
        return total;
    }

    private static LocalizedText Reason(LeftoverConfidence confidence, InstalledApp app) =>
        new(confidence.ExplanationKey() + $" ({app.Name})", Loc.LookupEnglish(confidence.ExplanationKey()) + $" ({app.Name})");

    /// <summary>Tên hiển thị: đường dẫn rút gọn theo thư mục chuẩn ("AppData\Roaming\Foo", "Start Menu\Programs\Foo.lnk").</summary>
    public string FriendlyTitle(string path)
    {
        (string Root, string Label)[] map =
        [
            (KnownPaths.StartMenuUser, "Start Menu"),
            (KnownPaths.StartMenuCommon, "Start Menu"),
            (LocalPrograms, @"AppData\Local\Programs"),
            (Roaming, @"AppData\Roaming"),
            (LocalLow, @"AppData\LocalLow"),
            (Local, @"AppData\Local"),
            (ProgramData, "ProgramData"),
            (KnownPaths.Desktop, "Desktop"),
            (KnownPaths.ProgramFiles, "Program Files"),
            (KnownPaths.ProgramFilesX86, "Program Files (x86)"),
        ];
        foreach (var (root, label) in map.Where(m => !string.IsNullOrEmpty(m.Root)).OrderByDescending(m => m.Root.Length))
        {
            if (PathPolicy.IsSameOrInside(path, root) && path.Length > root.TrimEnd('\\').Length)
                return label + path[root.TrimEnd('\\').Length..];
        }
        return path.AbbreviatingHome();
    }
}
