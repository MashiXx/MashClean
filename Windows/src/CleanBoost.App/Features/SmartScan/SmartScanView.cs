using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CleanBoost.App.Services;
using CleanBoost.App.Shared;
using CleanBoost.App.Shell;
using CleanBoost.App.UI;
using CleanBoost.Core;
using CleanBoost.Engine.Nodes;
using CleanBoost.Engine.Scanning;
using CleanBoost.Features.SmartScan;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CleanBoost.App.Features.SmartScan;

/// <summary>View model Smart Scan: một Clean Plan cho cả ba thẻ, mặc định chọn mọi mục an toàn.</summary>
public sealed partial class SmartScanViewModel(AppEnvironment env) : ScanCleanViewModel(env.Services, FeatureIDs.SmartScan, env.SmartScan.Tasks)
{
    [ObservableProperty] private IReadOnlyList<SmartScanCardSummary> _cards = [];
    [ObservableProperty] private bool _showDetails;

    protected override void ScanDidFinish(ScanResult result)
    {
        Cards = env.SmartScan.Summarize(result);
        ShowDetails = false;
    }
}

/// <summary>
/// Màn Smart Scan (SmartScanView của bản Mac): sau khi quét hiện màn tổng hợp với ba thẻ Dọn dẹp / Bảo trì / Ứng dụng
/// và nút tròn "Chạy"; "Xem lại chi tiết" mở cây kết quả chung.
/// </summary>
public sealed class SmartScanView : Grid, IAccentScreen, IAutoStartScreen, IBusyScreen
{
    private readonly SmartScanViewModel _vm;
    private readonly AppRouter _router;
    private readonly FeatureFlowView _flow;
    private readonly Grid _summary;
    private readonly System.Windows.Controls.Primitives.UniformGrid _cards = new() { Rows = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _selectedText = new() { FontSize = 13, Foreground = new SolidColorBrush(Color.FromArgb(0xB8, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _runButton;
    private readonly TextBlock _runSubtitle = new() { FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Center };

    public Accent Accent => Accent.SmartScan;
    public bool IsBusy => _vm.IsBusy;

    public SmartScanView(AppEnvironment env, AppRouter router)
    {
        _router = router;
        _vm = new SmartScanViewModel(env);
        var back = new Button { Style = (Style)Application.Current.FindResource("GlassButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
        var backContent = new StackPanel { Orientation = Orientation.Horizontal };
        backContent.Children.Add(new Icon { Symbol = "chevron.left", Size = 11, Foreground = Brushes.White, Margin = new Thickness(0, 0, 6, 0) });
        backContent.Children.Add(new TextBlock { Text = Loc.T("Tổng hợp") });
        back.Content = backContent;
        back.Click += (_, _) => _vm.ShowDetails = false;
        _flow = new FeatureFlowView
        {
            Appearance = new FeatureAppearance(Accent.SmartScan, "sparkles", "Smart Scan",
                Loc.T("Một lần quét cho cả máy: dọn rác an toàn, gợi ý bảo trì, tìm file sót của ứng dụng."), CleanTitle: Loc.T("Chạy")),
            ResultsTop = back,
            DataContext = _vm,
        };
        Children.Add(_flow);

        // Màn tổng hợp
        _summary = new Grid { Margin = new Thickness(24), Visibility = Visibility.Collapsed };
        _summary.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _summary.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _summary.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _summary.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _summary.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _summary.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _summary.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        var header = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        header.Children.Add(new TextBlock { Text = Loc.T("Quét xong"), Style = (Style)Application.Current.FindResource("HeroText"), HorizontalAlignment = HorizontalAlignment.Center });
        header.Children.Add(new TextBlock
        {
            Text = Loc.T("Bấm \"Chạy\" để thực hiện mọi mục an toàn. Mục cần xem lại không được chọn sẵn."),
            Style = (Style)Application.Current.FindResource("SecondaryBodyText"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0),
        });
        SetRow(header, 1);
        _summary.Children.Add(header);
        SetRow(_cards, 2);
        _cards.Margin = new Thickness(0, 28, 0, 0);
        _summary.Children.Add(_cards);
        var selectionRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24, 0, 0) };
        selectionRow.Children.Add(_selectedText);
        var details = new Button { Content = Loc.T("Xem lại chi tiết"), Style = (Style)Application.Current.FindResource("GlassButton"), Margin = new Thickness(14, 0, 0, 0) };
        details.Click += (_, _) => _vm.ShowDetails = true;
        selectionRow.Children.Add(details);
        SetRow(selectionRow, 3);
        _summary.Children.Add(selectionRow);
        var runContent = new StackPanel();
        runContent.Children.Add(new TextBlock { Text = Loc.T("Chạy"), FontFamily = AppFonts.Display, FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
        runContent.Children.Add(_runSubtitle);
        _runButton = new Button { Style = (Style)Application.Current.FindResource("BigActionButton"), Content = runContent, IsDefault = true };
        _runButton.Click += async (_, _) =>
        {
            if (_vm.PrepareCleanCommand.CanExecute(null)) await _vm.PrepareCleanAsync();
        };
        SetRow(_runButton, 5);
        _summary.Children.Add(_runButton);
        Children.Add(_summary);
        Theme.Apply(this, Accent.SmartScan);

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SmartScanViewModel.Phase) or nameof(SmartScanViewModel.ShowDetails) or nameof(SmartScanViewModel.Cards)) Update();
            if (e.PropertyName is nameof(ScanCleanViewModel.SelectedBytes) or nameof(ScanCleanViewModel.SelectedCount)) UpdateSelection();
        };
        Update();
    }

    public void AutoStart()
    {
        if (!_vm.IsBusy) _ = _vm.StartScanAsync();
    }

    private void Update()
    {
        var summary = _vm.Phase == Phase.Results && !_vm.ShowDetails;
        if (summary && _summary.Visibility != Visibility.Visible)
        {
            _summary.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
        }
        _summary.Visibility = summary ? Visibility.Visible : Visibility.Collapsed;
        _flow.Visibility = summary ? Visibility.Collapsed : Visibility.Visible;
        BuildCards();
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        _selectedText.Text = Loc.F("Đã chọn %@ · %lld mục", _vm.SelectedBytes.Formatted, _vm.SelectedCount);
        _runSubtitle.Text = _vm.SelectedBytes.Bytes > 0 ? _vm.SelectedBytes.Formatted : "";
        _runSubtitle.Visibility = _vm.SelectedBytes.Bytes > 0 ? Visibility.Visible : Visibility.Collapsed;
        _runButton.IsEnabled = _vm.SelectedCount > 0;
    }

    private void BuildCards()
    {
        _cards.Children.Clear();
        var cards = _vm.Cards;
        // Thẻ chia đều chiều ngang, tối đa ~900 px như bản Mac (HStack maxWidth 860).
        _cards.Columns = Math.Max(1, cards.Count);
        _cards.MaxWidth = cards.Count switch { <= 1 => 460.0, 2 => 760.0, _ => 940.0 };
        foreach (var card in cards)
        {
            var stack = new StackPanel();
            var top = new DockPanel();
            var view = new Button { Content = Loc.T("Xem"), Style = (Style)Application.Current.FindResource("GlassButton"), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(view, Dock.Right);
            view.Click += (_, _) =>
            {
                if (card.PrimaryFeatureID is { } id && SidebarItems.FromFeature(id) is { } item) _router.Open(item);
            };
            top.Children.Add(view);
            top.Children.Add(new Icon { Symbol = card.Icon, Size = 38, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left });
            stack.Children.Add(top);
            stack.Children.Add(new TextBlock { Text = card.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromArgb(0xD9, 255, 255, 255)), Margin = new Thickness(0, 14, 0, 0) });
            // Tiêu đề dài (vd "2 tasks recommended") thu nhỏ cho vừa thẻ thay vì bị cắt.
            stack.Children.Add(new Viewbox
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 6, 0, 8),
                Child = new TextBlock { Text = card.Headline, FontFamily = AppFonts.Display, FontSize = 26, FontWeight = FontWeights.Bold, Foreground = Brushes.White },
            });
            foreach (var f in card.Features)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = f.Subtitle, FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(0xB8, 255, 255, 255)), TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 34, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 4),
                });
            }
            _cards.Children.Add(new Border
            {
                Style = (Style)Application.Current.FindResource("Card"), MinHeight = 170, Margin = new Thickness(8, 0, 8, 0), Padding = new Thickness(20), Child = stack,
            });
        }
    }
}
