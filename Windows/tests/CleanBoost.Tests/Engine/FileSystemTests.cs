using CleanBoost.Core;
using CleanBoost.Core.FileSystem;
using CleanBoost.Engine.FileSystem;

namespace CleanBoost.Tests.Engine;

/// <summary>Đọc thư mục và đo dung lượng (mục 16): không theo liên kết, làm tròn theo cluster, huỷ được.</summary>
public sealed class FileSystemTests : IDisposable
{
    private readonly TempDir _tmp = new("fs");
    private readonly FileSystemService _fs = new(Path.GetTempPath());

    public void Dispose() => _tmp.Dispose();

    private long Cluster => SecureRemover.ClusterSize(_tmp.Root);

    [Fact]
    public void DirectoryReaderListsEntriesWithMetadata()
    {
        _tmp.File(@"d\file.bin", 1234);
        _tmp.Dir(@"d\sub");
        var ro = _tmp.File(@"d\ro.txt", 1);
        File.SetAttributes(ro, FileAttributes.ReadOnly | FileAttributes.Hidden);

        var entries = DirectoryReader.List(_tmp.P("d")).ToDictionary(e => e.Name);

        Assert.Equal(3, entries.Count);
        Assert.Equal(1234, entries["file.bin"].Size);
        Assert.False(entries["file.bin"].IsDirectory);
        Assert.True(entries["sub"].IsDirectory);
        Assert.False(entries["sub"].IsLink);
        Assert.True(entries["ro.txt"].IsReadOnly && entries["ro.txt"].IsHidden);
        Assert.Equal(DiskSize.RoundUp(1234, Cluster), entries["file.bin"].AllocatedSize(Cluster));
        Assert.Equal(0, entries["sub"].AllocatedSize(Cluster));
        File.SetAttributes(ro, FileAttributes.Normal);
    }

    [Fact]
    public void StatExistsAndRealDirectory()
    {
        var f = _tmp.File("x.txt", 10);
        Assert.Equal("x.txt", DirectoryReader.Stat(f)?.Name);
        Assert.Null(DirectoryReader.Stat(_tmp.P("missing")));
        Assert.True(DirectoryReader.Exists(f));
        Assert.False(DirectoryReader.Exists(_tmp.P("missing")));
        Assert.True(DirectoryReader.IsRealDirectory(_tmp.Root));
        Assert.False(DirectoryReader.IsRealDirectory(f));
        Assert.True(DirectoryReader.Stat(KnownPaths.SystemDrive)?.IsDirectory); // gốc ổ
        Assert.Throws<Win32IOException>(() => DirectoryReader.Read(_tmp.P(@"missing\deeper"), _ => { }));
        Assert.Empty(DirectoryReader.List(_tmp.P("missing")));
    }

    [Theory]
    [InlineData(0, 4096, 0)]
    [InlineData(1, 4096, 4096)]
    [InlineData(4096, 4096, 4096)]
    [InlineData(4097, 4096, 8192)]
    [InlineData(100, 0, 100)]
    [InlineData(-5, 4096, 0)]
    public void RoundUpToCluster(long size, long cluster, long expected) => Assert.Equal(expected, DiskSize.RoundUp(size, cluster));

    [Fact]
    public void MeasureCountsAllocatedSizeAndItems()
    {
        _tmp.File(@"m\a.bin", 10_000);
        _tmp.File(@"m\sub\b.bin", 5_000);
        _tmp.File(@"m\sub\deeper\c.bin", 1);
        _tmp.Dir(@"m\empty");
        var c = Cluster;
        var expected = DiskSize.RoundUp(10_000, c) + DiskSize.RoundUp(5_000, c) + DiskSize.RoundUp(1, c);

        var counter = new VisitCounter();
        var m = _fs.Measure(_tmp.P("m"), counter);
        var mc = _fs.MeasureConcurrently(_tmp.P("m"));

        Assert.True(m.Exists && m.IsDirectory);
        Assert.Equal(expected, m.AllocatedSize);
        Assert.Equal(6, m.ItemCount); // a.bin, sub, b.bin, deeper, c.bin, empty
        Assert.Equal(6, counter.Count);
        Assert.Equal(m.AllocatedSize, mc.AllocatedSize);
        Assert.Equal(m.ItemCount, mc.ItemCount);

        var file = _fs.Measure(_tmp.P(@"m\a.bin"));
        Assert.False(file.IsDirectory);
        Assert.Equal(1, file.ItemCount);
        Assert.Equal(PathMeasurement.Missing, _fs.Measure(_tmp.P("nope")));
    }

    [Fact]
    public void MeasureSkipCallbackPrunesSubtrees()
    {
        _tmp.File(@"m\keep\big.bin", 100_000);
        _tmp.File(@"m\small.bin", 10);
        var m = _fs.Measure(_tmp.P("m"), skip: p => p.EndsWith(@"\keep", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(DiskSize.RoundUp(10, Cluster), m.AllocatedSize);
        Assert.Equal(1, m.ItemCount);
    }

    [Fact]
    public void LastUseIsNewestOfWriteAndAccess()
    {
        var old = DateTime.UtcNow.AddDays(-50);
        var recent = DateTime.UtcNow.AddDays(-2);
        var withAccess = new PathMeasurement(0, 1, old, recent, false, true);
        Assert.Equal(recent, withAccess.LastUseUtc);
        Assert.Equal(recent, (withAccess with { NewestModificationUtc = recent, NewestAccessUtc = old }).LastUseUtc);
        Assert.Equal(old, (withAccess with { NewestAccessUtc = null }).LastUseUtc);
    }

    [SkippableFact]
    public void MeasureDoesNotFollowJunctions()
    {
        _tmp.File(@"outside\huge.bin", 200_000);
        _tmp.File(@"m\own.bin", 10);
        Skip.IfNot(Links.TryCreateJunction(_tmp.P(@"m\link"), _tmp.P("outside")), Links.JunctionSkipReason);

        var m = _fs.Measure(_tmp.P("m"));
        Assert.Equal(DiskSize.RoundUp(10, Cluster), m.AllocatedSize);
        var link = DirectoryReader.Stat(_tmp.P(@"m\link"));
        Assert.True(link?.IsLink);
        Assert.Equal(0L, link?.AllocatedSize(Cluster));
        var linkMeasure = _fs.Measure(_tmp.P(@"m\link"));
        Assert.False(linkMeasure.IsDirectory);
        Assert.Equal(0, linkMeasure.AllocatedSize);

        var visited = new List<string>();
        _fs.Walk(_tmp.P("m"), (p, _) => { visited.Add(Path.GetFileName(p)); return WalkDecision.Continue; });
        Assert.Equal(new[] { "link", "own.bin" }, visited.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void WalkHonorsSkipAndStop()
    {
        _tmp.File(@"w\a\1.txt");
        _tmp.File(@"w\b\2.txt");
        _tmp.File(@"w\c.txt");
        var seen = new List<string>();
        _fs.Walk(_tmp.P("w"), (p, e) =>
        {
            seen.Add(Path.GetRelativePath(_tmp.P("w"), p));
            return e.IsDirectory && e.Name == "a" ? WalkDecision.SkipDescendants : WalkDecision.Continue;
        });
        Assert.Contains("a", seen);
        Assert.DoesNotContain(@"a\1.txt", seen);
        Assert.Contains(@"b\2.txt", seen);

        var count = 0;
        _fs.Walk(_tmp.P("w"), (_, _) => ++count >= 2 ? WalkDecision.Stop : WalkDecision.Continue);
        Assert.Equal(2, count);
    }

    [Fact]
    public void CancellationStopsMeasuring()
    {
        for (var i = 0; i < 20; i++) _tmp.File($@"big\sub{i}\x.bin", 10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => _fs.MeasureConcurrently(_tmp.P("big"), ct: cts.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => _fs.Measure(_tmp.P("big"), ct: cts.Token));
    }

    [Fact]
    public void VolumeInfoForSystemDrive()
    {
        var v = _fs.SystemVolume;
        Assert.NotNull(v);
        Assert.True(v.Total > 0);
        Assert.InRange(v.UsedFraction, 0, 1);
        Assert.True(v.IsSystem);
        Assert.InRange(_fs.RecommendedConcurrency(_tmp.Root), 1, 4);
    }
}
