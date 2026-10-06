using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Principal;
using CleanBoost.Core.FileSystem;
using CleanBoost.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace CleanBoost.Core;

/// <summary>
/// Module duy nhất quyết định đường dẫn nào được phép xoá (SYSTEM_DESIGN mục 15.1), bản Windows.
/// Cả app lẫn helper quản trị dùng chung; helper chạy lại kiểm tra ở chế độ <see cref="PolicyMode.Root"/>, không tin kết quả từ app.
/// </summary>
public sealed class PathPolicy
{
    public enum PolicyMode
    {
        /// <summary>Quyền người dùng: file phải thuộc người dùng hiện tại (hoặc nhóm Administrators trong hồ sơ của họ).</summary>
        User,
        /// <summary>Helper quản trị: chỉ thao tác trong danh sách vùng hệ thống được phép.</summary>
        Root,
    }

    public enum Intent
    {
        /// <summary>Xoá chính đường dẫn này.</summary>
        RemoveItem,
        /// <summary>Giữ thư mục, xoá nội dung (<c>deleteContents</c>); cho phép nhắm vào thư mục "vỏ" như <c>%TEMP%</c>.</summary>
        RemoveContents,
    }

    public enum ViolationKind
    {
        NotAbsolute,
        NetworkPath,
        ForbiddenZone,
        ProtectedDirectory,
        OutsideAllowedRoots,
        ReadOnlyVolume,
        NotOwnedByUser,
        NotInRootAllowlist,
        UntrustedParent,
        NeedsAdminHelper,
    }

    public sealed class Violation : Exception
    {
        public ViolationKind Kind { get; }
        public string Path { get; }
        public string? Detail { get; }

        public Violation(ViolationKind kind, string path, string? detail = null) : base(Describe(kind, path, detail))
        {
            Kind = kind;
            Path = path;
            Detail = detail;
        }

        /// <summary>Vi phạm chỉ do quyền sở hữu: có thể thử lại qua helper (mục 7.4).</summary>
        public bool IsOwnershipOnly => Kind == ViolationKind.NotOwnedByUser;

        public string LocalizedDescription => Describe(Kind, Path, Detail);

        private static string Describe(ViolationKind kind, string path, string? detail) => kind switch
        {
            ViolationKind.NotAbsolute => Loc.F("Đường dẫn không tuyệt đối: %@", path),
            ViolationKind.NetworkPath => Loc.F("Ổ mạng, không hỗ trợ xoá: %@", path),
            ViolationKind.ForbiddenZone => Loc.F("Nằm trong vùng cấm (%@): %@", detail ?? "", path),
            ViolationKind.ProtectedDirectory => Loc.F("Thư mục được bảo vệ (%@): %@", detail ?? "", path),
            ViolationKind.OutsideAllowedRoots => Loc.F("Đường dẫn ra ngoài vùng rule cho phép sau khi giải liên kết: %@", path),
            ViolationKind.ReadOnlyVolume => Loc.F("Nằm trên ổ chỉ đọc: %@", path),
            ViolationKind.NotOwnedByUser => Loc.F("Không thuộc người dùng hiện tại (%@): %@", detail ?? "", path),
            ViolationKind.NotInRootAllowlist => Loc.F("Không nằm trong vùng helper được phép: %@", path),
            ViolationKind.NeedsAdminHelper => Loc.F("Cần quyền quản trị: %@", path),
            _ => Loc.F("Thư mục cha không an toàn (%@): %@", detail ?? "", path),
        };
    }

    /// <summary>Vùng cấm, có thể kèm ngoại lệ (vd <c>%WINDIR%</c> cấm trừ <c>%WINDIR%\Temp</c>).</summary>
    private sealed record ForbiddenRule(Glob Glob, Glob[] Exceptions);

    public PolicyMode Mode { get; }
    public SecurityIdentifier? UserSid { get; }
    public string HomePattern { get; }

    private readonly bool _userIsAdminMember;
    private readonly ForbiddenRule[] _forbidden;
    private readonly Glob[] _protectedExact;
    private readonly Glob[] _protectedShells;
    private readonly Glob[] _rootAllowlist;
    private readonly string[] _containedGuards;
    private readonly ForbiddenRule[] _forbiddenLiteral;
    private readonly ForbiddenRule[] _forbiddenPattern;

    private static readonly Lazy<PathPolicy> s_user = new(() => ForCurrentUser());
    private static readonly Lazy<PathPolicy> s_root = new(() => new PathPolicy(PolicyMode.Root, null, false, Glob.EscapeLiteral(KnownPaths.UsersRoot) + @"\*", [], homeIsPattern: true));

    /// <summary>Chính sách người dùng hiện tại.</summary>
    public static PathPolicy CurrentUser => s_user.Value;

    /// <summary>Chính sách phía helper quản trị.</summary>
    public static PathPolicy Root => s_root.Value;

    public static PathPolicy ForCurrentUser(string? home = null)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var isAdminMember = identity.Groups?.Any(g => g.Value == "S-1-5-32-544") ?? false;
        return new PathPolicy(PolicyMode.User, identity.User, isAdminMember, home ?? KnownPaths.UserHome, KnownPaths.UserKnownFolders);
    }

    /// <param name="home">Thư mục home thật (hoặc mẫu <c>Users\*</c> khi <paramref name="homeIsPattern"/>).</param>
    /// <param name="homeIsPattern"><c>true</c> khi <paramref name="home"/> đã là mẫu glob (chế độ quản trị bao mọi tài khoản).</param>
    public PathPolicy(PolicyMode mode, SecurityIdentifier? userSid, bool userIsAdminMember, string home, IReadOnlyList<string> extraProtected,
        bool homeIsPattern = false)
    {
        Mode = mode;
        UserSid = userSid;
        _userIsAdminMember = userIsAdminMember;
        HomePattern = home;
        // Đường dẫn thật ghép vào mẫu phải được escape: thư mục tên "[Work]" không được biến thành lớp ký tự.
        static string E(string literal) => Glob.EscapeLiteral(literal);
        var win = E(KnownPaths.Windows);
        var pf = E(KnownPaths.ProgramFiles);
        var pf86 = E(KnownPaths.ProgramFilesX86);
        var pd = E(KnownPaths.ProgramData);
        var sys = E(KnownPaths.SystemDrive);
        var users = E(KnownPaths.UsersRoot);
        var homeGlob = homeIsPattern ? home : E(home);
        string[] roots = [homeGlob];

        Glob G(string p) => new(p, homeGlob);

        // Luôn cấm: không rule nào vượt qua được. Ngoại lệ trong %WINDIR% là danh sách cố định, đã review.
        var windowsExceptions = new[]
        {
            win + @"\Temp", win + @"\SoftwareDistribution\Download", win + @"\SoftwareDistribution\DataStore\Logs",
            win + @"\Logs", win + @"\Minidump", win + @"\LiveKernelReports", win + @"\MEMORY.DMP", win + @"\Panther",
            win + @"\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache",
            win + @"\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Logs",
            win + @"\Downloaded Program Files", win + @"\SystemTemp",
        }.Select(G).ToArray();
        var forbidden = new List<ForbiddenRule> { new(G(win), windowsExceptions) };
        string[] subtrees =
        [
            @"*:\System Volume Information", @"*:\$Recycle.Bin", @"*:\Recovery", @"*:\Boot", @"*:\EFI",
            @"*:\pagefile.sys", @"*:\hiberfil.sys", @"*:\swapfile.sys", @"*:\bootmgr", @"*:\BOOTNXT", @"*:\DumpStack.log*",
            @"*:\$WinREAgent", @"*:\$Windows.~BT", @"*:\$Windows.~WS", @"*:\$WINDOWS.~Q", @"*:\$GetCurrent", @"*:\$SysReset",
            @"*:\Config.Msi", @"*:\Windows.old",
            pf + @"\Windows*", pf + @"\Common Files\microsoft shared", pf + @"\Common Files\System", pf + @"\Common Files\Services",
            pf + @"\Internet Explorer", pf + @"\ModifiableWindowsApps", pf + @"\Microsoft Update Health Tools",
            pf86 + @"\Windows*", pf86 + @"\Common Files\microsoft shared", pf86 + @"\Common Files\System", pf86 + @"\Internet Explorer",
            pd + @"\Microsoft\Windows Defender", pd + @"\Microsoft\Windows Defender Advanced Threat Protection", pd + @"\Microsoft\Crypto",
            pd + @"\Microsoft\Protect", pd + @"\Microsoft\Vault", pd + @"\Microsoft\Windows\SystemData", pd + @"\Microsoft\Windows\AppRepository",
            pd + @"\Microsoft\Windows\Containers", pd + @"\Microsoft\Windows NT", pd + @"\Microsoft\Network", pd + @"\Microsoft\Windows\WindowsApps",
            pd + @"\Microsoft\Windows\DeviceMetadataStore", pd + @"\Packages", pd + @"\regid.1991-06.com.microsoft",
            users + @"\Default", users + @"\Default User", users + @"\All Users",
            @"**\*.pst", @"**\*.ost",
        ];
        foreach (var s in subtrees) forbidden.Add(new ForbiddenRule(G(s), []));
        foreach (var r in roots)
        {
            string[] perHome =
            [
                r + @"\NTUSER*", r + @"\ntuser*",
                r + @"\AppData\Local\Microsoft\Windows\UsrClass.dat*",
                r + @"\AppData\Roaming\Microsoft\Credentials", r + @"\AppData\Local\Microsoft\Credentials",
                r + @"\AppData\Roaming\Microsoft\Protect", r + @"\AppData\Roaming\Microsoft\Crypto",
                r + @"\AppData\Roaming\Microsoft\SystemCertificates", r + @"\AppData\Local\Microsoft\Vault",
                r + @"\AppData\Roaming\Microsoft\Vault", r + @"\AppData\Local\Microsoft\WindowsApps",
                r + @"\AppData\Local\Microsoft\Windows\CloudStore", r + @"\AppData\Local\Microsoft\Windows\Shell",
                r + @"\AppData\Local\Microsoft\Windows\Notifications", r + @"\AppData\Local\Microsoft\Windows\WebCache",
                r + @"\AppData\Local\Microsoft\Edge\User Data\*\Login Data*", r + @"\AppData\Local\Google\Chrome\User Data\*\Login Data*",
                r + @"\OneDrive*", r + @"\iCloudDrive", r + @"\iCloud Photos", r + @"\Dropbox*", r + @"\Google Drive*",
                r + @"\My Drive", r + @"\Creative Cloud Files*", r + @"\Box",
            ];
            foreach (var s in perHome) forbidden.Add(new ForbiddenRule(G(s), []));
        }
        _forbidden = forbidden.ToArray();
        _forbiddenLiteral = _forbidden.Where(f => f.Glob.IsLiteral).ToArray();
        _forbiddenPattern = _forbidden.Where(f => !f.Glob.IsLiteral && f.Glob.Segments[0].Kind != Glob.SegmentKind.Globstar).ToArray();

        // Chính thư mục (không phải nội dung) bị cấm xoá.
        var exact = new List<string>
        {
            "*:", win, users, users + @"\Public", users + @"\Public\*", pf, pf86, pf + @"\Common Files", pf86 + @"\Common Files",
            pd, pd + @"\Microsoft", pd + @"\Microsoft\Windows", pd + @"\Microsoft\Windows\Start Menu",
            pd + @"\Microsoft\Windows\Start Menu\Programs", pd + @"\Microsoft\Windows\Start Menu\Programs\Startup",
            sys + @"\PerfLogs", sys + @"\ProgramData", sys + @"\Intel", sys + @"\Drivers",
        };
        foreach (var r in roots)
        {
            exact.AddRange(
            [
                r, r + @"\Documents", r + @"\Desktop", r + @"\Pictures", r + @"\Videos", r + @"\Music", r + @"\Downloads",
                r + @"\Favorites", r + @"\Contacts", r + @"\Links", r + @"\Saved Games", r + @"\Searches", r + @"\3D Objects",
                r + @"\AppData", r + @"\AppData\Local", r + @"\AppData\LocalLow", r + @"\AppData\Roaming",
                r + @"\AppData\Local\Microsoft", r + @"\AppData\Roaming\Microsoft",
                r + @"\AppData\Local\Microsoft\Windows", r + @"\AppData\Roaming\Microsoft\Windows",
                r + @"\AppData\Roaming\Microsoft\Windows\Start Menu", r + @"\AppData\Roaming\Microsoft\Windows\Start Menu\Programs",
                r + @"\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup", r + @"\AppData\Local\Programs",
                r + @"\AppData\Local\Packages\*",
            ]);
        }
        exact.AddRange(extraProtected.Select(E));
        _protectedExact = exact.Select(G).ToArray();

        // Thư mục được bảo vệ dạng đường dẫn thật: không cho xoá một thư mục chứa chúng (vd D:\Data chứa Documents đã chuyển chỗ).
        var guards = new List<string> { KnownPaths.Windows, KnownPaths.UsersRoot, KnownPaths.ProgramFiles, KnownPaths.ProgramFilesX86, KnownPaths.ProgramData };
        if (!homeIsPattern)
        {
            guards.Add(home);
            foreach (var sub in new[] { "Documents", "Desktop", "Pictures", "Videos", "Music", "Downloads", "AppData" }) guards.Add(Path.Combine(home, sub));
        }
        guards.AddRange(extraProtected);
        _containedGuards = guards.Where(g => !string.IsNullOrEmpty(g)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        // Thư mục "vỏ": không được xoá chính nó, nhưng được xoá nội dung (deleteContents).
        var shells = new List<string>
        {
            win + @"\Temp", win + @"\SoftwareDistribution\Download", win + @"\Logs", win + @"\Minidump", win + @"\LiveKernelReports",
            win + @"\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache",
            pd + @"\Microsoft\Windows\WER", pd + @"\Microsoft\Windows\WER\ReportArchive", pd + @"\Microsoft\Windows\WER\ReportQueue",
            pd + @"\Microsoft\Windows\WER\Temp", pd + @"\Package Cache",
        };
        foreach (var r in roots)
        {
            shells.AddRange(
            [
                r + @"\AppData\Local\Temp", r + @"\AppData\Local\CrashDumps", r + @"\AppData\Local\D3DSCache",
                r + @"\AppData\Local\Microsoft\Windows\INetCache", r + @"\AppData\Local\Microsoft\Windows\Explorer",
                r + @"\AppData\Local\Microsoft\Windows\WER", r + @"\AppData\Local\Microsoft\Windows\WER\ReportArchive",
                r + @"\AppData\Local\Microsoft\Windows\WER\ReportQueue", r + @"\AppData\Local\Packages",
                r + @"\AppData\Local\Microsoft\Windows\Start Menu", r + @"\.nuget", r + @"\.gradle", r + @"\.m2", r + @"\.cargo",
                r + @"\AppData\Local\Microsoft\Edge\User Data", r + @"\AppData\Local\Google\Chrome\User Data",
            ]);
        }
        _protectedShells = shells.Select(G).ToArray();

        // Vùng helper quản trị được phép thao tác (luôn là con, không phải chính thư mục).
        _rootAllowlist = new[]
        {
            win + @"\Temp\**", win + @"\SoftwareDistribution\Download\**", win + @"\SoftwareDistribution\DataStore\Logs\**",
            win + @"\Logs\**", win + @"\Minidump\**", win + @"\LiveKernelReports\**", win + @"\MEMORY.DMP", win + @"\Panther\**",
            win + @"\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache\**",
            win + @"\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Logs\**",
            win + @"\Downloaded Program Files\**", win + @"\SystemTemp\**",
            pd + @"\**", pf + @"\**", pf86 + @"\**",
            users + @"\*\AppData\**",
        }.Select(G).ToArray();
    }

    // MARK: Kiểm tra

    /// <summary>
    /// Kiểm tra một đường dẫn, trả về đường dẫn đã chuẩn hoá (bỏ <c>..</c>, giải liên kết của thư mục cha).
    /// </summary>
    /// <param name="allowedRoots">Vùng rule cho phép; sau khi chuẩn hoá đường dẫn phải vẫn nằm trong đó.</param>
    /// <param name="checkOwnership">Với chế độ người dùng, file phải thuộc người dùng hiện tại.</param>
    public string Check(string path, Intent intent = Intent.RemoveItem, IReadOnlyCollection<string>? allowedRoots = null, bool checkOwnership = true)
    {
        if (IsNetworkPath(path)) throw new Violation(ViolationKind.NetworkPath, path);
        var canonical = Canonicalize(path) ?? throw new Violation(ViolationKind.NotAbsolute, path);
        if (IsNetworkPath(canonical)) throw new Violation(ViolationKind.NetworkPath, canonical);

        if (FindForbidden(canonical) is { } rule) throw new Violation(ViolationKind.ForbiddenZone, canonical, rule.Glob.Pattern);
        if (_protectedExact.FirstOrDefault(g => g.Matches(canonical)) is { } exact)
            throw new Violation(ViolationKind.ProtectedDirectory, canonical, exact.Pattern);
        if (intent == Intent.RemoveItem && _protectedShells.FirstOrDefault(g => g.Matches(canonical)) is { } shell)
            throw new Violation(ViolationKind.ProtectedDirectory, canonical, shell.Pattern);
        // Không xoá (hay xoá nội dung) một thư mục có chứa thư mục được bảo vệ.
        if (_containedGuards.FirstOrDefault(g => g.Length > canonical.Length && IsSameOrInside(g, canonical)) is { } inner)
            throw new Violation(ViolationKind.ProtectedDirectory, canonical, inner);
        // Helper quản trị: không đi qua symlink/junction ở thư mục cha (chống tráo thư mục giữa lúc xác nhận và lúc xoá).
        if (Mode == PolicyMode.Root && LinkAncestor(path) is { } link)
            throw new Violation(ViolationKind.UntrustedParent, canonical, link);

        if (allowedRoots is { Count: > 0 })
        {
            var inside = allowedRoots.Select(r => Canonicalize(r) ?? r).Any(root => IsSameOrInside(canonical, root));
            if (!inside) throw new Violation(ViolationKind.OutsideAllowedRoots, canonical);
        }

        if (IsOnReadOnlyVolume(canonical)) throw new Violation(ViolationKind.ReadOnlyVolume, canonical);

        if (Mode == PolicyMode.User)
        {
            if (checkOwnership && UserSid is not null && Owner(canonical) is { } owner && !IsAcceptableOwner(owner, canonical))
                throw new Violation(ViolationKind.NotOwnedByUser, canonical, DescribeSid(owner));
        }
        else if (!_rootAllowlist.Any(g => g.Matches(canonical)))
        {
            throw new Violation(ViolationKind.NotInRootAllowlist, canonical);
        }
        return canonical;
    }

    /// <summary>Như <see cref="Check"/> nhưng trả về vi phạm thay vì ném lỗi.</summary>
    public Violation? Validate(string path, Intent intent = Intent.RemoveItem, IReadOnlyCollection<string>? allowedRoots = null, bool checkOwnership = true)
    {
        try
        {
            Check(path, intent, allowedRoots, checkOwnership);
            return null;
        }
        catch (Violation v)
        {
            return v;
        }
    }

    /// <summary>Chỉ kiểm tra vùng cấm, không kiểm tra quyền sở hữu hay ổ chỉ đọc. Dùng khi lọc đường dẫn mở rộng từ rule.</summary>
    public Violation? IsForbidden(string path)
    {
        if (IsNetworkPath(path)) return new Violation(ViolationKind.NetworkPath, path);
        var canonical = Canonicalize(path);
        if (canonical is null) return new Violation(ViolationKind.NotAbsolute, path);
        if (FindForbidden(canonical) is { } rule) return new Violation(ViolationKind.ForbiddenZone, canonical, rule.Glob.Pattern);
        if (_protectedExact.FirstOrDefault(g => g.Matches(canonical)) is { } exact) return new Violation(ViolationKind.ProtectedDirectory, canonical, exact.Pattern);
        return null;
    }

    /// <summary>
    /// Thư mục có nằm trong vùng cấm không, để bỏ qua sớm khi duyệt (mục 16.3). Gọi cho từng thư mục khi đi xuống,
    /// nên chỉ cần xét chính thư mục đó (tổ tiên đã được xét trước).
    /// </summary>
    public bool ShouldSkipDescending(string path)
    {
        foreach (var f in _forbiddenLiteral)
        {
            var prefix = f.Glob.Pattern;
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && (path.Length == prefix.Length || path[prefix.Length] == '\\'))
            {
                if (f.Exceptions.Length == 0) return true;
                // Ổ tổ tiên của một ngoại lệ (vd C:\Windows khi đi xuống C:\Windows\Temp) vẫn phải duyệt tiếp.
                if (f.Exceptions.Any(e => e.MatchesSelfOrAncestor(path) || e.CouldMatchDescendant(path))) continue;
                return true;
            }
        }
        foreach (var f in _forbiddenPattern)
        {
            if (f.Glob.Matches(path)) return true;
        }
        return path.EndsWith(".pst", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".ost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Vùng cấm có thể nằm bên trong một thư mục (vd <c>**\*.pst</c>, <c>…\User Data\*\Login Data*</c>): SecureRemover bỏ qua
    /// các mục khớp khi xoá đệ quy.
    /// </summary>
    public IReadOnlyList<Glob> ForbiddenGlobsWithin(string canonicalDirectory) =>
        _forbidden.Where(f => f.Glob.CouldMatchDescendant(canonicalDirectory) && !f.Glob.MatchesSelfOrAncestor(canonicalDirectory))
            .Select(f => f.Glob).ToArray();

    /// <summary>
    /// Vùng cấm cụ thể (không tính mẫu chung bắt đầu bằng <c>**</c>) có thể nằm bên trong thư mục không. Chuyển cả thư mục vào
    /// Thùng rác thì không bỏ qua được từng mục bên trong, nên CleanEngine chặn trường hợp này.
    /// </summary>
    public string? SpecificForbiddenWithin(string canonicalDirectory) =>
        _forbidden.FirstOrDefault(f => f.Glob.Segments.Count > 0 && f.Glob.Segments[0].Kind != Glob.SegmentKind.Globstar
                                       && f.Glob.CouldMatchDescendant(canonicalDirectory) && !f.Glob.MatchesSelfOrAncestor(canonicalDirectory))?.Glob.Pattern;

    /// <summary>Thư mục tổ tiên đầu tiên của đường dẫn là symlink/junction (không tính chính đường dẫn và gốc ổ).</summary>
    public static string? LinkAncestor(string path)
    {
        var normalized = NormalizeSyntax(path);
        if (normalized is null) return null;
        var parts = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (var n = 2; n < parts.Length; n++)
        {
            var ancestor = string.Join('\\', parts, 0, n);
            if (FileSystem.DirectoryReader.Stat(ancestor) is { IsReparsePoint: true, IsLink: true }) return ancestor;
        }
        return null;
    }

    private ForbiddenRule? FindForbidden(string canonical)
    {
        foreach (var f in _forbidden)
        {
            if (!f.Glob.MatchesSelfOrAncestor(canonical)) continue;
            if (f.Exceptions.Length > 0 && f.Exceptions.Any(e => e.MatchesSelfOrAncestor(canonical))) continue;
            return f;
        }
        return null;
    }

    private bool IsAcceptableOwner(SecurityIdentifier owner, string path)
    {
        if (UserSid is not null && owner.Equals(UserSid)) return true;
        // File tạo khi chạy với quyền nâng cao thuộc nhóm Administrators; chấp nhận khi nằm trong hồ sơ của chính người dùng.
        if (_userIsAdminMember && owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) && IsSameOrInside(path, HomePattern)) return true;
        return false;
    }

    // MARK: Tiện ích

    public static bool IsSameOrInside(string path, string root)
    {
        var r = root.TrimEnd('\\');
        if (path.Equals(r, StringComparison.OrdinalIgnoreCase)) return true;
        return path.Length > r.Length && path.StartsWith(r, StringComparison.OrdinalIgnoreCase) && path[r.Length] == '\\';
    }

    public static bool IsNetworkPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\.\", StringComparison.Ordinal)
        || path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Chuẩn hoá cú pháp: bỏ <c>.</c>/<c>..</c>, đổi <c>/</c> thành <c>\</c>, bỏ tiền tố <c>\\?\</c>.
    /// Từ chối luồng dữ liệu phụ (<c>file:stream</c>), ký tự đại diện và thành phần kết thúc bằng dấu chấm hoặc khoảng trắng.
    /// </summary>
    public static string? NormalizeSyntax(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Replace('/', '\\');
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p[4..];
        // "C:" trần là thư mục hiện hành của ổ C (tương đối), không phải gốc ổ.
        if (p.Length < 3 || !char.IsAsciiLetter(p[0]) || p[1] != ':') return null;
        if (p.Length > 2 && p[2] != '\\') return null;
        if (p.IndexOf(':', 2) >= 0 || p.IndexOfAny(['*', '?', '"', '<', '>', '|', '\0']) >= 0) return null;
        var parts = new List<string>();
        foreach (var comp in p[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (comp == ".") continue;
            if (comp == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            if (comp.EndsWith('.') || comp.EndsWith(' ')) return null;
            parts.Add(comp);
        }
        var drive = char.ToUpperInvariant(p[0]) + ":";
        return parts.Count == 0 ? drive + "\\" : drive + "\\" + string.Join('\\', parts);
    }

    /// <summary>
    /// Chuẩn hoá đầy đủ: giải symlink/junction và tên 8.3 của tổ tiên gần nhất đang tồn tại (GetFinalPathNameByHandle).
    /// Thành phần cuối không được giải: nếu là liên kết thì ta xoá chính liên kết, không đụng đích.
    /// </summary>
    public static string? Canonicalize(string path)
    {
        var normalized = NormalizeSyntax(path);
        if (normalized is null) return null;
        var parts = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1) return normalized;
        var tail = new List<string> { parts[^1] };
        var parentCount = parts.Length - 1;
        while (parentCount >= 1)
        {
            var parent = parentCount == 1 ? parts[0] + "\\" : string.Join('\\', parts, 0, parentCount);
            var real = FinalPath(parent);
            if (real is not null)
            {
                // Tên 8.3 (DOCUME~1) của thành phần cuối là bí danh của tên dài: đổi sang tên dài để luật cấm/bảo vệ khớp.
                // FindFirstFile đọc mục trong thư mục cha nên không đi theo liên kết.
                if (tail.Count == 1 && tail[0].Contains('~') && DirectoryReader.Stat(real.TrimEnd('\\') + "\\" + tail[0]) is { Name.Length: > 0 } entry)
                    tail[0] = entry.Name;
                if (IsNetworkPath(real)) return real.TrimEnd('\\') + "\\" + string.Join('\\', tail);
                return real.TrimEnd('\\') + "\\" + string.Join('\\', tail);
            }
            tail.Insert(0, parts[parentCount - 1]);
            parentCount--;
        }
        return normalized;
    }

    /// <summary>Đường dẫn thật của một thư mục đang tồn tại (theo liên kết), dạng <c>C:\...</c> hoặc <c>\\server\share</c>.</summary>
    public static string? FinalPath(string directory)
    {
        using var h = Win32.CreateFileW(LongPath.Prefix(directory), 0, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE | Win32.FILE_SHARE_DELETE,
            IntPtr.Zero, Win32.OPEN_EXISTING, Win32.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid) return null;
        var buffer = new char[1024];
        var n = Win32.GetFinalPathNameByHandleW(h, buffer, (uint)buffer.Length, 0);
        if (n == 0) return null;
        if (n >= buffer.Length)
        {
            buffer = new char[n + 1];
            n = Win32.GetFinalPathNameByHandleW(h, buffer, (uint)buffer.Length, 0);
            if (n == 0 || n >= buffer.Length) return null;
        }
        var s = new string(buffer, 0, (int)n);
        if (s.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + s[8..];
        if (s.StartsWith(@"\\?\", StringComparison.Ordinal)) s = s[4..];
        return s.Length == 2 && s[1] == ':' ? s + "\\" : s;
    }

    /// <summary>Chủ sở hữu của file/thư mục (không theo liên kết).</summary>
    public static SecurityIdentifier? Owner(string path)
    {
        using var h = Win32.CreateFileW(LongPath.Prefix(path), Win32.READ_CONTROL, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE | Win32.FILE_SHARE_DELETE,
            IntPtr.Zero, Win32.OPEN_EXISTING, Win32.FILE_FLAG_BACKUP_SEMANTICS | Win32.FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (h.IsInvalid) return null;
        return OwnerOf(h);
    }

    public static SecurityIdentifier? OwnerOf(SafeFileHandle handle)
    {
        var rc = Win32.GetSecurityInfo(handle, Win32.SE_FILE_OBJECT, Win32.OWNER_SECURITY_INFORMATION, out var owner, out _, out _, out _, out var sd);
        if (rc != 0) return null;
        try { return owner == IntPtr.Zero ? null : new SecurityIdentifier(owner); }
        finally { Win32.LocalFree(sd); }
    }

    public static string DescribeSid(SecurityIdentifier sid)
    {
        try { return sid.Translate(typeof(NTAccount)).Value; } catch { return sid.Value; }
    }

    private static readonly ConcurrentDictionary<string, bool> s_readOnlyVolumes = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsOnReadOnlyVolume(string path)
    {
        var root = VolumeRoot(path);
        if (root is null) return false;
        return s_readOnlyVolumes.GetOrAdd(root, r =>
        {
            if (Win32.GetDriveTypeW(r) == Win32.DRIVE_CDROM) return true;
            return Win32.GetVolumeInformationW(r, null, 0, out _, out _, out var flags, null, 0) && (flags & Win32.FILE_READ_ONLY_VOLUME) != 0;
        });
    }

    /// <summary>Gốc volume chứa đường dẫn (vd <c>C:\</c> hoặc điểm gắn trong thư mục).</summary>
    public static string? VolumeRoot(string path)
    {
        var buffer = new char[1024];
        if (!Win32.GetVolumePathNameW(path, buffer, (uint)buffer.Length))
        {
            var r = Path.GetPathRoot(path);
            return string.IsNullOrEmpty(r) ? null : r;
        }
        var s = new string(buffer).TrimEnd('\0');
        return s.Length == 0 ? null : s;
    }
}

/// <summary>Tiền tố <c>\\?\</c> cho API Win32 khi đường dẫn dài.</summary>
public static class LongPath
{
    public static string Prefix(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) return path;
        if (path.Length < 240) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path[2..];
        return @"\\?\" + path;
    }

    public static string Strip(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path[4..];
        return path;
    }
}
