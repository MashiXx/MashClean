using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CleanBoost.App.Services;
using CleanBoost.App.Shared;
using CleanBoost.App.Shell;
using CleanBoost.App.UI;
using CleanBoost.Core;
using CleanBoost.Engine.Rules;
using CleanBoost.Features.SystemJunk;

namespace CleanBoost.App.Features.SystemJunk;

/// <summary>Màn Rác hệ thống (SystemJunkView của bản Mac): luồng chung + các nhóm sẽ quét ở màn chờ.</summary>
public sealed class SystemJunkView : Grid, IAccentScreen, IAutoStartScreen, IBusyScreen
{
    public Accent Accent => Accent.Cleanup;
    public ScanCleanViewModel ViewModel { get; }
    public bool IsBusy => ViewModel.IsBusy;

    public SystemJunkView(AppEnvironment env)
    {
        var feature = env.Feature<SystemJunkFeature>();
        ViewModel = new ScanCleanViewModel(env.Services, FeatureIDs.SystemJunk, feature.ScanTasks);
        var flow = new FeatureFlowView
        {
            Appearance = new FeatureAppearance(Accent.Cleanup, "trash.circle", Loc.T("Rác hệ thống"),
                Loc.T("Dọn tệp tạm, cache, log, bản cập nhật Windows đã tải và cache công cụ lập trình. Mọi mục đều giải thích được: thuộc app nào, vì sao an toàn.")),
            IdleAccessory = CategoryChips(),
            DataContext = ViewModel,
        };
        Children.Add(flow);
    }

    public void AutoStart()
    {
        if (ViewModel.Phase == Phase.Idle) _ = ViewModel.StartScanAsync();
    }

    /// <summary>Tên các nhóm sẽ quét dưới dạng viên nang, 5 cái mỗi hàng.</summary>
    private static FrameworkElement CategoryChips()
    {
        var wrap = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 640 };
        foreach (var spec in SystemJunkFeature.Specs)
        {
            var pill = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255)),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(4),
                Child = new TextBlock { Text = RuleCategory.Title(spec.Category).Resolved, FontSize = 12, Foreground = Brushes.White },
            };
            wrap.Children.Add(pill);
        }
        return wrap;
    }
}
