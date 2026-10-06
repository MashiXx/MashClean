using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CleanBoost.App.Services;
using CleanBoost.App.Shell;
using CleanBoost.App.UI;
using CleanBoost.Core;
using CleanBoost.Core.Native;
using CleanBoost.Engine.Cleaning;
using CleanBoost.Engine.Persistence;

namespace CleanBoost.App.Screens;

/// <summary>Một lần dọn trong danh sách bên trái.</summary>
public sealed class HistoryOperationRow(CleanOperation op)
{
    public CleanOperation Operation { get; } = op;

    public string DateText => FormatDate(Operation.StartedAt);

    public string DetailText => Loc.F("%@ · %lld thành công%@", ByteCount.Format(Operation.FreedBytes ?? 0), Operation.ItemsOk,
        Operation.ItemsFailed > 0 ? Loc.F(" · %lld lỗi", Operation.ItemsFailed) : "");

    public Brush DetailBrush => Operation.ItemsFailed > 0 ? Theme.Frozen(Color.FromArgb(0xD9, 0xFF, 0xCC, 0x00)) : Kit.Secondary;

    /// <summary>Ngày viết tắt + giờ ngắn (<c>.abbreviated</c> / <c>.shortened</c> của bản Mac).</summary>
    public static string FormatDate(DateTimeOffset d) => Loc.IsEnglish
        ? d.ToString("MMM d, yyyy · h:mm tt", CultureInfo.GetCultureInfo("en-US"))
        : d.ToString("d 'thg' M, yyyy · HH:mm", CultureInfo.GetCultureInfo("vi-VN"));
}

/// <summary>Một mục trong bảng bên phải.</summary>
public sealed class HistoryItemRow(CleanItemLog log)
{
    public CleanItemLog Log { get; } = log;
    public string FullPath => Log.Path;
    public string DisplayPath => Log.Path.AbbreviatingHome();
    public string SizeText => ByteCount.Format(Log.Size);
    public bool CanRestore => Log.IsRestorable;

    public string StrategyText => Log.Strategy switch
    {
        "trash" => Loc.T("Thùng rác"),
        "delete" => Loc.T("Xoá vĩnh viễn"),
        "deleteContents" => Loc.T("Xoá nội dung"),
        var s => CustomTitle(s.Replace("custom:", "", StringComparison.Ordinal)),
    };

    /// <summary>Tên dễ đọc cho remover chuyên biệt đã biết; không biết thì giữ mã như bản Mac.</summary>
    private static string CustomTitle(string removerId) => removerId switch
    {
        RemoverIDs.RecycleBin => Loc.T("Đổ Thùng rác"),
        RemoverIDs.App or RemoverIDs.StoreApp => Loc.T("Gỡ cài đặt"),
        RemoverIDs.StartupItem => Loc.T("Mục khởi động"),
        RemoverIDs.RegistryKey => Loc.T("Khoá registry"),
        RemoverIDs.Maintenance => Loc.T("Bảo trì"),
        RemoverIDs.Docker => "Docker",
        RemoverIDs.PrivilegedFile => Loc.T("Xoá vĩnh viễn"),
        _ => removerId,
    };

    public string ResultText
    {
        get
        {
            var r = Log.Result;
            if (r == "ok") return Loc.T("Thành công");
            if (r == "restored") return Loc.T("Đã khôi phục");
            if (r.StartsWith("skipped:dryRun", StringComparison.Ordinal)) return Loc.T("Chạy thử");
            if (r.StartsWith("skipped", StringComparison.Ordinal)) return Loc.T("Đã bỏ qua");
            return Loc.F("Lỗi (%@)", r.Replace("failed:", "", StringComparison.Ordinal));
        }
    }

    public Brush ResultBrush => Log.Result switch
    {
        "ok" => Kit.Secondary,
        "restored" => Theme.SafeBrush,
        var r when r.StartsWith("skipped", StringComparison.Ordinal) => Kit.Tertiary,
        _ => Theme.RiskyBrush,
    };

    /// <summary>Lỗi: mô tả mã lỗi Windows; bỏ qua: lý do.</summary>
    public string? ResultTip
    {
        get
        {
            var r = Log.Result;
            if (r.StartsWith("failed:", StringComparison.Ordinal) && int.TryParse(r.AsSpan(7), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
                return Win32Error.Describe(code);
            if (r.StartsWith("skipped:", StringComparison.Ordinal))
            {
                return r[8..] switch
                {
                    "inUse" => Loc.T("Đang được sử dụng"),
                    "notFound" => Loc.T("Không còn tồn tại"),
                    "blockedByPolicy" => Loc.T("Bị chặn vì lý do an toàn"),
                    "protectedBySystem" => Loc.T("Windows bảo vệ mục này"),
                    "userDeclined" => Loc.T("Bạn đã không đồng ý xoá"),
                    "dryRun" => Loc.T("Chế độ thử: không xoá"),
                    "appRunning" => Loc.T("Ứng dụng đang chạy"),
                    _ => Loc.T("Không hỗ trợ"),
                };
            }
            return null;
        }
    }

    public bool HasResultTip => ResultTip is not null;
}

/// <summary>
/// Màn "Lịch sử" (HistoryView của bản Mac): tổng đã giải phóng, biểu đồ 30 lần dọn gần nhất theo ngày, danh sách lần dọn
/// và bảng mục đã xử lý; mục đã chuyển vào Thùng rác có nút "Khôi phục".
/// </summary>
public sealed class HistoryScreen : Grid, IAccentScreen
{
    // Độ rộng cột của bảng mục (khớp HistoryItemTemplate trong ScreenStyles.xaml).
    private static readonly double[] ItemColumns = [108, 84, 108, 88];

    private readonly AppEnvironment _env;
    private readonly Storage? _storage;
    private readonly TextBlock _subtitle = new() { Style = Kit.S("SecondaryBodyText"), Margin = new Thickness(0, 4, 0, 0) };
    private readonly Border _chartBox;
    private readonly HistoryChart _chart = new() { Height = 110 };
    private readonly Grid _body = new() { Margin = new Thickness(0, 14, 0, 0) };
    private readonly ListBox _operations;
    private readonly ListBox _items;
    private readonly FrameworkElement _itemsPanel;
    private readonly TextBlock _placeholder;
    private readonly StackPanel _empty;
    private readonly TextBlock _emptyText = new() { FontSize = 13, Foreground = Kit.Secondary, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _message = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 10, 0, 0), Visibility = Visibility.Collapsed };
    private string? _selectedId;
    private int _loadToken;

    public Accent Accent => Accent.Neutral;

    public HistoryScreen(AppEnvironment env, Storage? storageOverride = null)
    {
        _env = env;
        _storage = storageOverride ?? env.Storage;
        Resources.MergedDictionaries.Add(new ScreenStyles());
        Margin = new Thickness(24);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Tiêu đề
        var header = new DockPanel();
        var refresh = Kit.Glass(Loc.T("Làm mới"), "arrow.clockwise");
        refresh.VerticalAlignment = VerticalAlignment.Top;
        refresh.Margin = new Thickness(16, 6, 0, 0);
        refresh.Click += (_, _) => Reload();
        DockPanel.SetDock(refresh, Dock.Right);
        header.Children.Add(refresh);
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = Loc.T("Lịch sử dọn dẹp"), Style = Kit.S("TitleText") });
        titles.Children.Add(_subtitle);
        header.Children.Add(titles);
        Children.Add(header);

        // Biểu đồ
        _chartBox = new Border { Style = Kit.S("Box"), Padding = new Thickness(12, 10, 10, 8), Margin = new Thickness(0, 14, 0, 0), Child = _chart, Visibility = Visibility.Collapsed };
        SetRow(_chartBox, 1);
        Children.Add(_chartBox);

        // Thân: danh sách lần dọn + bảng mục
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _operations = new ListBox
        {
            Style = Kit.S("PlainListBox"), ItemTemplate = Kit.Template("HistoryOperationTemplate"), Margin = new Thickness(6),
        };
        _operations.SelectionChanged += (_, _) =>
        {
            if (_operations.SelectedItem is HistoryOperationRow row && row.Operation.Id != _selectedId) Select(row.Operation.Id);
        };
        _body.Children.Add(new Border { Style = Kit.S("Box"), Padding = new Thickness(0), Child = _operations });

        _items = new ListBox
        {
            Style = Kit.S("PlainListBox"), ItemTemplate = Kit.Template("HistoryItemTemplate"), Margin = new Thickness(6, 0, 6, 6),
        };
        VirtualizingPanel.SetScrollUnit(_items, ScrollUnit.Pixel);
        _items.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnItemButton));
        var table = new DockPanel();
        var tableHeader = ItemHeader();
        // Tiêu đề cột thẳng hàng với nội dung: chừa chỗ cho thanh cuộn khi danh sách có thanh cuộn.
        _items.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (e.OriginalSource is not ScrollViewer sv) return;
            var right = 14 + (sv.ComputedVerticalScrollBarVisibility == Visibility.Visible ? 8 : 0);
            if (tableHeader.Margin.Right != right) tableHeader.Margin = new Thickness(16, 6, right, 0);
        }));
        DockPanel.SetDock(tableHeader, Dock.Top);
        table.Children.Add(tableHeader);
        var headerLine = Kit.HLine(0x1F);
        headerLine.Margin = new Thickness(12, 0, 12, 4);
        DockPanel.SetDock(headerLine, Dock.Top);
        table.Children.Add(headerLine);
        table.Children.Add(_items);
        _itemsPanel = table;
        _placeholder = new TextBlock
        {
            Text = Loc.T("Chọn một lần dọn để xem chi tiết"), FontSize = 13, Foreground = Kit.Secondary,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
        };
        var detail = new Grid();
        detail.Children.Add(_itemsPanel);
        detail.Children.Add(_placeholder);
        var detailBox = new Border { Style = Kit.S("Box"), Padding = new Thickness(0), Child = detail };
        Grid.SetColumn(detailBox, 2);
        _body.Children.Add(detailBox);
        SetRow(_body, 2);
        Children.Add(_body);

        // Chưa có lần dọn nào
        _empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        _empty.Children.Add(new Icon { Symbol = "clock.arrow.circlepath", Size = 54, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.9 });
        _empty.Children.Add(new TextBlock { Text = Loc.T("Chưa có lần dọn nào"), Style = Kit.S("HeadlineText"), FontSize = 17, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) });
        _empty.Children.Add(_emptyText);
        SetRow(_empty, 2);
        Children.Add(_empty);

        SetRow(_message, 3);
        Children.Add(_message);

        // Tải lại khi màn hiện ra (onAppear) và sau mỗi lần dọn.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Reload();
        };
        CleanEngine.DidClean += OnDidClean;
        Unloaded += (_, _) => CleanEngine.DidClean -= OnDidClean;
        Loaded += (_, _) =>
        {
            CleanEngine.DidClean -= OnDidClean;
            CleanEngine.DidClean += OnDidClean;
        };
    }

    // Màn đang ẩn thì lần hiện sau tự tải lại.
    private void OnDidClean() => Dispatcher.InvokeAsync(() =>
    {
        if (IsVisible) Reload();
    });

    private static Grid ItemHeader()
    {
        var g = new Grid { Height = 30, Margin = new Thickness(16, 6, 14, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var w in ItemColumns) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        string[] titles = [Loc.T("Mục"), Loc.T("Cách xoá"), Loc.T("Dung lượng"), Loc.T("Kết quả")];
        for (var i = 0; i < titles.Length; i++)
        {
            var t = new TextBlock { Text = titles[i], Style = Kit.S("TableHeaderText") };
            if (i == 2)
            {
                t.HorizontalAlignment = HorizontalAlignment.Right;
                t.Margin = new Thickness(0, 0, 16, 0);
            }
            Grid.SetColumn(t, i);
            g.Children.Add(t);
        }
        return g;
    }

    // MARK: Dữ liệu

    public void Reload()
    {
        if (_storage is null)
        {
            ShowEmpty(Loc.T("Không mở được database"));
            _subtitle.Text = Loc.F("Tổng đã giải phóng: %@. Mục chuyển vào Thùng rác có thể khôi phục trong 90 ngày.", ByteCount.Format(0));
            return;
        }
        List<CleanOperation> operations;
        long total;
        try
        {
            operations = _storage.Operations();
            total = _storage.TotalFreed();
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Storage, "Không đọc được lịch sử", ex);
            ShowEmpty(ex.Message);
            return;
        }
        _subtitle.Text = Loc.F("Tổng đã giải phóng: %@. Mục chuyển vào Thùng rác có thể khôi phục trong 90 ngày.", ByteCount.Format(total));

        // Biểu đồ: 30 lần dọn gần nhất, cũ trước, cộng dồn theo ngày (BarMark unit: .day).
        var recent = operations.Take(30).Reverse().ToList();
        _chart.Data = recent.GroupBy(o => o.StartedAt.LocalDateTime.Date)
            .Select(g => new HistoryChart.Bar(g.Key, g.Sum(o => o.FreedBytes ?? 0), g.Count()))
            .OrderBy(b => b.Day).ToList();
        _chartBox.Visibility = operations.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        if (operations.Count == 0)
        {
            ShowEmpty(Loc.T("Các lần dọn dẹp sẽ hiện ở đây. Mục đã chuyển vào Thùng rác có thể khôi phục trong 90 ngày."));
            return;
        }
        _empty.Visibility = Visibility.Collapsed;
        _body.Visibility = Visibility.Visible;

        var rows = operations.Select(o => new HistoryOperationRow(o)).ToList();
        _operations.ItemsSource = rows;
        // Giữ lần dọn đang chọn; chưa chọn thì mở lần gần nhất.
        var selected = rows.FirstOrDefault(r => r.Operation.Id == _selectedId) ?? rows[0];
        _selectedId = null;
        _operations.SelectedItem = selected;
        if (_selectedId != selected.Operation.Id) Select(selected.Operation.Id);
    }

    private void ShowEmpty(string text)
    {
        _emptyText.Text = text;
        _empty.Visibility = Visibility.Visible;
        _body.Visibility = Visibility.Collapsed;
        _chartBox.Visibility = Visibility.Collapsed;
        _operations.ItemsSource = null;
        _items.ItemsSource = null;
        _selectedId = null;
    }

    private async void Select(string? operationId)
    {
        _selectedId = operationId;
        var token = ++_loadToken;
        if (operationId is null || _storage is null)
        {
            _items.ItemsSource = null;
            _itemsPanel.Visibility = Visibility.Collapsed;
            _placeholder.Visibility = Visibility.Visible;
            return;
        }
        List<CleanItemLog> items;
        try
        {
            items = await Task.Run(() => _storage.Items(operationId));
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Storage, "Không đọc được mục của lần dọn", ex);
            items = [];
        }
        if (token != _loadToken) return;
        _items.ItemsSource = items.Select(i => new HistoryItemRow(i)).ToList();
        _itemsPanel.Visibility = Visibility.Visible;
        _placeholder.Visibility = Visibility.Collapsed;
        if (_items.Items.Count > 0) _items.ScrollIntoView(_items.Items[0]);
    }

    // MARK: Khôi phục

    private void OnItemButton(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not Button { Tag: "restore", DataContext: HistoryItemRow row }) return;
        e.Handled = true;
        try
        {
            _env.CleanEngine.Restore(row.Log);
            ShowMessage(Loc.F("Đã khôi phục %@", row.DisplayPath), Theme.SafeBrush);
            Log.Info(LogCategory.Clean, "Đã khôi phục một mục từ Thùng rác");
        }
        catch (CleanEngine.RestoreException ex)
        {
            ShowMessage(ex.Message, Theme.ReviewBrush);
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Clean, "Khôi phục lỗi", ex);
            ShowMessage(ex.Message, Theme.ReviewBrush);
        }
        Select(_selectedId);
    }

    private void ShowMessage(string text, Brush brush)
    {
        _message.Text = text;
        _message.Foreground = brush;
        _message.Visibility = Visibility.Visible;
    }
}

/// <summary>
/// Biểu đồ cột dung lượng đã dọn theo ngày (Swift Charts BarMark của bản Mac): trục thời gian liên tục theo ngày,
/// cột trắng 0.8, trục MB bên phải, nhãn ngày bên dưới; rê chuột để xem số liệu.
/// </summary>
public sealed class HistoryChart : FrameworkElement
{
    public sealed record Bar(DateTime Day, long Bytes, int Count);

    private IReadOnlyList<Bar> _data = [];
    private int _hover = -1;
    private readonly ToolTip _tip = new() { Placement = PlacementMode.Mouse };

    public IReadOnlyList<Bar> Data
    {
        get => _data;
        set
        {
            _data = value;
            _hover = -1;
            InvalidateVisual();
        }
    }

    public HistoryChart()
    {
        SnapsToDevicePixels = true;
        _tip.PlacementTarget = this;
        Unloaded += (_, _) => _tip.IsOpen = false;
    }

    private const double AxisWidth = 44;
    private const double LabelHeight = 18;
    private const double TopPad = 14;

    private static readonly Brush BarBrush = Theme.Frozen(Color.FromArgb(0xCC, 255, 255, 255));
    private static readonly Brush BarHover = Brushes.White;
    private static readonly Pen GridPen = new(Theme.Frozen(Color.FromArgb(0x24, 255, 255, 255)), 1) { DashStyle = new DashStyle([3, 3], 0) };
    private static readonly Pen Baseline = new(Theme.Frozen(Color.FromArgb(0x40, 255, 255, 255)), 1);
    private static readonly Brush LabelBrush = Theme.Frozen(Color.FromArgb(0x8C, 255, 255, 255));
    private static readonly Typeface LabelFace = new(AppFonts.Text, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private FormattedText Label(string s, double size = 10) =>
        new(s, Loc.Culture, FlowDirection.LeftToRight, LabelFace, size, LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    /// <summary>Trần trục Y "đẹp" (1, 2, 2.5, 5 × 10^k) và số vạch.</summary>
    private static (double Max, int Ticks) NiceScale(double max)
    {
        if (max <= 0) return (1, 2);
        var exp = Math.Pow(10, Math.Floor(Math.Log10(max / 3)));
        foreach (var m in new[] { 1, 2, 2.5, 5, 10 })
        {
            var step = m * exp;
            var ticks = (int)Math.Ceiling(max / step - 1e-9);
            if (ticks <= 4) return (step * Math.Max(ticks, 1), Math.Max(ticks, 1));
        }
        return (Math.Ceiling(max), 4);
    }

    private Rect Plot => new(0, TopPad, Math.Max(0, ActualWidth - AxisWidth), Math.Max(0, ActualHeight - TopPad - LabelHeight));

    private (DateTime First, int Slots) Range()
    {
        var first = _data[0].Day;
        var last = _data[^1].Day;
        return (first, Math.Max(1, (int)(last - first).TotalDays + 1));
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var plot = Plot;
        if (plot.Width <= 0 || plot.Height <= 0) return;
        var maxMB = _data.Count == 0 ? 0 : _data.Max(b => b.Bytes) / 1_000_000.0;
        var (axisMax, ticks) = NiceScale(maxMB);

        // Lưới ngang + nhãn MB bên phải
        for (var i = 0; i <= ticks; i++)
        {
            var v = axisMax * i / ticks;
            var y = Math.Round(plot.Bottom - plot.Height * i / ticks) + 0.5;
            dc.DrawLine(i == 0 ? Baseline : GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Label(v.ToString(v >= 10 || v == 0 ? "N0" : "0.#", Loc.Culture));
            dc.DrawText(label, new Point(plot.Right + 8, y - label.Height / 2));
        }
        var unit = Label("MB", 10);
        dc.DrawText(unit, new Point(plot.Right + 8, 0));
        if (_data.Count == 0) return;

        var (first, slots) = Range();
        var slot = plot.Width / slots;
        var barWidth = Math.Clamp(slot * 0.62, 3, 26);
        for (var i = 0; i < _data.Count; i++)
        {
            var bar = _data[i];
            var index = (int)(bar.Day - first).TotalDays;
            var cx = plot.Left + (index + 0.5) * slot;
            var h = Math.Max(bar.Bytes > 0 ? 2 : 0, plot.Height * (bar.Bytes / 1_000_000.0) / axisMax);
            if (h <= 0) continue;
            var rect = new Rect(cx - barWidth / 2, plot.Bottom - h, barWidth, h);
            var r = Math.Min(3, barWidth / 2);
            dc.DrawRoundedRectangle(i == _hover ? BarHover : BarBrush, null, rect, r, r);
        }

        // Nhãn ngày: tối đa ~6 nhãn, không chồng nhau.
        var format = Loc.IsEnglish ? "MMM d" : "d/M";
        var maxLabels = Math.Max(1, Math.Min(6, (int)(plot.Width / 64)));
        var stride = Math.Max(1, (int)Math.Ceiling(slots / (double)maxLabels));
        double lastRight = double.NegativeInfinity;
        for (var s = 0; s < slots; s += stride)
        {
            var text = Label(first.AddDays(s).ToString(format, Loc.Culture));
            var cx = plot.Left + (s + 0.5) * slot;
            var x = Math.Clamp(cx - text.Width / 2, 0, Math.Max(0, plot.Right - text.Width));
            if (x < lastRight + 6) continue;
            dc.DrawText(text, new Point(x, plot.Bottom + 4));
            lastRight = x + text.Width;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hover = HitBar(e.GetPosition(this));
        if (hover == _hover) return;
        _hover = hover;
        InvalidateVisual();
        if (hover >= 0)
        {
            var b = _data[hover];
            _tip.Content = b.Day.ToString(Loc.IsEnglish ? "MMM d, yyyy" : "d 'thg' M, yyyy", Loc.Culture) + "\n" +
                           Loc.F("Đã dọn %@ · %lld lần", ByteCount.Format(b.Bytes), b.Count);
            _tip.IsOpen = false;
            _tip.IsOpen = true;
        }
        else
        {
            _tip.IsOpen = false;
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        _tip.IsOpen = false;
        InvalidateVisual();
    }

    private int HitBar(Point p)
    {
        if (_data.Count == 0) return -1;
        var plot = Plot;
        if (p.X < plot.Left || p.X > plot.Right || p.Y < 0 || p.Y > plot.Bottom + LabelHeight) return -1;
        var (first, slots) = Range();
        var slot = plot.Width / slots;
        var index = (int)((p.X - plot.Left) / slot);
        for (var i = 0; i < _data.Count; i++)
        {
            if ((int)(_data[i].Day - first).TotalDays == index) return i;
        }
        return -1;
    }
}
