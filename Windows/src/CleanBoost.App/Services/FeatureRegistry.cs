using CleanBoost.Engine.Cleaning;
using CleanBoost.Engine.Scanning;

namespace CleanBoost.App.Services;

/// <summary>
/// Điểm đăng ký tính năng. Mỗi tính năng cài phương thức partial của mình trong thư mục riêng
/// (<c>Features\&lt;Tên&gt;\&lt;Tên&gt;Registration.cs</c>): thêm <see cref="IFeatureScanProvider"/> và remover chuyên biệt.
/// Thứ tự gọi giữ đúng thứ tự Smart Scan của bản Mac.
/// </summary>
public sealed partial class FeatureRegistry
{
    private readonly List<IFeatureScanProvider> _providers = [];
    private readonly List<IRemover> _removers = [];

    public AppEnvironment Env { get; }

    public FeatureRegistry(AppEnvironment env) => Env = env;

    public IReadOnlyList<IFeatureScanProvider> CreateProviders()
    {
        RegisterMaintenance();
        RegisterUninstaller();
        RegisterStartupItems();
        RegisterLargeOldFiles();
        RegisterDuplicates();
        RegisterSpaceLens();
        return _providers;
    }

    public IReadOnlyList<IRemover> CreateRemovers() => _removers;

    private void Add(IFeatureScanProvider provider) => _providers.Add(provider);
    private void Add(IRemover remover) => _removers.Add(remover);

    partial void RegisterMaintenance();
    partial void RegisterUninstaller();
    partial void RegisterStartupItems();
    partial void RegisterLargeOldFiles();
    partial void RegisterDuplicates();
    partial void RegisterSpaceLens();
}
