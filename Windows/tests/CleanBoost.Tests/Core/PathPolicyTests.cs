using System.Runtime.InteropServices;
using System.Text;
using CleanBoost.Core;
using CleanBoost.Core.FileSystem;
using static CleanBoost.Core.PathPolicy;

namespace CleanBoost.Tests.Core;

/// <summary>Chuẩn hoá cú pháp đường dẫn (mục 15.1): không đụng tới ổ đĩa.</summary>
public class PathSyntaxTests
{
    [Theory]
    [InlineData(@"C:\Users\x\..\y", @"C:\Users\y")]
    [InlineData("C:/Users/x/./y/", @"C:\Users\x\y")]
    [InlineData(@"c:\a\b", @"C:\a\b")]
    [InlineData(@"C:\..\..\a", @"C:\a")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"\\?\C:\Windows\Temp", @"C:\Windows\Temp")]
    [InlineData(@"C:\a\\\b", @"C:\a\b")]
    public void NormalizesSyntax(string input, string expected) => Assert.Equal(expected, NormalizeSyntax(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"Windows\System32")]          // tương đối
    [InlineData(@"C:Windows")]                 // tương đối theo ổ
    [InlineData("C:")]                         // thư mục hiện hành của ổ C, không phải gốc ổ
    [InlineData(@"C:\a\file.txt:stream")]      // luồng dữ liệu phụ
    [InlineData(@"C:\a\file.txt::$DATA")]
    [InlineData(@"C:\Windows.\System32")]      // Win32 bỏ dấu chấm cuối → C:\Windows
    [InlineData(@"C:\Windows \System32")]      // Win32 bỏ khoảng trắng cuối
    [InlineData(@"C:\Users\*")]
    [InlineData(@"C:\Users\a?")]
    [InlineData("C:\\a\\\"b\"")]
    [InlineData(@"C:\a<b")]
    [InlineData(@"\\.\C:\Windows")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume3\Windows")]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\Windows")]
    public void RejectsUnsafeSyntax(string input) => Assert.Null(NormalizeSyntax(input));

    [Theory]
    [InlineData(@"\\server\share\x", true)]
    [InlineData(@"\\?\UNC\server\share\x", true)]
    [InlineData(@"\\?\C:\x", false)]
    [InlineData(@"\\.\C:", false)]
    [InlineData(@"C:\x", false)]
    public void NetworkPathDetection(string path, bool expected) => Assert.Equal(expected, IsNetworkPath(path));

    [Theory]
    [InlineData(@"C:\a\b", @"C:\a", true)]
    [InlineData(@"C:\a", @"C:\a", true)]
    [InlineData(@"c:\A\b", @"C:\a\", true)]
    [InlineData(@"C:\ab", @"C:\a", false)]         // tiền tố chuỗi nhưng không phải thư mục con
    [InlineData(@"C:\a B\x", @"C:\a", false)]
    [InlineData(@"C:\x", @"C:\", true)]
    [InlineData(@"D:\a", @"C:\a", false)]
    public void SameOrInside(string path, string root, bool expected) => Assert.Equal(expected, IsSameOrInside(path, root));

    [Fact]
    public void LongPathPrefixOnlyForLongPaths()
    {
        Assert.Equal(@"C:\short", LongPath.Prefix(@"C:\short"));
        var longPath = @"C:\" + new string('a', 300);
        Assert.Equal(@"\\?\" + longPath, LongPath.Prefix(longPath));
        var unc = @"\\server\share\" + new string('b', 300);
        Assert.Equal(@"\\?\UNC\server\share\" + new string('b', 300), LongPath.Prefix(unc));
        Assert.Equal(longPath, LongPath.Strip(@"\\?\" + longPath));
        Assert.Equal(@"\\server\share\x", LongPath.Strip(@"\\?\UNC\server\share\x"));
    }
}

/// <summary>
/// Quyết định của chính sách thật (<see cref="PathPolicy.CurrentUser"/>, <see cref="PathPolicy.Root"/>) cho các đường dẫn
/// quen thuộc. Chỉ hỏi chính sách, không xoá gì. Bỏ kiểm tra chủ sở hữu để kết quả không phụ thuộc máy.
/// </summary>
public class RealPolicyDecisionTests
{
    private static readonly string Win = KnownPaths.Windows;
    private static readonly string Home = KnownPaths.UserHome;
    private static readonly string UserTemp = Path.Combine(KnownPaths.UserHome, "AppData", "Local", "Temp");

    private static ViolationKind? User(string path, Intent intent = Intent.RemoveItem) =>
        PathPolicy.CurrentUser.Validate(path, intent, checkOwnership: false)?.Kind;

    private static ViolationKind? Root(string path, Intent intent = Intent.RemoveItem) => PathPolicy.Root.Validate(path, intent)?.Kind;

    [Fact]
    public void System32IsForbiddenForUserAndHelper()
    {
        foreach (var p in new[] { Win, Win + @"\System32", Win + @"\System32\drivers\etc\hosts", Win.ToLowerInvariant() + @"\SYSTEM32", @"\\?\" + Win + @"\System32" })
        {
            Assert.Equal(ViolationKind.ForbiddenZone, User(p));
            Assert.Equal(ViolationKind.ForbiddenZone, Root(p));
            Assert.Equal(ViolationKind.ForbiddenZone, Root(p, Intent.RemoveContents));
        }
    }

    [Fact]
    public void WindowsTempIsAShellTheHelperMayEmpty()
    {
        Assert.Null(Root(Win + @"\Temp\cb-test-nonexistent.tmp"));
        Assert.Null(Root(Win + @"\Temp\cb-test-dir\nested\x.log"));
        Assert.Null(Root(Win + @"\Temp", Intent.RemoveContents));
        Assert.Equal(ViolationKind.ProtectedDirectory, Root(Win + @"\Temp"));
        Assert.Null(Root(Win + @"\SoftwareDistribution\Download\abc"));
        // Ngoại lệ trong %WINDIR% không mở cho thư mục anh em.
        Assert.Equal(ViolationKind.ForbiddenZone, Root(Win + @"\SoftwareDistribution\DataStore\DataStore.edb"));
        Assert.Equal(ViolationKind.ForbiddenZone, Root(Win + @"\TempX\a"));
    }

    [Fact]
    public void TraversalOutOfAnAllowedZoneIsCaught()
    {
        var v = PathPolicy.Root.Validate(Win + @"\Temp\..\System32\config\SAM");
        Assert.NotNull(v);
        Assert.Equal(ViolationKind.ForbiddenZone, v.Kind);
        Assert.Equal(Win + @"\System32\config\SAM", v.Path, ignoreCase: true);

        var n = PathPolicy.CurrentUser.Validate(UserTemp + @"\..\..\..\NTUSER.DAT", checkOwnership: false);
        Assert.NotNull(n);
        Assert.Equal(ViolationKind.ForbiddenZone, n.Kind);
        Assert.Equal(Home + @"\NTUSER.DAT", n.Path, ignoreCase: true);
    }

    [Fact]
    public void UserProfileAndKnownFoldersAreProtected()
    {
        Assert.Equal(ViolationKind.ProtectedDirectory, User(Home));
        Assert.Equal(ViolationKind.ProtectedDirectory, User(Home, Intent.RemoveContents));
        foreach (var name in new[] { "Documents", "Desktop", "Downloads", "Pictures", "AppData", @"AppData\Local", @"AppData\Roaming" })
            Assert.Equal(ViolationKind.ProtectedDirectory, User(Path.Combine(Home, name), Intent.RemoveContents));
        foreach (var known in KnownPaths.UserKnownFolders)
            Assert.NotNull(User(known, Intent.RemoveContents));
    }

    [Fact]
    public void UserTempOnlyAllowsDeletingContents()
    {
        Assert.Equal(ViolationKind.ProtectedDirectory, User(UserTemp));
        Assert.Null(User(UserTemp, Intent.RemoveContents));
        Assert.Null(User(UserTemp + @"\cb-test-nonexistent\x.tmp"));
    }

    [Fact]
    public void RegistryHivesCredentialsAndMailStoresAreForbidden()
    {
        Assert.Equal(ViolationKind.ForbiddenZone, User(Home + @"\NTUSER.DAT"));
        Assert.Equal(ViolationKind.ForbiddenZone, User(Home + @"\ntuser.dat.LOG1"));
        Assert.Equal(ViolationKind.ForbiddenZone, User(Home + @"\AppData\Local\Microsoft\Windows\UsrClass.dat"));
        Assert.Equal(ViolationKind.ForbiddenZone, User(Home + @"\AppData\Roaming\Microsoft\Credentials\ABC"));
        Assert.Equal(ViolationKind.ForbiddenZone, User(Home + @"\AppData\Roaming\Microsoft\Protect\S-1-5-21\key"));
        Assert.Equal(ViolationKind.ForbiddenZone, User(UserTemp + @"\cb-test\Outlook\archive.pst"));
        Assert.Equal(ViolationKind.ForbiddenZone, User(UserTemp + @"\cb-test\cache.OST"));
        Assert.Equal(ViolationKind.ForbiddenZone, Root(KnownPaths.ProgramData + @"\Vendor\backup.pst"));
    }

    [Fact]
    public void SystemZonesAreForbidden()
    {
        var sys = KnownPaths.SystemDrive;
        string[] paths =
        [
            sys + @"\$Recycle.Bin\S-1-5-21-1\$R123.txt", sys + @"\System Volume Information", sys + @"\pagefile.sys",
            sys + @"\hiberfil.sys", sys + @"\Recovery\x", sys + @"\Windows.old\x",
            KnownPaths.ProgramFiles + @"\WindowsApps\Pkg", KnownPaths.ProgramData + @"\Microsoft\Crypto\RSA\x",
            KnownPaths.ProgramData + @"\Microsoft\Windows Defender\Scans", KnownPaths.UsersRoot + @"\Default\NTUSER.DAT",
        ];
        foreach (var p in paths)
        {
            Assert.Equal(ViolationKind.ForbiddenZone, User(p));
            Assert.Equal(ViolationKind.ForbiddenZone, Root(p));
        }
    }

    [Fact]
    public void TopLevelDirectoriesAreProtected()
    {
        foreach (var p in new[] { KnownPaths.SystemDrive + @"\", KnownPaths.ProgramFiles, KnownPaths.ProgramData, KnownPaths.UsersRoot, KnownPaths.UsersRoot + @"\Public" })
        {
            Assert.Equal(ViolationKind.ProtectedDirectory, User(p, Intent.RemoveContents));
            Assert.Equal(ViolationKind.ProtectedDirectory, Root(p, Intent.RemoveContents));
        }
    }

    [Fact]
    public void HelperAllowlistIsNarrow()
    {
        Assert.Equal(ViolationKind.NotInRootAllowlist, Root(Home + @"\Documents\cb-test.txt"));
        Assert.Equal(ViolationKind.NotInRootAllowlist, Root(KnownPaths.SystemDrive + @"\cb-test-dir\x"));
        Assert.Null(Root(UserTemp + @"\cb-test.tmp"));
        Assert.Null(Root(KnownPaths.ProgramFiles + @"\Vendor\cache\x"));
        Assert.Null(Root(KnownPaths.ProgramData + @"\Vendor\Logs\x.log"));
        // Vùng cấm theo hồ sơ áp cho mọi người dùng phía helper.
        Assert.Equal(ViolationKind.ForbiddenZone, Root(KnownPaths.UsersRoot + @"\someone\NTUSER.DAT"));
        Assert.Equal(ViolationKind.ForbiddenZone, Root(KnownPaths.UsersRoot + @"\someone\AppData\Roaming\Microsoft\Credentials\x"));
        Assert.Equal(ViolationKind.ProtectedDirectory, Root(KnownPaths.UsersRoot + @"\someone\AppData\Local\Temp"));
        Assert.Null(Root(KnownPaths.UsersRoot + @"\someone\AppData\Local\Temp\x"));
    }

    [Theory]
    [InlineData(@"\\server\share\x", ViolationKind.NetworkPath)]
    [InlineData(@"\\?\UNC\server\share\x", ViolationKind.NetworkPath)]
    [InlineData(@"relative\path", ViolationKind.NotAbsolute)]
    [InlineData(@"C:relative", ViolationKind.NotAbsolute)]
    [InlineData("C:", ViolationKind.NotAbsolute)]
    [InlineData(@"C:\cb-test\file.txt:hidden", ViolationKind.NotAbsolute)]
    [InlineData(@"C:\Windows.\System32", ViolationKind.NotAbsolute)]
    [InlineData(@"C:\Windows \System32", ViolationKind.NotAbsolute)]
    [InlineData(@"C:\Users\*", ViolationKind.NotAbsolute)]
    public void MalformedPathsAreRejectedNotThrown(string path, ViolationKind expected)
    {
        Assert.Equal(expected, User(path));
        Assert.Equal(expected, Root(path));
        Assert.Equal(expected, PathPolicy.CurrentUser.IsForbidden(path)?.Kind);
    }

    [Fact]
    public void ForwardSlashUncIsStillRejected() => Assert.NotNull(User("//server/share/x"));

    [Fact]
    public void IsForbiddenIgnoresShellsButNotProtectedOrForbidden()
    {
        Assert.Null(PathPolicy.CurrentUser.IsForbidden(UserTemp));
        Assert.Equal(ViolationKind.ProtectedDirectory, PathPolicy.CurrentUser.IsForbidden(Home)?.Kind);
        Assert.Equal(ViolationKind.ForbiddenZone, PathPolicy.CurrentUser.IsForbidden(Win + @"\System32")?.Kind);
    }

    [Fact]
    public void ShouldSkipDescending()
    {
        var p = PathPolicy.CurrentUser;
        Assert.False(p.ShouldSkipDescending(Win));                                  // còn ngoại lệ bên trong
        Assert.False(p.ShouldSkipDescending(Win + @"\Temp"));
        Assert.False(p.ShouldSkipDescending(Win + @"\SoftwareDistribution"));      // tổ tiên của ...\Download
        Assert.True(p.ShouldSkipDescending(Win + @"\System32"));
        Assert.True(p.ShouldSkipDescending(KnownPaths.SystemDrive + @"\System Volume Information"));
        Assert.True(p.ShouldSkipDescending(KnownPaths.ProgramData + @"\Microsoft\Crypto"));
        Assert.True(p.ShouldSkipDescending(Home + @"\OneDrive - Contoso"));
        Assert.True(p.ShouldSkipDescending(UserTemp + @"\weird.pst"));
        Assert.False(p.ShouldSkipDescending(KnownPaths.UsersRoot));
        Assert.False(p.ShouldSkipDescending(UserTemp));
    }
}

/// <summary>Chính sách với hồ sơ giả lập trong thư mục tạm: thư mục bảo vệ, vỏ, vùng rule, liên kết, chủ sở hữu.</summary>
public sealed class FixturePolicyTests : IDisposable
{
    private readonly TempDir _tmp = new("policy");
    private readonly string _home;
    private readonly PathPolicy _policy;

    public FixturePolicyTests()
    {
        _home = _tmp.Dir(@"Users\tester");
        _policy = TestPolicy.For(_home, _tmp.P(@"Users\tester\Projects"));
    }

    public void Dispose() => _tmp.Dispose();

    private string H(string relative) => Path.Combine(_home, relative);

    private ViolationKind? Kind(string path, Intent intent = Intent.RemoveItem, string[]? roots = null) =>
        _policy.Validate(path, intent, roots)?.Kind;

    [Fact]
    public void HomeAndKnownFoldersAreProtected()
    {
        Assert.Equal(ViolationKind.ProtectedDirectory, Kind(_home, Intent.RemoveContents));
        Assert.Equal(ViolationKind.ProtectedDirectory, Kind(H("Documents"), Intent.RemoveContents));
        Assert.Equal(ViolationKind.ProtectedDirectory, Kind(H(@"AppData\Local"), Intent.RemoveContents));
        Assert.Equal(ViolationKind.ProtectedDirectory, Kind(H(@"AppData\Local\Programs")));
        Assert.Equal(ViolationKind.ProtectedDirectory, Kind(H(@"AppData\Local\Packages\Vendor.App_abc")));
        Assert.Null(Kind(H(@"AppData\Local\Packages\Vendor.App_abc\LocalCache")));
        Assert.Equal(ViolationKind.ProtectedDirectory, Kind(H("Projects"), Intent.RemoveContents)); // extraProtected
        Assert.Null(Kind(H(@"Projects\build\obj")));
    }

    [Fact]
    public void ShellsAllowContentsOnly()
    {
        foreach (var shell in new[] { @"AppData\Local\Temp", @"AppData\Local\CrashDumps", @".nuget", @"AppData\Local\Microsoft\Windows\INetCache" })
        {
            Assert.Equal(ViolationKind.ProtectedDirectory, Kind(H(shell)));
            Assert.Null(Kind(H(shell), Intent.RemoveContents));
            Assert.Null(Kind(H(shell + @"\child")));
        }
    }

    [Fact]
    public void PerHomeForbiddenZones()
    {
        foreach (var p in new[]
                 {
                     "NTUSER.DAT", @"AppData\Roaming\Microsoft\Credentials\x", @"OneDrive - Contoso\report.docx", @"Dropbox\x",
                     @"AppData\Local\Google\Chrome\User Data\Default\Login Data", @"AppData\Local\Microsoft\Vault\x",
                 })
        {
            Assert.Equal(ViolationKind.ForbiddenZone, Kind(H(p)));
        }
    }

    [Fact]
    public void AllowedRootsAreEnforcedAfterNormalization()
    {
        var root = H(@"AppData\Local\Vendor\Cache");
        string[] roots = [root];
        Assert.Null(Kind(root + @"\blob", roots: roots));
        Assert.Null(Kind(root, Intent.RemoveContents, roots));
        Assert.Equal(ViolationKind.OutsideAllowedRoots, Kind(H(@"AppData\Local\Vendor\CacheX\blob"), roots: roots));
        Assert.Equal(ViolationKind.OutsideAllowedRoots, Kind(root + @"\..\Settings\user.json", roots: roots));
    }

    [Fact]
    public void CheckReturnsCanonicalPath()
    {
        var dir = _tmp.Dir(@"Users\tester\AppData\Local\Vendor");
        var canonical = _policy.Check(dir + @"\.\sub\..\file.txt");
        Assert.Equal(dir + @"\file.txt", canonical, ignoreCase: true);
    }

    [SkippableFact]
    public void JunctionInParentIsResolvedBeforeChecks()
    {
        var outside = _tmp.Dir("outside");
        _tmp.File(@"outside\secret.txt");
        var cache = _tmp.Dir(@"Users\tester\AppData\Local\Vendor\Cache");
        var link = Path.Combine(cache, "link");
        Skip.IfNot(Links.TryCreateJunction(link, outside), Links.JunctionSkipReason);

        string[] roots = [cache];
        var escaped = _policy.Validate(link + @"\secret.txt", allowedRoots: roots);
        Assert.NotNull(escaped);
        Assert.Equal(ViolationKind.OutsideAllowedRoots, escaped.Kind);
        Assert.Equal(Path.Combine(outside, "secret.txt"), escaped.Path, ignoreCase: true);
        // Chính liên kết (thành phần cuối) không được giải: xoá nó là xoá liên kết.
        Assert.Null(_policy.Validate(link, allowedRoots: roots));
    }

    [SkippableFact]
    public void JunctionIntoWindowsIsForbidden()
    {
        var cache = _tmp.Dir(@"Users\tester\AppData\Local\Vendor\Cache");
        var link = Path.Combine(cache, "winlink");
        Skip.IfNot(Links.TryCreateJunction(link, KnownPaths.Windows), Links.JunctionSkipReason);

        var v = _policy.Validate(link + @"\System32");
        Assert.NotNull(v);
        Assert.Equal(ViolationKind.ForbiddenZone, v.Kind);
        Assert.Equal(KnownPaths.Windows + @"\System32", v.Path, ignoreCase: true);
        Assert.Equal(ViolationKind.ForbiddenZone, PathPolicy.Root.Validate(link + @"\System32")?.Kind);
        Assert.Null(_policy.Validate(link));
    }

    [Fact]
    public void OwnershipIsCheckedInUserMode()
    {
        var file = _tmp.File(@"Users\tester\AppData\Local\Vendor\Cache\blob");
        Assert.Null(_policy.Validate(file));

        var foreign = new PathPolicy(PolicyMode.User, TestPolicy.ForeignSid, false, _home, []);
        var v = foreign.Validate(file);
        Assert.NotNull(v);
        Assert.Equal(ViolationKind.NotOwnedByUser, v.Kind);
        Assert.True(v.IsOwnershipOnly);
        Assert.Null(foreign.Validate(file, checkOwnership: false));
        // Chưa tồn tại: không có chủ sở hữu để so.
        Assert.Null(foreign.Validate(file + ".missing"));
    }

    [Fact]
    public void ViolationCarriesDescription()
    {
        var v = _policy.Validate(_home);
        Assert.NotNull(v);
        Assert.Contains(_home, v.LocalizedDescription, StringComparison.OrdinalIgnoreCase);
        Assert.False(v.IsOwnershipOnly);
    }
}

/// <summary>
/// Tên 8.3 (<c>DOCUME~1</c>) của thành phần cuối trỏ vào cùng thư mục với tên dài: chính sách phải nhận ra để không lách được
/// thư mục bảo vệ. Bỏ qua nếu ổ không tạo tên ngắn.
/// </summary>
public sealed class ShortNamePolicyTests : IDisposable
{
    private readonly TempDir _tmp = new("short");

    public void Dispose() => _tmp.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint size);

    internal static string? ShortName(string path)
    {
        var sb = new StringBuilder(1024);
        var n = GetShortPathNameW(path, sb, (uint)sb.Capacity);
        if (n == 0 || n >= sb.Capacity) return null;
        var shortLeaf = Path.GetFileName(sb.ToString().TrimEnd('\\'));
        return shortLeaf.Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase) ? null : shortLeaf;
    }

    [SkippableFact]
    public void ShortNameOfProtectedFolderIsStillProtected()
    {
        var home = _tmp.Dir(@"Users\tester");
        var docs = _tmp.Dir(@"Users\tester\Documents");
        _tmp.File(@"Users\tester\Documents\thesis.docx", 4096);
        var policy = TestPolicy.For(home);
        var shortLeaf = ShortName(docs);
        Skip.If(shortLeaf is null, "Ổ chứa %TEMP% không tạo tên 8.3.");

        var alias = Path.Combine(home, shortLeaf!);
        var v = policy.Validate(alias, Intent.RemoveContents);
        Assert.NotNull(v);
        Assert.Equal(ViolationKind.ProtectedDirectory, v.Kind);

        // Và remover không xoá gì qua tên ngắn.
        var r = SecureRemover.Remove(alias, policy, contentsOnly: true);
        Assert.Equal(RemoveOutcomeKind.Skipped, r.Outcome);
        Assert.Equal(SkipReason.BlockedByPolicy, r.Reason);
        Assert.True(_tmp.Exists(@"Users\tester\Documents\thesis.docx"));
    }

    [SkippableFact]
    public void ShortNameOfForbiddenFolderIsStillForbidden()
    {
        var home = _tmp.Dir(@"Users\tester");
        var credentials = _tmp.Dir(@"Users\tester\AppData\Roaming\Microsoft\Credentials");
        _tmp.File(@"Users\tester\AppData\Roaming\Microsoft\Credentials\blob");
        var policy = TestPolicy.For(home);
        var shortLeaf = ShortName(credentials);
        Skip.If(shortLeaf is null, "Ổ chứa %TEMP% không tạo tên 8.3.");
        var alias = Path.Combine(Path.GetDirectoryName(credentials)!, shortLeaf!);
        Assert.Equal(ViolationKind.ForbiddenZone, policy.Validate(alias)?.Kind);
        Assert.Equal(ViolationKind.ForbiddenZone, policy.IsForbidden(alias)?.Kind);
    }

    [SkippableFact]
    public void RealProfileShortNamesAreProtected()
    {
        var docs = Path.Combine(KnownPaths.UserHome, "Documents");
        var shortDocs = Directory.Exists(docs) ? ShortName(docs) : null;
        var shortPf = ShortName(KnownPaths.ProgramFiles);
        Skip.If(shortDocs is null && shortPf is null, "Không có tên 8.3 cho Documents / Program Files trên máy này.");
        if (shortDocs is not null)
            Assert.Equal(ViolationKind.ProtectedDirectory,
                PathPolicy.CurrentUser.Validate(Path.Combine(KnownPaths.UserHome, shortDocs), Intent.RemoveContents, checkOwnership: false)?.Kind);
        if (shortPf is not null)
            Assert.Equal(ViolationKind.ProtectedDirectory,
                PathPolicy.CurrentUser.Validate(Path.Combine(Path.GetDirectoryName(KnownPaths.ProgramFiles)!, shortPf), Intent.RemoveContents, checkOwnership: false)?.Kind);
    }

    [Fact]
    public void TildeInOrdinaryNamesIsHarmless()
    {
        var home = _tmp.Dir(@"Users\tester");
        var dir = _tmp.Dir(@"Users\tester\AppData\Local\Vendor\cache~old");
        var policy = TestPolicy.For(home);
        Assert.Equal(dir, policy.Check(dir), ignoreCase: true);
        Assert.Equal(Path.Combine(home, "missing~1"), policy.Check(Path.Combine(home, "missing~1")), ignoreCase: true);
    }
}
