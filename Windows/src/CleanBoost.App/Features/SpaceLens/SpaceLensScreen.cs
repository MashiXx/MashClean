using System.Windows;
using CleanBoost.App.Features.SpaceLens;

namespace CleanBoost.App.Shell;

/// <summary>Màn Space Lens (bản đồ dung lượng).</summary>
public sealed partial class ScreenFactory
{
    partial void CreateSpaceLens(ref FrameworkElement? view) => view = new SpaceLensView(Env, Router);
}
