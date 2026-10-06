using System.Windows;
using CleanBoost.App.Features.StartupItems;

namespace CleanBoost.App.Shell;

/// <summary>Màn Mục khởi động (Login Items của bản Mac).</summary>
public sealed partial class ScreenFactory
{
    partial void CreateStartupItems(ref FrameworkElement? view) => view = new StartupItemsView(Env);
}
