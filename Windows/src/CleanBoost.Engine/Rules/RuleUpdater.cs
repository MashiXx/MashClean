using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CleanBoost.Core;
using CleanBoost.Engine.Persistence;

namespace CleanBoost.Engine.Rules;

/// <summary>Manifest trên CDN (mục 14.2).</summary>
public sealed record RuleManifest
{
    public ulong Latest { get; init; }
    public string MinApp { get; init; } = "1.0.0";
    public string Url { get; init; } = "";
    public string Sha256 { get; init; } = "";
    /// <summary>Kill switch: tắt ngay một rule lỗi.</summary>
    public List<string>? DisabledRules { get; init; }
}

/// <summary>Kiểm tra rule mới mỗi 24 giờ (và khi khởi động nếu lần trước đã quá 24 giờ).</summary>
public sealed class RuleUpdater
{
    public enum OutcomeKind { SkippedRecentlyChecked, NotConfigured, UpToDate, Installed, AppTooOld, Failed }

    public sealed record Outcome(OutcomeKind Kind, ulong Version = 0, string? Message = null)
    {
        public string Description => Kind switch
        {
            OutcomeKind.Installed => Loc.F("Đã cập nhật rule lên %llu", Version),
            OutcomeKind.UpToDate or OutcomeKind.SkippedRecentlyChecked => Loc.T("Rule đang là bản mới nhất"),
            OutcomeKind.NotConfigured => Loc.T("Chưa cấu hình máy chủ rule"),
            OutcomeKind.AppTooOld => Loc.F("Bộ rule mới cần app %@", Message ?? ""),
            _ => Loc.F("Lỗi cập nhật rule: %@", Message ?? ""),
        };
    }

    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);


    private readonly RuleStore _store;
    private readonly Uri? _manifestUrl;
    private readonly AppSettings _settings;
    private readonly HttpClient _http;

    public RuleUpdater(RuleStore store, Uri? manifestUrl, AppSettings settings, HttpClient? http = null)
    {
        _store = store;
        _manifestUrl = manifestUrl;
        _settings = settings;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>URL manifest theo kênh (<c>stable</c>/<c>beta</c>): mẫu lấy từ cấu hình build, thay <c>{channel}</c>.</summary>
    public static Uri? ManifestUrl(string channel) =>
        AppConfig.RulesManifestTemplate is { Length: > 0 } t && Uri.TryCreate(t.Replace("{channel}", channel, StringComparison.Ordinal), UriKind.Absolute, out var u) ? u : null;

    public async Task<Outcome> CheckIfNeededAsync(bool force = false, CancellationToken ct = default)
    {
        if (_manifestUrl is null) return new Outcome(OutcomeKind.NotConfigured);
        if (!force && _settings.RulesLastCheck is { } last && DateTimeOffset.Now - last < CheckInterval) return new Outcome(OutcomeKind.SkippedRecentlyChecked);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _manifestUrl);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            if (_settings.RulesManifestETag is { Length: > 0 } etag && EntityTagHeaderValue.TryParse(etag, out var tag)) request.Headers.IfNoneMatch.Add(tag);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            _settings.RulesLastCheck = DateTimeOffset.Now;
            if (response.StatusCode == HttpStatusCode.NotModified) return new Outcome(OutcomeKind.UpToDate);
            if (response.StatusCode != HttpStatusCode.OK) return new Outcome(OutcomeKind.Failed, Message: "HTTP " + (int)response.StatusCode);
            if (response.Headers.ETag is { } newTag) _settings.RulesManifestETag = newTag.ToString();
            var manifest = JsonSerializer.Deserialize<RuleManifest>(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false), RuleJson.Options)
                ?? throw new JsonException("manifest null");
            _store.ApplyDisabledRules(manifest.DisabledRules ?? []);

            if (manifest.Latest <= _store.Snapshot.Version) return new Outcome(OutcomeKind.UpToDate);
            if (OSVersion.Parse(manifest.MinApp) is { } required && required > AppVersion.Current) return new Outcome(OutcomeKind.AppTooOld, Message: manifest.MinApp);
            var bundleUrl = new Uri(_manifestUrl, manifest.Url);
            var data = await _http.GetByteArrayAsync(bundleUrl, ct).ConfigureAwait(false);
            var snap = _store.Install(data, manifest.Sha256, manifest.DisabledRules);
            return new Outcome(OutcomeKind.Installed, snap.Version);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Chữ ký sai hoặc lỗi mạng: giữ bộ cũ, ghi log.
            Log.Error(LogCategory.Rules, "Cập nhật rule thất bại", ex);
            return new Outcome(OutcomeKind.Failed, Message: ex.Message);
        }
    }
}
