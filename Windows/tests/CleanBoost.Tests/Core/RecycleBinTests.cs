using System.Text;
using CleanBoost.Core;
using CleanBoost.Core.FileSystem;

namespace CleanBoost.Tests.Core;

/// <summary>Đọc file <c>$I</c> của Thùng rác (mục 15.3) và vòng chuyển vào Thùng rác → tìm lại → khôi phục.</summary>
public sealed class RecycleBinTests : IDisposable
{
    private readonly TempDir _tmp = new("bin");

    public void Dispose() => _tmp.Dispose();

    private static readonly DateTime Deleted = new(2026, 3, 14, 15, 9, 26, DateTimeKind.Utc);

    /// <summary>$I phiên bản 1 (Vista–8.1): header 8 + size 8 + FILETIME 8 + đường dẫn cố định 520 byte.</summary>
    internal static byte[] V1(string path, long size, DateTime deletedUtc)
    {
        var bytes = new byte[24 + 520];
        BitConverter.GetBytes(1L).CopyTo(bytes, 0);
        BitConverter.GetBytes(size).CopyTo(bytes, 8);
        BitConverter.GetBytes(deletedUtc.ToFileTimeUtc()).CopyTo(bytes, 16);
        Encoding.Unicode.GetBytes(path).CopyTo(bytes, 24);
        return bytes;
    }

    /// <summary>$I phiên bản 2 (Windows 10+): header 8 + size 8 + FILETIME 8 + số ký tự (gồm NUL) 4 + đường dẫn UTF-16.</summary>
    internal static byte[] V2(string path, long size, DateTime deletedUtc, int? charsOverride = null)
    {
        var text = Encoding.Unicode.GetBytes(path + "\0");
        var bytes = new byte[28 + text.Length];
        BitConverter.GetBytes(2L).CopyTo(bytes, 0);
        BitConverter.GetBytes(size).CopyTo(bytes, 8);
        BitConverter.GetBytes(deletedUtc.ToFileTimeUtc()).CopyTo(bytes, 16);
        BitConverter.GetBytes(charsOverride ?? path.Length + 1).CopyTo(bytes, 24);
        text.CopyTo(bytes, 28);
        return bytes;
    }

    [Fact]
    public void ParsesVersion2()
    {
        var e = RecycleBin.ParseInfo(V2(@"C:\Users\t\Downloads\setup (1).exe", 123_456_789, Deleted), "$IABC.exe");
        Assert.NotNull(e);
        Assert.Equal(@"C:\Users\t\Downloads\setup (1).exe", e.OriginalPath);
        Assert.Equal(123_456_789, e.Size);
        Assert.Equal(Deleted, e.DeletedUtc);
        Assert.Equal("$IABC.exe", e.InfoPath);
    }

    [Fact]
    public void ParsesVersion1WithFixedBuffer()
    {
        var e = RecycleBin.ParseInfo(V1(@"D:\Ảnh\kỷ niệm.jpg", 42, Deleted), "$I1.jpg");
        Assert.NotNull(e);
        Assert.Equal(@"D:\Ảnh\kỷ niệm.jpg", e.OriginalPath);
        Assert.Equal(42, e.Size);
        Assert.Equal(Deleted, e.DeletedUtc);
    }

    [Fact]
    public void Version1TruncatedBufferStillReadsPath()
    {
        var full = V1(@"C:\a.txt", 1, Deleted);
        var e = RecycleBin.ParseInfo(full[..(24 + 20)], "$I");
        Assert.Equal(@"C:\a.txt", e?.OriginalPath);
    }

    [Fact]
    public void LongPathInVersion2()
    {
        var longPath = @"C:\" + string.Join('\\', Enumerable.Repeat("very-long-folder-name", 20)) + @"\file.bin";
        Assert.True(longPath.Length > 260);
        Assert.Equal(longPath, RecycleBin.ParseInfo(V2(longPath, 1, Deleted), "$I")?.OriginalPath);
    }

    [Fact]
    public void RejectsMalformedInfo()
    {
        Assert.Null(RecycleBin.ParseInfo(new byte[10], "$I"));                                        // quá ngắn
        Assert.Null(RecycleBin.ParseInfo(V2(@"C:\a", 1, Deleted, charsOverride: 10_000), "$I"));      // độ dài vượt dữ liệu
        Assert.Null(RecycleBin.ParseInfo(V2(@"C:\a", 1, Deleted, charsOverride: 0), "$I"));
        Assert.Null(RecycleBin.ParseInfo(V2(@"C:\a", 1, Deleted, charsOverride: -5), "$I"));
        var v3 = V2(@"C:\a", 1, Deleted);
        BitConverter.GetBytes(3L).CopyTo(v3, 0);
        Assert.Null(RecycleBin.ParseInfo(v3, "$I"));                                                    // phiên bản lạ
        Assert.Null(RecycleBin.ParseInfo(V2(@"C:\a", 1, Deleted)[..27], "$I"));                         // thiếu trường độ dài
    }

    [Fact]
    public void NegativeFileTimeIsClamped()
    {
        var bytes = V2(@"C:\a", 1, Deleted);
        BitConverter.GetBytes(-1L).CopyTo(bytes, 16);
        Assert.Equal(DateTime.FromFileTimeUtc(0), RecycleBin.ParseInfo(bytes, "$I")?.DeletedUtc);
    }

    [Fact]
    public void ParseInfoFromMissingFileIsNull() => Assert.Null(RecycleBin.ParseInfo(_tmp.P("$Inope.txt")));

    [Fact]
    public void EntriesPairInfoWithDataFiles()
    {
        var bin = _tmp.Dir("bin");
        File.WriteAllBytes(Path.Combine(bin, "$IAB12CD.txt"), V2(@"C:\Users\t\notes.txt", 10, Deleted));
        File.WriteAllText(Path.Combine(bin, "$RAB12CD.txt"), "0123456789");
        File.WriteAllBytes(Path.Combine(bin, "$IZZ.dat"), [1, 2, 3]); // hỏng: bỏ qua
        File.WriteAllText(Path.Combine(bin, "desktop.ini"), "x");
        _tmp.Dir(@"bin\$RFOLDER");

        var entries = RecycleBin.Entries(bin).ToList();

        var e = Assert.Single(entries);
        Assert.Equal(@"C:\Users\t\notes.txt", e.OriginalPath);
        Assert.Equal(Path.Combine(bin, "$RAB12CD.txt"), e.DataPath);
        Assert.Equal(Path.Combine(bin, "$IAB12CD.txt"), e.InfoPath);
        Assert.Empty(RecycleBin.Entries(_tmp.P("does-not-exist")));
    }

    [Fact]
    public void RestoreValidatesBeforeMoving()
    {
        var bin = _tmp.Dir("bin");
        var data = Path.Combine(bin, "$RQQ.txt");
        File.WriteAllText(data, "hello");
        File.WriteAllBytes(Path.Combine(bin, "$IQQ.txt"), V2(_tmp.P(@"orig\hello.txt"), 5, Deleted));

        Assert.Equal(RecycleBin.RestoreError.NotRestorable, RecycleBin.Restore(_tmp.File("plain.txt"), _tmp.P("x.txt"), out _));
        Assert.Equal(RecycleBin.RestoreError.TrashedItemMissing, RecycleBin.Restore(Path.Combine(bin, "$RMISSING.txt"), _tmp.P("y.txt"), out _));
        var occupied = _tmp.File("occupied.txt");
        Assert.Equal(RecycleBin.RestoreError.DestinationExists, RecycleBin.Restore(data, occupied, out _));

        // Thành công: tạo thư mục cha, chuyển file, xoá $I đi kèm.
        Assert.Null(RecycleBin.Restore(data, _tmp.P(@"orig\hello.txt"), out var message));
        Assert.Null(message);
        Assert.Equal("hello", File.ReadAllText(_tmp.P(@"orig\hello.txt")));
        Assert.False(File.Exists(data));
        Assert.False(File.Exists(Path.Combine(bin, "$IQQ.txt")));
    }

    [Fact]
    public void BinDirectoryIsPerUserPerDrive()
    {
        var bin = RecycleBin.BinDirectory(@"C:\x\y.txt");
        Assert.NotNull(bin);
        Assert.StartsWith(@"C:\$Recycle.Bin\S-1-5-", bin, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TestPolicy.Sid.Value, Path.GetFileName(bin));
    }

    [SkippableFact]
    public void MoveToTrashFindAndRestoreRoundTrip()
    {
        var path = _tmp.File($"cb-test-trash-{Guid.NewGuid():N}.txt", 300);
        var original = File.ReadAllBytes(path);
        var why = RecycleBinGuard.SkipReason(path);
        Skip.If(why is not null, why);

        string? trashed = null;
        try
        {
            var r = RecycleBin.MoveToTrash(path);
            Assert.True(r.Success, $"MoveToTrash thất bại: {r.Error} {r.Code}");
            Assert.False(File.Exists(path));
            trashed = r.TrashedPath;
            Assert.NotNull(trashed);
            Assert.StartsWith("$R", Path.GetFileName(trashed));
            Assert.True(DirectoryReader.Exists(trashed));

            // $I thật do Windows ghi phải đọc được và trỏ về đường dẫn gốc.
            var entry = RecycleBin.Entries(Path.GetDirectoryName(trashed)!).FirstOrDefault(e => e.DataPath.Equals(trashed, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(entry);
            Assert.Equal(path, entry.OriginalPath, ignoreCase: true);
            Assert.Equal(trashed, RecycleBin.FindTrashed(path, DateTime.UtcNow.AddMinutes(-5)), ignoreCase: true);

            Assert.Null(RecycleBin.Restore(trashed, path, out var message));
            Assert.Null(message);
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.False(DirectoryReader.Exists(trashed));
            trashed = null;
        }
        finally
        {
            PurgeTestItem(trashed);
        }
    }

    [Fact]
    public void MoveToTrashOnNetworkPathIsUnavailable()
    {
        Assert.False(RecycleBin.IsAvailable(@"\\server\share\x.txt"));
        Assert.Equal(RecycleBin.TrashError.Unavailable, RecycleBin.MoveToTrash(@"\\server\share\x.txt").Error);
    }

    /// <summary>Chỉ xoá đúng cặp $R/$I do test tạo nếu khôi phục thất bại giữa chừng.</summary>
    internal static void PurgeTestItem(string? trashed)
    {
        if (trashed is null) return;
        var name = Path.GetFileName(trashed);
        if (!name.StartsWith("$R", StringComparison.OrdinalIgnoreCase)) return;
        try { File.Delete(trashed); } catch { }
        try { File.Delete(Path.Combine(Path.GetDirectoryName(trashed)!, "$I" + name[2..])); } catch { }
    }
}
