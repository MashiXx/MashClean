using System.Windows;
using CleanBoost.App.Features.Maintenance;

namespace CleanBoost.App.Shell;

public sealed partial class ScreenFactory
{
    partial void CreateMaintenance(ref FrameworkElement? view) => view = new MaintenanceView(Env);
}
