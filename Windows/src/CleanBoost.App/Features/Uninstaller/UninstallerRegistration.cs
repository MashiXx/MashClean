using CleanBoost.Features.Uninstaller;

namespace CleanBoost.App.Services;

/// <summary>Đăng ký Gỡ cài đặt (UninstallerFeature.registerRemovers của bản Mac): feature + remover <c>app</c> và <c>registryKey</c>.</summary>
public sealed partial class FeatureRegistry
{
    partial void RegisterUninstaller()
    {
        Add(new UninstallerFeature(Env.AppScanner));
        Add(new AppUninstallRemover());
        Add(new RegistryKeyRemover());
    }
}
