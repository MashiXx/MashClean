using System.Text.Json;
using System.Text.Json.Nodes;
using CleanBoost.Core;

namespace CleanBoost.Engine.Persistence;

/// <summary>
/// Cài đặt của app (tương đương App Group <c>UserDefaults</c> của bản Mac), lưu JSON tại
/// <c>%LOCALAPPDATA%\CleanBoost\settings.json</c>. Ghi nguyên tử; phát <see cref="Changed"/> sau mỗi thay đổi.
/// </summary>
public sealed class AppSettings
{
    private readonly string _path;
    private readonly object _lock = new();
    private JsonObject _values;

    public static AppSettings Shared { get; } = new(Path.Combine(KnownPaths.AppData, "settings.json"));

    /// <summary>Tên khoá cài đặt đã thay đổi.</summary>
    public event Action<string>? Changed;

    public AppSettings(string path)
    {
        _path = path;
        _values = ReadFile(path);
    }

    private static JsonObject ReadFile(string path)
    {
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject o) return o;
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Storage, "Không đọc được settings.json", ex);
        }
        return [];
    }

    /// <summary>Đọc lại từ file (khi tiến trình khác vừa ghi).</summary>
    public void Reload()
    {
        lock (_lock) _values = ReadFile(_path);
        Changed?.Invoke("*");
    }

    private T Get<T>(string key, T fallback)
    {
        lock (_lock)
        {
            if (!_values.TryGetPropertyValue(key, out var node) || node is null) return fallback;
            try { return node.Deserialize<T>() ?? fallback; } catch { return fallback; }
        }
    }

    private void Set<T>(string key, T value)
    {
        lock (_lock)
        {
            _values[key] = value is null ? null : JsonSerializer.SerializeToNode(value);
            Save();
        }
        Changed?.Invoke(key);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, _values.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Storage, "Không ghi được settings.json", ex);
        }
    }

    // MARK: Khoá (giữ tên như bản Mac)

    public string UpdateChannel { get => Get("updates.channel", "stable"); set => Set("updates.channel", value); }
    /// <summary>Biểu tượng khay hệ thống (tương đương thanh menu của Mac).</summary>
    public bool MenuBarEnabled { get => Get("menuBar.enabled", true); set => Set("menuBar.enabled", value); }
    /// <summary>Số liệu vẽ lên biểu tượng khay: <c>cpu</c>, <c>memory</c>, <c>network</c> (phân tách bằng dấu phẩy) hoặc rỗng.</summary>
    public string MenuBarStatusStyle { get => Get("menuBar.statusStyle", ""); set => Set("menuBar.statusStyle", value); }
    public bool AnalyticsEnabled { get => Get("analytics.enabled", false); set => Set("analytics.enabled", value); }
    public bool DryRunSetting { get => Get("debug.dryRun", false); set => Set("debug.dryRun", value); }
    /// <summary>Chạy thử: cài đặt, biến môi trường <c>MASHCLEAN_DRY_RUN=1</c> hoặc tham số <c>--dry-run</c>.</summary>
    public bool DryRun => DryRunSetting || Core.DryRun.IsEnabledByEnvironment;
    public bool ShowRiskyItems { get => Get("scan.showRisky", false); set => Set("scan.showRisky", value); }
    public bool LowDiskAlerts { get => Get("alerts.lowDisk", true); set => Set("alerts.lowDisk", value); }
    public int TrashAlertGB { get => Get("alerts.trashGB", 5); set => Set("alerts.trashGB", value); }
    public int LargeFileThresholdMB { get => Get("largeFiles.thresholdMB", 100); set => Set("largeFiles.thresholdMB", value); }
    public int OldFileDays { get => Get("largeFiles.oldDays", 365); set => Set("largeFiles.oldDays", value); }
    public bool OnboardingCompleted { get => Get("onboarding.completed", false); set => Set("onboarding.completed", value); }
    public AppLanguage Language
    {
        get => AppLanguageExtensions.ParseLanguage(Get<string?>("app.language", "system"));
        set => Set("app.language", value.Code());
    }
    /// <summary>Thêm "Phân tích bằng Clean Boost" vào menu chuột phải của File Explorer.</summary>
    public bool ExplorerIntegration { get => Get("explorer.contextMenu", true); set => Set("explorer.contextMenu", value); }
    /// <summary>Chạy Clean Boost (chỉ biểu tượng khay) khi đăng nhập Windows — tương đương login item menu bar của Mac.</summary>
    public bool StartAtLogin { get => Get("startup.atLogin", true); set => Set("startup.atLogin", value); }
    /// <summary>Đóng cửa sổ chính thì thu xuống khay thay vì thoát (khi bật biểu tượng khay).</summary>
    public bool CloseToTray { get => Get("tray.closeToTray", true); set => Set("tray.closeToTray", value); }

    public DateTimeOffset? RulesLastCheck { get => Get<DateTimeOffset?>("rules.lastCheck", null); set => Set("rules.lastCheck", value); }
    public string? RulesManifestETag { get => Get<string?>("rules.manifestETag", null); set => Set("rules.manifestETag", value); }

    public DateTimeOffset? LastAlert(string kind) => Get<DateTimeOffset?>("alerts.last." + kind, null);
    public void MarkAlerted(string kind) => Set("alerts.last." + kind, DateTimeOffset.Now);

    /// <summary>Mỗi loại cảnh báo tối đa 1 lần mỗi 24 giờ.</summary>
    public bool ShouldAlert(string kind) => LastAlert(kind) is not { } last || DateTimeOffset.Now - last >= TimeSpan.FromHours(24);
}
