using System.Text;
using System.Text.RegularExpressions;
using CleanBoost.Core;

namespace CleanBoost.Features.Duplicates;

/// <summary>
/// Gợi ý bản nên giữ trong một nhóm trùng lặp (KeepAdvisor của bản Mac, mục 11.8), theo thứ tự ưu tiên:
/// 1. thư mục quan trọng hơn (Documents &gt; Desktop &gt; Pictures/Videos/Music &gt; thư mục khác &gt; Downloads);
/// 2. tên không có dấu hiệu bản sao ("copy", "bản sao", "(1)", " 2", "- Copy" của Explorer);
/// 3. ngày tạo cũ hơn; 4. đường dẫn ngắn hơn; 5. đường dẫn nhỏ hơn theo thứ tự chữ.
/// </summary>
public static partial class KeepAdvisor
{
    public sealed record Candidate(string Path, DateTime DateUtc);

    /// <summary>Thư mục chuẩn thật của người dùng (có thể đã chuyển sang ổ khác).</summary>
    public sealed record Folders(string Home, string Documents, string Desktop, IReadOnlyList<string> Media, string Downloads)
    {
        public static Folders Current { get; } = new(KnownPaths.UserHome, KnownPaths.Documents, KnownPaths.Desktop,
            [KnownPaths.Pictures, KnownPaths.Videos, KnownPaths.Music], KnownPaths.Downloads);
    }

    public static int Importance(string path, Folders folders)
    {
        static bool In(string p, string root) => !string.IsNullOrEmpty(root) && PathPolicy.IsSameOrInside(p, root);
        if (In(path, folders.Documents)) return 5;
        if (In(path, folders.Desktop)) return 4;
        if (folders.Media.Any(m => In(path, m))) return 3;
        if (In(path, folders.Downloads)) return 1;
        return 2;
    }

    [GeneratedRegex(@"\(\d+\)$")]
    private static partial Regex NumberedSuffix();

    [GeneratedRegex(@" \d{1,2}$")]
    private static partial Regex SpaceNumberSuffix();

    /// <summary>Tên (bỏ đuôi) chứa "copy", "bản sao", "ban sao", hoặc kết thúc bằng "(1)" hay " 2".</summary>
    public static bool HasCopyMarker(string fileName)
    {
        var name = fileName.Normalize(NormalizationForm.FormC);
        var dot = name.LastIndexOf('.');
        var stem = (dot > 0 ? name[..dot] : name).ToLowerInvariant();
        if (stem.Contains("copy", StringComparison.Ordinal) || stem.Contains("bản sao", StringComparison.Ordinal) || stem.Contains("ban sao", StringComparison.Ordinal))
            return true;
        return NumberedSuffix().IsMatch(stem) || SpaceNumberSuffix().IsMatch(stem);
    }

    /// <summary>Chỉ số bản nên giữ, <c>null</c> nếu danh sách rỗng.</summary>
    public static int? SuggestKeep(IReadOnlyList<Candidate> files, Folders? folders = null)
    {
        if (files.Count == 0) return null;
        folders ??= Folders.Current;
        var best = 0;
        for (var i = 1; i < files.Count; i++)
        {
            if (IsBetter(files[i], files[best], folders)) best = i;
        }
        return best;
    }

    public static bool IsBetter(Candidate a, Candidate b, Folders folders)
    {
        var ia = Importance(a.Path, folders);
        var ib = Importance(b.Path, folders);
        if (ia != ib) return ia > ib;
        var ca = HasCopyMarker(Path.GetFileName(a.Path));
        var cb = HasCopyMarker(Path.GetFileName(b.Path));
        if (ca != cb) return !ca;
        if (a.DateUtc != b.DateUtc) return a.DateUtc < b.DateUtc;
        if (a.Path.Length != b.Path.Length) return a.Path.Length < b.Path.Length;
        return string.CompareOrdinal(a.Path, b.Path) < 0;
    }
}
