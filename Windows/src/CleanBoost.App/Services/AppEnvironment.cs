using CleanBoost.Core;
using CleanBoost.Engine.Apps;
using CleanBoost.Engine.Cleaning;
using CleanBoost.Engine.FileSystem;
using CleanBoost.Engine.Ipc;
using CleanBoost.Engine.Persistence;
using CleanBoost.Engine.Rules;
using CleanBoost.Engine.Scanning;
using CleanBoost.Features.SmartScan;
using CleanBoost.Features.SystemJunk;

namespace CleanBoost.App.Services;

/// <summary>
/// Composition root (SYSTEM_DESIGN mục 4.3): container phụ thuộc tự viết, không cần thư viện DI.
/// Tạo một lần lúc khởi động; mọi màn hình nhận các dịch vụ từ đây.
/// </summary>
public sealed class AppEnvironment
{
    public AppSettings Settings { get; }
    public Storage? Storage { get; }
    public RuleStore Rules { get; }
    public FileSystemService FileSystem { get; }
    public HelperClient Helper { get; }
    public ScanEngine ScanEngine { get; }
    public CleanEngine CleanEngine { get; }
    public AppScanner AppScanner { get; }
    public ScanServices Services { get; }
    public RuleUpdater RuleUpdater { get; }
    public IReadOnlyList<IFeatureScanProvider> Features { get; }
    public SmartScanFeature SmartScan { get; }

    /// <summary>Các tính năng đăng ký thêm (do từng màn hình cung cấp) — xem <see cref="FeatureRegistry"/>.</summary>
    public FeatureRegistry Registry { get; }

    public AppEnvironment()
    {
        Settings = AppSettings.Shared;
        try
        {
            Storage = new Storage(Storage.DefaultPath);
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Storage, "Không mở được database", ex);
            Storage = null;
        }
        Rules = new RuleStore(RuleStore.DefaultBundledPath, RuleStore.DefaultCacheDirectory);
        FileSystem = new FileSystemService();
        Helper = new HelperClient();
        ScanEngine = new ScanEngine(FileSystem);
        CleanEngine = new CleanEngine(FileSystem, Helper, Storage);
        AppScanner = new AppScanner(FileSystem, Storage, () => Rules.Snapshot.Knowledge);
        Services = new ScanServices(this);
        RuleUpdater = new RuleUpdater(Rules, RuleUpdater.ManifestUrl(Settings.UpdateChannel), Settings);

        Registry = new FeatureRegistry(this);
        var systemJunk = new SystemJunkFeature(AppScanner, Rules);
        var providers = new List<IFeatureScanProvider> { systemJunk };
        providers.AddRange(Registry.CreateProviders());
        Features = providers;
        foreach (var remover in Registry.CreateRemovers()) CleanEngine.Register(remover);
        SmartScan = new SmartScanFeature(Features);
    }

    public T Feature<T>() where T : class, IFeatureScanProvider => Features.OfType<T>().First();
    public T? FeatureOrNull<T>() where T : class, IFeatureScanProvider => Features.OfType<T>().FirstOrDefault();
}
