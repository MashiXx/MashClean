using System.Windows;
using CleanBoost.App.Features.Duplicates;

namespace CleanBoost.App.Shell;

public sealed partial class ScreenFactory
{
    partial void CreateDuplicates(ref FrameworkElement? view) => view = new DuplicatesView(Env);
}
