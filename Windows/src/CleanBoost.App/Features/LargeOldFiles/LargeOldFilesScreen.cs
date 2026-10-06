using System.Windows;
using CleanBoost.App.Features.LargeOldFiles;

namespace CleanBoost.App.Shell;

public sealed partial class ScreenFactory
{
    partial void CreateLargeOldFiles(ref FrameworkElement? view) => view = new LargeOldFilesView(Env);
}
