namespace CleanBoost.Core;

/// <summary>
/// Glob đã biên dịch (SYSTEM_DESIGN mục 8.3, 8.6) cho đường dẫn Windows: hỗ trợ <c>*</c>, <c>?</c>, <c>[...]</c> trong một
/// thành phần và <c>**</c> cho không hoặc nhiều thành phần. So khớp không phân biệt hoa thường như NTFS.
/// Chấp nhận cả <c>/</c> lẫn <c>\</c>; thành phần đầu là ổ đĩa (<c>C:</c>).
/// </summary>
public sealed class Glob : IEquatable<Glob>
{
    public enum SegmentKind { Literal, Wildcard, Globstar }

    public readonly record struct Segment(SegmentKind Kind, string Text);

    public string Pattern { get; }
    public IReadOnlyList<Segment> Segments { get; }

    public Glob(string pattern, string? home = null)
    {
        var p = pattern.Replace('/', '\\');
        home ??= KnownPaths.UserHome;
        if (p == "~") p = home;
        else if (p.StartsWith("~\\", StringComparison.Ordinal)) p = home + p[1..];
        p = p.Replace("${userHome}", home, StringComparison.Ordinal);
        Pattern = p.Length > 3 ? p.TrimEnd('\\') : p;
        Segments = SplitPath(Pattern).Select(s => s == "**"
            ? new Segment(SegmentKind.Globstar, s)
            : HasMagic(s) ? new Segment(SegmentKind.Wildcard, s) : new Segment(SegmentKind.Literal, s)).ToArray();
    }

    public override string ToString() => Pattern;

    public static bool HasMagic(ReadOnlySpan<char> s) => s.IndexOfAny('*', '?', '[') >= 0;
    public static bool HasMagic(string s) => HasMagic(s.AsSpan());

    /// <summary>
    /// Đổi một đường dẫn thật thành mẫu khớp đúng chính nó: <c>[</c>, <c>*</c>, <c>?</c> thành lớp ký tự
    /// (<c>D:\[Work] Docs</c> → <c>D:\[[]Work] Docs</c>). Dùng khi ghép đường dẫn thật vào mẫu của PathPolicy.
    /// </summary>
    public static string EscapeLiteral(string path)
    {
        if (!HasMagic(path)) return path;
        var sb = new System.Text.StringBuilder(path.Length + 8);
        foreach (var c in path)
        {
            sb.Append(c switch
            {
                '[' => "[[]",
                '*' => "[*]",
                '?' => "[?]",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }

    public bool IsLiteral => Segments.All(s => s.Kind == SegmentKind.Literal);

    /// <summary>Phần đường dẫn cố định trước thành phần wildcard đầu tiên, ví dụ <c>C:\Users\a\AppData\Local\Temp</c>.</summary>
    public string LiteralPrefix
    {
        get
        {
            var parts = new List<string>();
            foreach (var seg in Segments)
            {
                if (seg.Kind != SegmentKind.Literal) break;
                parts.Add(seg.Text);
            }
            return JoinPath(parts);
        }
    }

    /// <summary>Đường dẫn có khớp toàn bộ glob không.</summary>
    public bool Matches(string path) => Match(0, SplitPath(path), 0);

    /// <summary>Đường dẫn có thể là tổ tiên của một đường dẫn khớp không (cắt nhánh sớm khi duyệt).</summary>
    public bool CouldMatchDescendant(string path) => PrefixMatch(0, SplitPath(path), 0);

    /// <summary>Đường dẫn khớp glob hoặc nằm bên trong một đường dẫn khớp glob.</summary>
    public bool MatchesSelfOrAncestor(string path)
    {
        var comps = SplitPath(path);
        for (var n = comps.Length; n > 0; n--)
        {
            if (Match(0, comps, 0, n)) return true;
        }
        return false;
    }

    private bool Match(int si, string[] comps, int ci, int end = -1)
    {
        if (end < 0) end = comps.Length;
        while (true)
        {
            if (si == Segments.Count) return ci == end;
            var seg = Segments[si];
            switch (seg.Kind)
            {
                case SegmentKind.Globstar:
                    for (var k = ci; k <= end; k++)
                    {
                        if (Match(si + 1, comps, k, end)) return true;
                    }
                    return false;
                case SegmentKind.Literal:
                    if (ci >= end || !string.Equals(comps[ci], seg.Text, StringComparison.OrdinalIgnoreCase)) return false;
                    break;
                default:
                    if (ci >= end || !ComponentMatches(seg.Text, comps[ci])) return false;
                    break;
            }
            si++;
            ci++;
        }
    }

    private bool PrefixMatch(int si, string[] comps, int ci)
    {
        while (true)
        {
            if (ci == comps.Length) return true;
            if (si == Segments.Count) return false;
            var seg = Segments[si];
            switch (seg.Kind)
            {
                case SegmentKind.Globstar: return true;
                case SegmentKind.Literal:
                    if (!string.Equals(comps[ci], seg.Text, StringComparison.OrdinalIgnoreCase)) return false;
                    break;
                default:
                    if (!ComponentMatches(seg.Text, comps[ci])) return false;
                    break;
            }
            si++;
            ci++;
        }
    }

    /// <summary>So khớp một thành phần kiểu fnmatch, không phân biệt hoa thường.</summary>
    public static bool ComponentMatches(string pattern, string name) => WildMatch(pattern.AsSpan(), name.AsSpan());

    private static bool WildMatch(ReadOnlySpan<char> p, ReadOnlySpan<char> s)
    {
        int pi = 0, si = 0, starP = -1, starS = 0;
        while (si < s.Length)
        {
            if (pi < p.Length)
            {
                var c = p[pi];
                if (c == '*')
                {
                    starP = pi++;
                    starS = si;
                    continue;
                }
                if (c == '?')
                {
                    pi++;
                    si++;
                    continue;
                }
                if (c == '[')
                {
                    var consumed = MatchClass(p[pi..], s[si], out var ok);
                    if (consumed > 0)
                    {
                        if (ok)
                        {
                            pi += consumed;
                            si++;
                            continue;
                        }
                    }
                    else if (EqualsIgnoreCase(c, s[si]))
                    {
                        pi++;
                        si++;
                        continue;
                    }
                }
                else if (EqualsIgnoreCase(c, s[si]))
                {
                    pi++;
                    si++;
                    continue;
                }
            }
            if (starP < 0) return false;
            pi = starP + 1;
            si = ++starS;
        }
        while (pi < p.Length && p[pi] == '*') pi++;
        return pi == p.Length;
    }

    /// <summary>Trả về số ký tự của lớp <c>[...]</c> (0 nếu không hợp lệ, khi đó <c>[</c> là ký tự thường).</summary>
    private static int MatchClass(ReadOnlySpan<char> p, char ch, out bool matched)
    {
        matched = false;
        var i = 1;
        var negate = i < p.Length && (p[i] == '!' || p[i] == '^');
        if (negate) i++;
        var start = i;
        var hit = false;
        while (i < p.Length && (p[i] != ']' || i == start))
        {
            var lo = p[i];
            if (i + 2 < p.Length && p[i + 1] == '-' && p[i + 2] != ']')
            {
                var hi = p[i + 2];
                var c = char.ToUpperInvariant(ch);
                if (c >= char.ToUpperInvariant(lo) && c <= char.ToUpperInvariant(hi)) hit = true;
                else if (ch >= lo && ch <= hi) hit = true;
                i += 3;
            }
            else
            {
                if (EqualsIgnoreCase(lo, ch)) hit = true;
                i++;
            }
        }
        if (i >= p.Length) return 0;
        matched = hit != negate;
        return i + 1;
    }

    private static bool EqualsIgnoreCase(char a, char b) => a == b || char.ToUpperInvariant(a) == char.ToUpperInvariant(b);

    /// <summary>Tách đường dẫn thành các thành phần, bỏ phần rỗng và tiền tố <c>\\?\</c>.</summary>
    public static string[] SplitPath(string path)
    {
        var p = path;
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p[4..];
        return p.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Ghép các thành phần thành đường dẫn tuyệt đối: <c>C:</c> + <c>\a\b</c>.</summary>
    public static string JoinPath(IReadOnlyList<string> parts)
    {
        if (parts.Count == 0) return "";
        if (parts.Count == 1) return parts[0].EndsWith(':') ? parts[0] + "\\" : parts[0];
        return string.Join('\\', parts);
    }

    public bool Equals(Glob? other) => other is not null && string.Equals(Pattern, other.Pattern, StringComparison.OrdinalIgnoreCase);
    public override bool Equals(object? obj) => obj is Glob g && Equals(g);
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Pattern);
}

/// <summary>Tập glob dùng cho <c>exclude</c> (luôn thắng <c>match</c>).</summary>
public sealed class GlobSet
{
    public static readonly GlobSet Empty = new([]);
    public IReadOnlyList<Glob> Globs { get; }

    public GlobSet(IEnumerable<Glob> globs) => Globs = globs.ToArray();
    public GlobSet(IEnumerable<string> patterns, string? home = null) => Globs = patterns.Select(p => new Glob(p, home)).ToArray();

    public bool IsEmpty => Globs.Count == 0;
    public bool Matches(string path) => Globs.Any(g => g.Matches(path));
    public bool MatchesSelfOrAncestor(string path) => Globs.Any(g => g.MatchesSelfOrAncestor(path));
}
