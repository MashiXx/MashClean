using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CleanBoost.Core;

/// <summary>Ngôn ngữ giao diện người dùng chọn.</summary>
public enum AppLanguage
{
    System,
    Vietnamese,
    English,
}

public static class AppLanguageExtensions
{
    public static string Code(this AppLanguage l) => l switch
    {
        AppLanguage.Vietnamese => "vi",
        AppLanguage.English => "en",
        _ => "system",
    };

    public static AppLanguage ParseLanguage(string? code) => code switch
    {
        "vi" => AppLanguage.Vietnamese,
        "en" => AppLanguage.English,
        _ => AppLanguage.System,
    };

    /// <summary>Tên luôn hiển thị song ngữ để người dùng nhận ra dù đang ở ngôn ngữ nào.</summary>
    public static string DisplayName(this AppLanguage l) => l switch
    {
        AppLanguage.Vietnamese => "Tiếng Việt",
        AppLanguage.English => "English",
        _ => "Theo hệ thống · System",
    };
}

/// <summary>
/// Bản địa hoá. Giống bản Mac: chuỗi giao diện viết bằng tiếng Việt ngay trong code, tiếng Anh tra theo khoá là chuỗi
/// tiếng Việt trong <c>en.json</c>. Khoá giữ nguyên định dạng của Mac (<c>%@</c>, <c>%lld</c>) để dùng lại bản dịch.
/// </summary>
public static class Loc
{
    private static Dictionary<string, string> s_english = new(StringComparer.Ordinal);

    /// <summary>Mã ngôn ngữ đang hiển thị: "vi" hoặc "en".</summary>
    public static string Active { get; private set; } = DetectSystem();

    public static bool IsEnglish => Active == "en";

    public static CultureInfo Culture => IsEnglish ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("vi-VN");

    public static event Action? LanguageChanged;

    public static void Apply(AppLanguage language)
    {
        var next = language switch
        {
            AppLanguage.English => "en",
            AppLanguage.Vietnamese => "vi",
            _ => DetectSystem(),
        };
        if (next == Active) return;
        Active = next;
        LanguageChanged?.Invoke();
    }

    private static string DetectSystem() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("vi", StringComparison.OrdinalIgnoreCase) ? "vi" : "en";

    /// <summary>Nạp thêm bản dịch tiếng Anh (gộp nhiều file, file sau ghi đè file trước).</summary>
    public static void LoadEnglish(Stream json)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        var merged = new Dictionary<string, string>(s_english, StringComparer.Ordinal);
        foreach (var (k, v) in dict)
        {
            if (!string.IsNullOrEmpty(v)) merged[k] = v;
        }
        s_english = merged;
    }

    public static void LoadEnglish(IReadOnlyDictionary<string, string> dict)
    {
        var merged = new Dictionary<string, string>(s_english, StringComparer.Ordinal);
        foreach (var (k, v) in dict)
        {
            if (!string.IsNullOrEmpty(v)) merged[k] = v;
        }
        s_english = merged;
    }

    public static int EnglishCount => s_english.Count;

    /// <summary>Bản tiếng Anh của khoá, bất kể ngôn ngữ đang hiển thị (dùng cho <see cref="LocalizedText.Source"/>).</summary>
    public static string LookupEnglish(string key) => s_english.TryGetValue(key, out var v) ? v : key;

    /// <summary>Dịch một chuỗi nguồn tiếng Việt.</summary>
    public static string T(string key)
    {
        if (!IsEnglish) return key;
        return s_english.TryGetValue(key, out var v) ? v : key;
    }

    /// <summary>Dịch rồi điền tham số theo định dạng kiểu Mac (<c>%@</c>, <c>%lld</c>, <c>%d</c>, <c>%.1f</c>, <c>%1$@</c>).</summary>
    public static string F(string key, params object?[] args) => FormatPrintf(T(key), args, Culture);

    /// <summary>Định dạng kiểu printf rút gọn đủ cho các khoá của bản Mac.</summary>
    public static string FormatPrintf(string format, object?[] args, CultureInfo culture)
    {
        var sb = new StringBuilder(format.Length + 16);
        var next = 0;
        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if (c != '%' || i + 1 >= format.Length)
            {
                sb.Append(c);
                continue;
            }
            if (format[i + 1] == '%')
            {
                sb.Append('%');
                i++;
                continue;
            }
            // %[n$][.p](@|d|i|u|f|ld|lld|lu|llu)
            var j = i + 1;
            int? position = null;
            var digitsStart = j;
            while (j < format.Length && char.IsDigit(format[j])) j++;
            if (j > digitsStart && j < format.Length && format[j] == '$')
            {
                position = int.Parse(format.AsSpan(digitsStart, j - digitsStart), CultureInfo.InvariantCulture) - 1;
                j++;
            }
            else
            {
                j = digitsStart;
            }
            int? precision = null;
            if (j < format.Length && format[j] == '.')
            {
                var ps = ++j;
                while (j < format.Length && char.IsDigit(format[j])) j++;
                precision = j > ps ? int.Parse(format.AsSpan(ps, j - ps), CultureInfo.InvariantCulture) : 0;
            }
            while (j < format.Length && format[j] == 'l') j++;
            if (j >= format.Length)
            {
                sb.Append(format, i, format.Length - i);
                break;
            }
            var conv = format[j];
            if (conv is not ('@' or 'd' or 'i' or 'u' or 'f' or 's'))
            {
                sb.Append(c);
                continue;
            }
            var index = position ?? next++;
            var arg = index >= 0 && index < args.Length ? args[index] : null;
            sb.Append(FormatArg(arg, conv, precision, culture));
            i = j;
        }
        return sb.ToString();
    }

    private static string FormatArg(object? arg, char conv, int? precision, CultureInfo culture)
    {
        if (arg is null) return "";
        if (conv == 'f')
        {
            var d = Convert.ToDouble(arg, CultureInfo.InvariantCulture);
            return d.ToString("F" + (precision ?? 6).ToString(CultureInfo.InvariantCulture), culture);
        }
        return arg switch
        {
            string s => s,
            ByteCount b => b.Formatted,
            IFormattable f when conv is 'd' or 'i' or 'u' => f.ToString("0", culture),
            IFormattable f => f.ToString(null, culture),
            _ => arg.ToString() ?? "",
        };
    }
}
