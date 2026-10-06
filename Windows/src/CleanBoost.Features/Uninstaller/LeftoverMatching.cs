using System.Globalization;
using System.Text;
using CleanBoost.Core;
using CleanBoost.Engine.Apps;
using CleanBoost.Engine.Nodes;
using CleanBoost.Engine.Rules;

namespace CleanBoost.Features.Uninstaller;

/// <summary>Nhóm kết quả riêng của Gỡ cài đặt (LeftoverMatching.swift của bản Mac, spec mục 4.1).</summary>
public static class UninstallerCategory
{
    /// <summary>File sót của một app đang gỡ (rule <c>app.*.leftovers</c> cũng thuộc nhóm này).</summary>
    public const string AppLeftovers = RuleCategory.AppLeftovers;
    /// <summary>File sót của app đã gỡ từ trước (không còn trong danh sách Uninstall).</summary>
    public const string OrphanedLeftovers = "orphanedLeftovers";
    /// <summary>Node "chạy trình gỡ của app".</summary>
    public const string Applications = "applications";
}

/// <summary>Mức tin cậy khi khớp file sót với app (bảng spec mục 4.1), thứ tự certain &lt; high &lt; medium &lt; low.</summary>
public enum LeftoverConfidence
{
    Certain = 0,
    High = 1,
    Medium = 2,
    Low = 3,
}

public static class LeftoverConfidenceExtensions
{
    public static readonly LeftoverConfidence[] All = [LeftoverConfidence.Certain, LeftoverConfidence.High, LeftoverConfidence.Medium, LeftoverConfidence.Low];

    /// <summary>Chuỗi nguồn tiếng Việt (khoá dịch).</summary>
    public static string TitleKey(this LeftoverConfidence c) => c switch
    {
        LeftoverConfidence.Certain => "Chắc chắn",
        LeftoverConfidence.High => "Cao",
        LeftoverConfidence.Medium => "Trung bình",
        _ => "Thấp",
    };

    public static string Title(this LeftoverConfidence c) => Loc.T(c.TitleKey());

    /// <summary>
    /// Giải thích theo nguồn khớp của bản Windows: thư mục cài đặt/gói Store thay cho bundle ID, tên app/nhà phát hành
    /// trong AppData và lối tắt thay cho LaunchAgent, khoá registry thay cho biên nhận pkg.
    /// </summary>
    public static string ExplanationKey(this LeftoverConfidence c) => c switch
    {
        LeftoverConfidence.Certain => "Nằm trong thư mục cài đặt của app, gói Microsoft Store của app, hoặc lối tắt trỏ vào app",
        LeftoverConfidence.High => "Khớp tên app hoặc nhà phát hành trong thư mục dữ liệu chuẩn, hoặc rule riêng của app",
        LeftoverConfidence.Medium => "Thư mục hoặc khoá registry mang tên app hay nhà phát hành, hãy xem lại trước khi xoá",
        _ => "Tên gần giống app, hãy xem lại trước khi xoá",
    };

    public static string Explanation(this LeftoverConfidence c) => Loc.T(c.ExplanationKey());

    public static string Symbol(this LeftoverConfidence c) => c switch
    {
        LeftoverConfidence.Certain => "checkmark.seal.fill",
        LeftoverConfidence.High => "checkmark.circle",
        LeftoverConfidence.Medium => "key",
        _ => "questionmark.circle",
    };

    /// <summary>Chắc chắn/Cao → safe (chọn sẵn); Trung bình/Thấp → review.</summary>
    public static SafetyLevel Safety(this LeftoverConfidence c) => c <= LeftoverConfidence.High ? SafetyLevel.Safe : SafetyLevel.Review;
}

/// <summary>Một mục file sót kèm mức tin cậy.</summary>
public sealed record LeftoverItem(LeftoverConfidence Confidence, Node Node);

/// <summary>Kết quả tìm file sót cho một app.</summary>
public sealed record LeftoverReport(InstalledApp App, IReadOnlyList<LeftoverItem> Items)
{
    public IEnumerable<LeftoverItem> ItemsWith(LeftoverConfidence c) => Items.Where(i => i.Confidence == c);
    public ByteCount TotalSize => Items.Sum(i => i.Node.Size);
}

/// <summary>
/// Hàm thuần so khớp tên (LeftoverMatcher của bản Mac). Windows không có bundle ID nên so theo tên đã chuẩn hoá:
/// bỏ phần trong ngoặc, số phiên bản, kiến trúc ("x64", "64-bit"), chỉ giữ chữ và số, chữ thường
/// ("7-Zip 23.01 (x64)" → "7zip", "Mozilla Firefox (x64 en-US)" → "mozillafirefox").
/// </summary>
public static class LeftoverMatcher
{
    /// <summary>Độ dài tối thiểu của khoá tên để so khớp chính xác (Mac: tên app ≥ 3 ký tự).</summary>
    public const int MinExactKey = 3;
    /// <summary>Độ dài tối thiểu để so "gần giống" (mức Thấp).</summary>
    public const int MinSimilarKey = 5;

    /// <summary>"version 1.17.0", "phiên bản 6.6.113", "ver. 2", "v 3.1" (chữ kèm số phiên bản phía sau).</summary>
    private static readonly System.Text.RegularExpressions.Regex s_versionPhrase = new(
        @"(?<![\p{L}\p{N}])(version|phiên bản|phien ban|ver\.?|v\.)\s*\d[\p{L}\p{N}._\-]*", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly HashSet<string> s_architectureTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "x64", "x86", "x32", "amd64", "arm64", "arm", "ia64", "64bit", "32bit", "64-bit", "32-bit", "win64", "win32", "64", "32",
        "version", "ver", "build", "release",
    };

    private static readonly HashSet<string> s_companySuffixes = new(StringComparer.Ordinal)
    {
        "inc", "incorporated", "llc", "ltd", "limited", "corp", "corporation", "co", "company", "gmbh", "ag", "sa", "sas", "srl", "sro",
        "bv", "nv", "plc", "pty", "oy", "ab", "as", "aps", "kg", "kk", "spa", "team", "software", "technologies", "technology", "development",
        "developers", "labs", "studio", "studios",
    };

    /// <summary>Chuẩn hoá tên app/thư mục để so sánh.</summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var s = s_versionPhrase.Replace(StripBrackets(name), " ");
        var tokens = s.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(tokens.Length);
        foreach (var raw in tokens)
        {
            var t = raw.Trim(',', ';', ':', '-', '_');
            if (t.Length == 0) continue;
            if (s_architectureTokens.Contains(t)) continue;
            if (IsVersionToken(t)) continue;
            kept.Add(t);
        }
        // Chỉ còn số (vd "2024") thì giữ lại để không thành chuỗi rỗng.
        if (kept.Count == 0) kept.AddRange(tokens.Where(t => !s_architectureTokens.Contains(t)));
        return LettersAndDigits(string.Join("", kept));
    }

    /// <summary>Chuẩn hoá tên nhà phát hành: bỏ hậu tố công ty ("Inc.", "LLC", "s.r.o.", "Corporation"...).</summary>
    public static string NormalizePublisher(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return "";
        var s = StripBrackets(publisher);
        var tokens = s.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries).Select(LettersAndDigits).Where(t => t.Length > 0).ToList();
        if (tokens.Count > 1 && tokens[0] == "the") tokens.RemoveAt(0);
        while (tokens.Count > 1 && s_companySuffixes.Contains(tokens[^1])) tokens.RemoveAt(tokens.Count - 1);
        return string.Join("", tokens);
    }

    /// <summary>Chỉ giữ chữ cái và chữ số (kể cả tiếng Việt), chữ thường, bỏ dấu để "Cốc Cốc" khớp "Coc Coc".</summary>
    public static string LettersAndDigits(string s)
    {
        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (c is 'đ' or 'Đ')
            {
                sb.Append('d');
                continue;
            }
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static string StripBrackets(string s)
    {
        var sb = new StringBuilder(s.Length);
        var depth = 0;
        foreach (var c in s)
        {
            if (c is '(' or '[' or '{') { depth++; continue; }
            if (c is ')' or ']' or '}') { depth = Math.Max(0, depth - 1); continue; }
            if (depth == 0) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>"1.2.3", "v2026.2.1", "23.01", "3.12.1rc2", "2.0-beta".</summary>
    internal static bool IsVersionToken(string t)
    {
        var s = t.StartsWith('v') || t.StartsWith('V') ? t[1..] : t;
        if (s.Length == 0 || !char.IsAsciiDigit(s[0])) return false;
        var dots = 0;
        var i = 0;
        while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] is '.' or '_'))
        {
            if (s[i] is '.' or '_') dots++;
            i++;
        }
        // "23.01" / "1.2.3" là phiên bản; "7" (số đứng riêng) do Normalize xử lý như token thường.
        return dots >= 1;
    }

    /// <summary>
    /// Khoá tên của một app: tên chuẩn hoá, và tên bỏ tiền tố nhà phát hành ("mozillafirefox" → "firefox")
    /// để khớp <c>&lt;Hãng&gt;\&lt;App&gt;</c>.
    /// </summary>
    public static IReadOnlyList<string> NameKeys(InstalledApp app)
    {
        var keys = new List<string>();
        var full = Normalize(app.Name);
        if (full.Length >= MinExactKey) keys.Add(full);
        var pub = NormalizePublisher(app.Publisher);
        if (pub.Length >= MinExactKey && full.StartsWith(pub, StringComparison.Ordinal) && full.Length - pub.Length >= MinExactKey)
            keys.Add(full[pub.Length..]);
        return keys;
    }

    /// <summary>
    /// Tên thư mục giống tên app: bắt đầu bằng khoá tên ("calibre-cache", "mqtt-explorer-updater"), hoặc khoá tên bắt đầu bằng
    /// tên thư mục; khoá đủ dài. Không so "chứa ở giữa" để tránh "checkpoint-nodejs" khớp "Node.js".
    /// </summary>
    public static bool IsSimilar(string normalizedEntry, string appKey) =>
        appKey.Length >= MinSimilarKey && normalizedEntry.Length >= MinSimilarKey && normalizedEntry != appKey
        && (normalizedEntry.StartsWith(appKey, StringComparison.Ordinal) || appKey.StartsWith(normalizedEntry, StringComparison.Ordinal));

    /// <summary>Tên thư mục không mang thông tin về app (không bao giờ dùng để khớp hay coi là file sót).</summary>
    public static readonly HashSet<string> GenericNames = new(StringComparer.Ordinal)
    {
        "cache", "caches", "log", "logs", "temp", "tmp", "data", "config", "configs", "settings", "backup", "backups", "download", "downloads",
        "update", "updates", "updater", "crashdumps", "crashpad", "crashreports", "plugins", "extensions", "profiles", "profile", "userdata",
        "assets", "resources", "bin", "lib", "app", "apps", "application", "applications", "program", "programs", "common", "shared",
        "local", "roaming", "locallow", "default", "storage", "db", "database", "files", "installer", "installers", "setup", "uninstall",
        "uninstaller", "packages", "package", "runtime", "runtimes", "tools", "service", "services", "client", "server", "helper", "launcher",
        "desktop", "documents", "media", "images", "fonts", "themes", "skins", "license", "licenses", "state", "localstate", "sessions",
        // Framework/thành phần dùng chung: không rõ app nào sở hữu.
        "cef", "chromium", "electron", "webview", "webview2", "ebwebview", "qtwebengine", "nwjs", "squirreltemp", "dotnet", "java", "jre", "jdk",
    };
}
