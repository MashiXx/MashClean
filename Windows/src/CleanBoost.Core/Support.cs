using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CleanBoost.Core;

/// <summary>Chuỗi đa ngôn ngữ đến từ rule JSON (<c>{"en": "...", "vi": "..."}</c> hoặc một chuỗi đơn coi là tiếng Anh).</summary>
[JsonConverter(typeof(LocalizedTextJsonConverter))]
public sealed class LocalizedText : IEquatable<LocalizedText>
{
    public IReadOnlyDictionary<string, string> Values { get; }

    public LocalizedText(IReadOnlyDictionary<string, string> values) => Values = values;
    public LocalizedText(string vi, string en) => Values = new Dictionary<string, string> { ["vi"] = vi, ["en"] = en };

    /// <summary>Một chuỗi nguồn tiếng Việt: tiếng Anh tra qua <see cref="Loc"/>.</summary>
    public static LocalizedText Source(string vietnamese) => new(vietnamese, Loc.LookupEnglish(vietnamese));

    public static readonly LocalizedText Empty = new(new Dictionary<string, string>());

    /// <summary>Ngôn ngữ giao diện → tiếng Anh → tiếng Việt → bất kỳ.</summary>
    public string Resolved
    {
        get
        {
            if (Values.TryGetValue(Loc.Active, out var v) && !string.IsNullOrEmpty(v)) return v;
            if (Values.TryGetValue("en", out v) && !string.IsNullOrEmpty(v)) return v;
            if (Values.TryGetValue("vi", out v) && !string.IsNullOrEmpty(v)) return v;
            return Values.Values.FirstOrDefault() ?? "";
        }
    }

    public bool IsEmpty => Values.Count == 0 || Values.Values.All(string.IsNullOrEmpty);

    public override string ToString() => Resolved;

    public bool Equals(LocalizedText? other) =>
        other is not null && Values.Count == other.Values.Count && Values.All(kv => other.Values.TryGetValue(kv.Key, out var o) && o == kv.Value);

    public override bool Equals(object? obj) => obj is LocalizedText t && Equals(t);
    public override int GetHashCode() => Values.Aggregate(0, (h, kv) => h ^ HashCode.Combine(kv.Key, kv.Value));
}

internal sealed class LocalizedTextJsonConverter : JsonConverter<LocalizedText>
{
    public override LocalizedText Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new LocalizedText(new Dictionary<string, string> { ["en"] = reader.GetString() ?? "" });
        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(ref reader, options) ?? [];
        return new LocalizedText(dict);
    }

    public override void Write(Utf8JsonWriter writer, LocalizedText value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (k, v) in value.Values.OrderBy(kv => kv.Key, StringComparer.Ordinal)) writer.WriteString(k, v);
        writer.WriteEndObject();
    }
}

/// <summary>Phiên bản dạng <c>10.0.19045</c>, so sánh được (dùng cho <c>conditions.minOS</c> và phiên bản app).</summary>
public readonly record struct OSVersion(int Major, int Minor = 0, int Patch = 0) : IComparable<OSVersion>
{
    public static OSVersion? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Split('.');
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major)) return null;
        int Part(int i) => parts.Length > i && int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        return new OSVersion(major, Part(1), Part(2));
    }

    /// <summary>Phiên bản Windows hiện tại, ví dụ 10.0.22631.</summary>
    public static OSVersion Current
    {
        get
        {
            var v = Environment.OSVersion.Version;
            return new OSVersion(v.Major, v.Minor, v.Build);
        }
    }

    public int CompareTo(OSVersion other) => (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
    public static bool operator <(OSVersion l, OSVersion r) => l.CompareTo(r) < 0;
    public static bool operator >(OSVersion l, OSVersion r) => l.CompareTo(r) > 0;
    public static bool operator <=(OSVersion l, OSVersion r) => l.CompareTo(r) <= 0;
    public static bool operator >=(OSVersion l, OSVersion r) => l.CompareTo(r) >= 0;

    /// <summary>Mã hoá thành số nguyên (header rule bundle: phiên bản app tối thiểu).</summary>
    public uint Packed => (uint)(Major * 10_000 + Minor * 100 + Patch);
    public static OSVersion FromPacked(uint packed) => new((int)(packed / 10_000), (int)(packed / 100 % 100), (int)(packed % 100));

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

public static class AppVersion
{
    /// <summary>Phiên bản app (từ thuộc tính assembly của tiến trình chính).</summary>
    public static OSVersion Current
    {
        get
        {
            var asm = Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var raw = info?.Split('+')[0] ?? asm.GetName().Version?.ToString(3) ?? "1.0.0";
            return OSVersion.Parse(raw) ?? new OSVersion(1);
        }
    }

    public static string Display => Current.ToString();
}

/// <summary>Chế độ chạy thử (mục 15.4): chạy toàn bộ luồng nhưng không xoá gì.</summary>
public static class DryRun
{
    public static bool IsEnabledByEnvironment =>
        Environment.GetEnvironmentVariable("MASHCLEAN_DRY_RUN") == "1" || Environment.GetCommandLineArgs().Contains("--dry-run");
}

/// <summary>Danh sách "không bao giờ đề xuất mục này" (bảng <c>ignore_entry</c>).</summary>
public sealed record IgnoreList(IReadOnlySet<string> Paths, IReadOnlySet<string> Rules, IReadOnlySet<string> AppIDs)
{
    public static readonly IgnoreList Empty = new(new HashSet<string>(), new HashSet<string>(), new HashSet<string>());

    public bool IgnoresPath(string path)
    {
        if (Paths.Count == 0) return false;
        foreach (var p in Paths)
        {
            if (PathPolicy.IsSameOrInside(path, p)) return true;
        }
        return false;
    }

    public bool IgnoresRule(string? rule) => rule is not null && Rules.Contains(rule);
    public bool IgnoresApp(string? appID) => appID is not null && AppIDs.Contains(appID);
}

/// <summary>Hằng số định danh dùng chung giữa app, helper và tích hợp Explorer.</summary>
public static class Identifiers
{
    public const string AppID = "CleanBoost";
    public const string AppUserModelID = "CleanBoost.App";
    public const string UrlScheme = "cleanboost";
    public const string SingleInstanceMutex = @"Local\CleanBoost.SingleInstance";
    public const string ActivationPipe = "CleanBoost.Activation";
    public const string DidCleanEvent = @"Local\CleanBoost.DidClean";
    public const string HelperExeName = "CleanBoost.Helper.exe";
    public const string AppExeName = "CleanBoost.exe";
    /// <summary>Phiên bản giao thức app ↔ helper; tăng khi đổi lệnh.</summary>
    public const int HelperProtocolVersion = 2;
}

public static class FeatureIDs
{
    public const string SmartScan = "smartScan";
    public const string SystemJunk = "systemJunk";
    public const string Uninstaller = "uninstaller";
    public const string SpaceLens = "spaceLens";
    public const string Maintenance = "maintenance";
    public const string LoginItems = "loginItems";
    public const string LargeOldFiles = "largeOldFiles";
    public const string Duplicates = "duplicates";
}

public static class RemoverIDs
{
    public const string File = "file";
    public const string PrivilegedFile = "privilegedFile";
    public const string RecycleBin = "recycleBin";
    public const string StartupItem = "startupItem";
    public const string App = "app";
    public const string StoreApp = "storeApp";
    public const string RegistryKey = "registryKey";
    public const string Docker = "docker";
    public const string Maintenance = "maintenance";
}
