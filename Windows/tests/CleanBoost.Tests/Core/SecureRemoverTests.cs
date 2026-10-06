using CleanBoost.Core;
using CleanBoost.Core.FileSystem;

namespace CleanBoost.Tests.Core;

/// <summary>Xoá an toàn (mục 9.4): cây lồng nhau, chỉ đọc, giữ thư mục khi chỉ xoá nội dung, không bao giờ đi theo liên kết.</summary>
public sealed class SecureRemoverTests : IDisposable
{
    private readonly TempDir _tmp = new("remover");
    private readonly string _home;
    private readonly PathPolicy _policy;
    private const string Work = @"Users\tester\AppData\Local\Vendor";

    public SecureRemoverTests()
    {
        _home = _tmp.Dir(@"Users\tester");
        _tmp.Dir(Work);
        _policy = TestPolicy.For(_home);
    }

    public void Dispose() => _tmp.Dispose();

    private string W(string relative = "") => _tmp.P(Work + (relative.Length == 0 ? "" : "\\" + relative));

    private long Cluster => SecureRemover.ClusterSize(W());

    [Fact]
    public void DeletesNestedTreeAndReportsAllocatedBytes()
    {
        _tmp.File(Work + @"\tree\a.bin", 10_000);
        _tmp.File(Work + @"\tree\sub\b.bin", 5_000);
        _tmp.File(Work + @"\tree\sub\deeper\c.bin", 1);
        _tmp.Dir(Work + @"\tree\empty");

        var r = SecureRemover.Remove(W("tree"), _policy);

        Assert.Equal(RemoveOutcomeKind.Ok, r.Outcome);
        Assert.True(r.IsSuccess);
        var c = Cluster;
        Assert.Equal(DiskSize.RoundUp(10_000, c) + DiskSize.RoundUp(5_000, c) + DiskSize.RoundUp(1, c), r.FreedBytes);
        Assert.False(Directory.Exists(W("tree")));
        Assert.True(Directory.Exists(W()));
    }

    [Fact]
    public void ContentsOnlyKeepsTheDirectory()
    {
        _tmp.File(Work + @"\cache\a.bin", 2_000);
        _tmp.File(Work + @"\cache\sub\b.bin", 2_000);

        var r = SecureRemover.Remove(W("cache"), _policy, contentsOnly: true);

        Assert.Equal(RemoveOutcomeKind.Ok, r.Outcome);
        Assert.True(Directory.Exists(W("cache")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(W("cache")));
        Assert.Equal(2 * DiskSize.RoundUp(2_000, Cluster), r.FreedBytes);
    }

    [Fact]
    public void ContentsOnlyOnAFileDeletesTheFile()
    {
        var f = _tmp.File(Work + @"\loose.db", 100);
        var r = SecureRemover.Remove(f, _policy, contentsOnly: true);
        Assert.Equal(RemoveOutcomeKind.Ok, r.Outcome);
        Assert.False(File.Exists(f));
    }

    [Fact]
    public void ReadOnlyFilesAndDirectoriesAreRemoved()
    {
        var f = _tmp.File(Work + @"\ro\locked.txt", 64);
        File.SetAttributes(f, FileAttributes.ReadOnly);
        var inner = _tmp.Dir(Work + @"\ro\inner");
        _tmp.File(Work + @"\ro\inner\x.txt", 64);
        new DirectoryInfo(inner).Attributes |= FileAttributes.ReadOnly;
        new DirectoryInfo(W("ro")).Attributes |= FileAttributes.ReadOnly;

        var r = SecureRemover.Remove(W("ro"), _policy);

        Assert.Equal(RemoveOutcomeKind.Ok, r.Outcome);
        Assert.False(Directory.Exists(W("ro")));
    }

    [Fact]
    public void MissingPathIsNotFound()
    {
        var r = SecureRemover.Remove(W("missing.tmp"), _policy);
        Assert.Equal(RemoveOutcomeKind.Skipped, r.Outcome);
        Assert.Equal(SkipReason.NotFound, r.Reason);
        Assert.True(r.IsSuccess);
        Assert.Equal(0, r.FreedBytes);

        var deep = SecureRemover.Remove(W(@"nope\deeper\x.tmp"), _policy);
        Assert.Equal(SkipReason.NotFound, deep.Reason);
    }

    [Fact]
    public void PolicyViolationsAreBlockedAndUntouched()
    {
        var hive = _tmp.File(@"Users\tester\NTUSER.DAT", 128);
        var doc = _tmp.File(@"Users\tester\Documents\thesis.docx", 128);
        var tempFile = _tmp.File(@"Users\tester\AppData\Local\Temp\junk.tmp", 128);

        Assert.Equal(SkipReason.BlockedByPolicy, SecureRemover.Remove(_home, _policy, contentsOnly: true).Reason);
        Assert.Equal(SkipReason.BlockedByPolicy, SecureRemover.Remove(Path.GetDirectoryName(doc)!, _policy, contentsOnly: true).Reason);
        Assert.Equal(SkipReason.BlockedByPolicy, SecureRemover.Remove(hive, _policy).Reason);
        // Thư mục vỏ: không xoá chính nó, được xoá nội dung.
        var temp = Path.GetDirectoryName(tempFile)!;
        Assert.Equal(SkipReason.BlockedByPolicy, SecureRemover.Remove(temp, _policy).Reason);
        Assert.True(File.Exists(tempFile));
        Assert.Equal(RemoveOutcomeKind.Ok, SecureRemover.Remove(temp, _policy, contentsOnly: true).Outcome);

        Assert.True(File.Exists(hive));
        Assert.True(File.Exists(doc));
        Assert.True(Directory.Exists(temp));
        Assert.False(File.Exists(tempFile));
    }

    [Fact]
    public void OutsideAllowedRootsIsBlocked()
    {
        var f = _tmp.File(Work + @"\settings\user.json");
        var r = SecureRemover.Remove(W(@"cache\..\settings\user.json"), _policy, allowedRoots: [W("cache")]);
        Assert.Equal(SkipReason.BlockedByPolicy, r.Reason);
        Assert.True(File.Exists(f));
    }

    [Fact]
    public void NotOwnedIsBlockedUnlessOwnershipCheckDisabled()
    {
        var f = _tmp.File(Work + @"\other\x.bin");
        var foreign = new PathPolicy(PathPolicy.PolicyMode.User, TestPolicy.ForeignSid, false, _home, []);
        Assert.Equal(SkipReason.BlockedByPolicy, SecureRemover.Remove(f, foreign).Reason);
        Assert.True(File.Exists(f));
        Assert.Equal(RemoveOutcomeKind.Ok, SecureRemover.Remove(f, foreign, checkOwnership: false).Outcome);
        Assert.False(File.Exists(f));
    }

    [SkippableFact]
    public void JunctionInsideTreeIsUnlinkedNotFollowed()
    {
        var victim = _tmp.Dir("victim");
        var precious = _tmp.File(@"victim\precious.txt", 4096);
        _tmp.File(Work + @"\tree\a.bin", 100);
        Skip.IfNot(Links.TryCreateJunction(W(@"tree\link"), victim), Links.JunctionSkipReason);

        var r = SecureRemover.Remove(W("tree"), _policy);

        Assert.Equal(RemoveOutcomeKind.Ok, r.Outcome);
        Assert.False(Directory.Exists(W("tree")));
        Assert.True(File.Exists(precious));
        Assert.Equal(DiskSize.RoundUp(100, Cluster), r.FreedBytes);
    }

    [SkippableFact]
    public void JunctionTargetedWithContentsOnlyRemovesOnlyTheLink()
    {
        var victim = _tmp.Dir("victim");
        var precious = _tmp.File(@"victim\precious.txt", 4096);
        var link = W("link");
        Skip.IfNot(Links.TryCreateJunction(link, victim), Links.JunctionSkipReason);

        var r = SecureRemover.Remove(link, _policy, contentsOnly: true);

        Assert.Equal(RemoveOutcomeKind.Ok, r.Outcome);
        Assert.False(DirectoryReader.Exists(link));
        Assert.True(File.Exists(precious));
        Assert.Equal(0, r.FreedBytes);
    }

    [SkippableFact]
    public void JunctionIntoWindowsDeletesOnlyTheJunction()
    {
        var link = W(@"tree\winlink");
        _tmp.Dir(Work + @"\tree");
        Skip.IfNot(Links.TryCreateJunction(link, KnownPaths.Windows), Links.JunctionSkipReason);

        // Đi qua junction để nhắm vào System32: bị chặn.
        var through = SecureRemover.Remove(link + @"\System32", _policy, contentsOnly: true);
        Assert.Equal(SkipReason.BlockedByPolicy, through.Reason);

        var r = SecureRemover.Remove(W("tree"), _policy);
        Assert.Equal(RemoveOutcomeKind.Ok, r.Outcome);
        Assert.False(DirectoryReader.Exists(link));
        Assert.True(Directory.Exists(KnownPaths.Windows + @"\System32"));
    }

    [SkippableFact]
    public void FileSymlinkIsUnlinkedNotFollowed()
    {
        var target = _tmp.File(@"victim\target.txt", 4096);
        _tmp.Dir(Work + @"\tree");
        Skip.IfNot(Links.TryCreateFileSymlink(W(@"tree\file-link.txt"), target), Links.SymlinkSkipReason);

        var r = SecureRemover.Remove(W("tree"), _policy);

        Assert.Equal(RemoveOutcomeKind.Ok, r.Outcome);
        Assert.False(Directory.Exists(W("tree")));
        Assert.True(File.Exists(target));
        Assert.Equal(4096, new FileInfo(target).Length);
    }

    [SkippableFact]
    public void DirectorySymlinkTargetedDirectlyRemovesOnlyTheLink()
    {
        var victim = _tmp.Dir("victim");
        var precious = _tmp.File(@"victim\precious.txt", 4096);
        var link = W("dirlink");
        Skip.IfNot(Links.TryCreateDirectorySymlink(link, victim), Links.SymlinkSkipReason);

        var r = SecureRemover.Remove(link, _policy);

        Assert.Equal(RemoveOutcomeKind.Ok, r.Outcome);
        Assert.False(DirectoryReader.Exists(link));
        Assert.True(File.Exists(precious));
    }

    [Fact]
    public void LockedFileIsReportedInUseAndOthersAreRemoved()
    {
        var locked = _tmp.File(Work + @"\tree\a.bin", 100);
        var other = _tmp.File(Work + @"\tree\b.bin", 100);

        RemoveResult r;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            r = SecureRemover.Remove(W("tree"), _policy);
        }

        Assert.Equal(RemoveOutcomeKind.Skipped, r.Outcome);
        Assert.Equal(SkipReason.InUse, r.Reason);
        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(other));
        Assert.Equal(DiskSize.RoundUp(100, Cluster), r.FreedBytes);
    }

    [Fact]
    public void CancellationStopsBeforeDeleting()
    {
        for (var i = 0; i < 5; i++) _tmp.File(Work + $@"\tree\f{i}.bin", 10);
        SecureRemover.Remove(W("tree"), _policy, shouldContinue: () => false);
        Assert.Equal(5, Directory.GetFiles(W("tree")).Length);
    }

    [Fact]
    public void ProgressIsReportedIncrementally()
    {
        for (var i = 0; i < 450; i++) _tmp.File(Work + $@"\many\f{i:D3}.bin", 10);
        var reports = new List<long>();
        var r = SecureRemover.Remove(W("many"), _policy, progress: reports.Add);
        Assert.Equal(RemoveOutcomeKind.Ok, r.Outcome);
        Assert.True(reports.Count >= 2);
        Assert.Equal(reports.OrderBy(x => x), reports);
        Assert.True(reports[^1] <= r.FreedBytes);
    }

    [Theory]
    [InlineData(2, RemoveOutcomeKind.Skipped, SkipReason.NotFound)]
    [InlineData(3, RemoveOutcomeKind.Skipped, SkipReason.NotFound)]
    [InlineData(32, RemoveOutcomeKind.Skipped, SkipReason.InUse)]
    [InlineData(33, RemoveOutcomeKind.Skipped, SkipReason.InUse)]
    [InlineData(5, RemoveOutcomeKind.Failed, null)]
    public void Win32ErrorMapping(int code, RemoveOutcomeKind outcome, SkipReason? reason)
    {
        var r = SecureRemover.ResultFor(code, @"C:\x");
        Assert.Equal(outcome, r.Outcome);
        Assert.Equal(reason, r.Reason);
        if (outcome == RemoveOutcomeKind.Failed) Assert.Equal(code, r.ErrorCode);
    }
}
