using System.Windows;
using CleanBoost.App.Features.Uninstaller;

namespace CleanBoost.App.Shell;

/// <summary>Màn Gỡ cài đặt trên thanh bên.</summary>
public sealed partial class ScreenFactory
{
    partial void CreateUninstaller(ref FrameworkElement? view)
    {
        view = new UninstallerView(Env, Router);
    }
}
