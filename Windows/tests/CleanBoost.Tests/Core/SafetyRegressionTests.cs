using CleanBoost.Core;
using CleanBoost.Engine.Cleaning;
using CleanBoost.Engine.FileSystem;
using CleanBoost.Engine.Nodes;
using Xunit;
using static CleanBoost.Core.PathPolicy;

namespace CleanBoost.Tests.Core;

/// <summary>Test hồi quy cho các lỗi tìm thấy khi rà soát an toàn (H1, H2, M3, M5, L3).</summary>
public sealed class SafetyRegressionTests : IDisposable
{
    private readonly TempDir _tmp = new("safety");

    public void Dispose() => _tmp.Dispose();

    private static ViolationKind? Kind(PathPolicy policy, string path, Intent intent = Intent.RemoveItem, bool ownership = false) =>
        policy.Validate(path, intent, checkOwnership: ownership)?.Kind;

    // M5: đường dẫn thật chứa ký tự glob không được biến thành mẫu.

    [Theory]
    [InlineData(@"D:\[Work] Docs")]
    [InlineData(@"D:\a*b\c?d")]
    [InlineData(@"D:\plain\path")]
    public void EscapedLiteralMatchesOnlyItself(string path)
    {
        var glob = new Glob(Glob.EscapeLiteral(path));
        Assert.True(glob.Matches(path));
        Assert.False(glob.Matches(path + "x"));
    }

    [Fact]
    public void BracketedHomeStaysProtected()
    {
        var home = _tmp.Dir(@"Users\[x] tester");
        _tmp.Dir(@"Users\[x] tester\Documents");
        var policy = TestPolicy.For(home);
        Assert.Equal(ViolationKind.ProtectedDirectory, Kind(policy, home, Intent.RemoveContents));
        Assert.Equal(ViolationKind.ProtectedDirectory, Kind(policy, Path.Combine(home, "Documents")));
    }

    [Fact]
    public void BracketedRelocatedFolderStaysProtected()
    {
        var home = _tmp.Dir(@"Users\tester");
        var docs = _tmp.Dir(@"Data\[Work] Docs");
        var policy = TestPolicy.For(home, docs);
        Assert.Equal(ViolationKind.ProtectedDirectory, Kind(policy, docs));
    }

    // L3: không xoá thư mục chứa thư mục được bảo vệ.

    [Fact]
    public void FolderContainingProtectedFolderIsBlocked()
    {
        var home = _tmp.Dir(@"Users\tester");
        var docs = _tmp.Dir(@"Data\Docs");
        _tmp.File(@"Data\Docs\cv.docx");
        var policy = TestPolicy.For(home, docs);
        Assert.Equal(ViolationKind.ProtectedDirectory, Kind(policy, _tmp.P("Data")));
        Assert.Equal(ViolationKind.ProtectedDirectory, Kind(policy, _tmp.P("Data"), Intent.RemoveContents));
        // Thư mục anh em vẫn xoá được.
        _tmp.Dir(@"Data\Other");
        Assert.Null(Kind(policy, _tmp.P(@"Data\Other")));
    }

    // H1: biến TEMP cấu hình sai không được làm rule tệp tạm nhắm vào dữ liệu thật.

    [Fact]
    public void MisconfiguredTempFallsBackToLocalAppDataTemp()
    {
        var fallback = Path.Combine(KnownPaths.LocalAppData, "Temp");
        Assert.Equal(fallback, KnownPaths.ValidatedTemp("C:"));
        Assert.Equal(fallback, KnownPaths.ValidatedTemp(@"D:\"));
        Assert.Equal(fallback, KnownPaths.ValidatedTemp(KnownPaths.UserHome));
        Assert.Equal(fallback, KnownPaths.ValidatedTemp(KnownPaths.UsersRoot));
        Assert.Equal(fallback, KnownPaths.ValidatedTemp(@"D:\Data"));
        Assert.Equal(fallback, KnownPaths.ValidatedTemp(KnownPaths.ProgramData));
        Assert.Equal(@"R:\Temp", KnownPaths.ValidatedTemp(@"R:\Temp"));
        Assert.Equal(KnownPaths.Temp, KnownPaths.ValidatedTemp(KnownPaths.Temp));
    }

    // H2: helper quản trị không đi qua junction ở thư mục cha.

    [SkippableFact]
    public void RootPolicyRejectsLinkedParent()
    {
        var real = _tmp.Dir("real");
        _tmp.File(@"real\victim.dat");
        var link = _tmp.P("link");
        Skip.IfNot(Links.TryCreateJunction(link, real), Links.JunctionSkipReason);
        Assert.Equal(link, LinkAncestor(Path.Combine(link, "victim.dat")), ignoreCase: true);
        Assert.Equal(ViolationKind.UntrustedParent, Kind(Root, Path.Combine(link, "victim.dat")));
        // Đường dẫn thật (không qua liên kết) vẫn được.
        Assert.Null(LinkAncestor(Path.Combine(real, "victim.dat")));
        Assert.Null(Kind(Root, Path.Combine(real, "victim.dat")));
    }

    // M3: xoá thư mục không được xoá mục thuộc vùng cấm nằm bên trong.

    [Fact]
    public void SecureRemoverKeepsForbiddenFilesInside()
    {
        var home = _tmp.Dir(@"Users\tester");
        var policy = TestPolicy.For(home);
        var cache = _tmp.Dir(@"Users\tester\AppData\Local\Vendor\Cache");
        _tmp.File(@"Users\tester\AppData\Local\Vendor\Cache\a.bin", 4096);
        _tmp.File(@"Users\tester\AppData\Local\Vendor\Cache\sub\archive.pst", 4096);

        var result = SecureRemover.Remove(cache, policy, contentsOnly: true, checkOwnership: false);

        Assert.False(_tmp.Exists(@"Users\tester\AppData\Local\Vendor\Cache\a.bin"));
        Assert.True(_tmp.Exists(@"Users\tester\AppData\Local\Vendor\Cache\sub\archive.pst"));
        Assert.Equal(RemoveOutcomeKind.Failed, result.Outcome);
        Assert.True(result.FreedBytes > 0);
    }

    [Fact]
    public void TrashingFolderWithBrowserPasswordsIsBlocked()
    {
        var home = _tmp.Dir(@"Users\tester");
        var policy = TestPolicy.For(home);
        var chrome = _tmp.Dir(@"Users\tester\AppData\Local\Google\Chrome");
        _tmp.File(@"Users\tester\AppData\Local\Google\Chrome\User Data\Default\Login Data");
        var engine = new CleanEngine(new FileSystemService(home), helper: null, storage: null, policy);
        var node = Node.ForPath(chrome, isDirectory: true);
        node.Removal = RemovalStrategy.MoveToTrash;
        node.Safety = SafetyLevel.Review;

        var plan = engine.MakePlan([node]);

        Assert.Empty(plan.Items);
        Assert.Equal(ViolationKind.ForbiddenZone, Assert.Single(plan.BlockedItems).Violation.Kind);
    }
}
