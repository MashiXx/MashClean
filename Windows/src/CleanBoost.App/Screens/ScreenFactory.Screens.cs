using System.Windows;
using CleanBoost.App.Screens;

namespace CleanBoost.App.Shell;

/// <summary>Màn Lịch sử và Báo cáo lỗi (thư mục <c>Screens\</c>).</summary>
public sealed partial class ScreenFactory
{
    partial void CreateHistory(ref FrameworkElement? view) => view = new HistoryScreen(Env, ScreensSnapshot.DemoStorage());

    partial void CreateDiagnostics(ref FrameworkElement? view) => view = new DiagnosticsScreen(Env);
}
