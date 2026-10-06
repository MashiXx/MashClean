using System.Reflection;

namespace CleanBoost.Core;

/// <summary>
/// Cấu hình lúc build (tương đương các khoá <c>MashClean*</c> trong Info.plist của bản Mac), đọc từ
/// <c>AssemblyMetadata</c> của exe chính. Đặt qua thuộc tính MSBuild <c>CleanBoostRulesManifestUrl</c>,
/// <c>CleanBoostSupportEmail</c>, <c>CleanBoostAnalyticsUrl</c>, <c>CleanBoostUpdateFeedUrl</c>; để trống thì tính năng tương ứng tắt.
/// </summary>
public static class AppConfig
{
    private static string? Metadata(string key)
    {
        var asm = Assembly.GetEntryAssembly();
        var value = asm?.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static string? RulesManifestTemplate => Environment.GetEnvironmentVariable("CLEANBOOST_RULES_MANIFEST_URL") is { Length: > 0 } e ? e : Metadata("RulesManifestUrl");
    public static string? SupportEmail => Metadata("SupportEmail");
    public static string? AnalyticsUrl => Metadata("AnalyticsUrl");
    /// <summary>URL JSON mô tả bản mới nhất (thay cho appcast Sparkle).</summary>
    public static string? UpdateFeedUrl => Metadata("UpdateFeedUrl");
    public static string DownloadPage => Metadata("DownloadPage") ?? "https://github.com/MashiXx/MashClean/releases/latest";
}
