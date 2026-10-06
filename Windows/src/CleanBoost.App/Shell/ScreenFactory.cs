using System.Windows;
using CleanBoost.App.Features.SmartScan;
using CleanBoost.App.Features.SystemJunk;
using CleanBoost.App.Services;
using CleanBoost.App.UI;

namespace CleanBoost.App.Shell;

/// <summary>Màn hình có màu chủ đạo riêng: cửa sổ chính vẽ gradient tương ứng phía sau.</summary>
public interface IAccentScreen
{
    Accent Accent { get; }
}

/// <summary>Màn hình hỗ trợ tự bắt đầu quét khi mở qua liên kết sâu hoặc phím tắt.</summary>
public interface IAutoStartScreen
{
    void AutoStart();
}

/// <summary>Màn hình đang quét/dọn (dùng khi chụp ảnh tự động và khi thoát).</summary>
public interface IBusyScreen
{
    bool IsBusy { get; }
}

/// <summary>
/// Tạo màn hình cho từng mục thanh bên. Mỗi tính năng cài phương thức partial của mình trong
/// <c>Features\&lt;Tên&gt;\&lt;Tên&gt;Screen.cs</c>; chưa cài thì hiện màn "đang phát triển".
/// </summary>
public sealed partial class ScreenFactory(AppEnvironment env, AppRouter router)
{
    public AppEnvironment Env { get; } = env;
    public AppRouter Router { get; } = router;

    public FrameworkElement Create(SidebarItem item)
    {
        FrameworkElement? view = null;
        switch (item)
        {
            case SidebarItem.SmartScan:
                view = new SmartScanView(Env, Router);
                break;
            case SidebarItem.SystemJunk:
                view = new SystemJunkView(Env);
                break;
            case SidebarItem.LargeOldFiles:
                CreateLargeOldFiles(ref view);
                break;
            case SidebarItem.Duplicates:
                CreateDuplicates(ref view);
                break;
            case SidebarItem.Uninstaller:
                CreateUninstaller(ref view);
                break;
            case SidebarItem.LoginItems:
                CreateStartupItems(ref view);
                break;
            case SidebarItem.Maintenance:
                CreateMaintenance(ref view);
                break;
            case SidebarItem.SpaceLens:
                CreateSpaceLens(ref view);
                break;
            case SidebarItem.History:
                CreateHistory(ref view);
                break;
            case SidebarItem.Diagnostics:
                CreateDiagnostics(ref view);
                break;
        }
        return view ?? new PlaceholderScreen(item);
    }

    partial void CreateLargeOldFiles(ref FrameworkElement? view);
    partial void CreateDuplicates(ref FrameworkElement? view);
    partial void CreateUninstaller(ref FrameworkElement? view);
    partial void CreateStartupItems(ref FrameworkElement? view);
    partial void CreateMaintenance(ref FrameworkElement? view);
    partial void CreateSpaceLens(ref FrameworkElement? view);
    partial void CreateHistory(ref FrameworkElement? view);
    partial void CreateDiagnostics(ref FrameworkElement? view);
}

/// <summary>Màn tạm cho tính năng chưa có giao diện.</summary>
public sealed class PlaceholderScreen : System.Windows.Controls.Grid, IAccentScreen
{
    public Accent Accent => Accent.Neutral;

    public PlaceholderScreen(SidebarItem item)
    {
        var stack = new System.Windows.Controls.StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new Icon { Symbol = item.Symbol(), Size = 64, Foreground = System.Windows.Media.Brushes.White });
        stack.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = item.Title(), FontSize = 26, FontWeight = FontWeights.Bold, Foreground = System.Windows.Media.Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0),
        });
        Children.Add(stack);
    }
}
