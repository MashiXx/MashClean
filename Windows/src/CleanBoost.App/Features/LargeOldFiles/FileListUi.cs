using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using CleanBoost.App.Shared;
using CleanBoost.App.UI;
using CleanBoost.Core;
using CleanBoost.Engine.Nodes;

// Thành phần giao diện dùng chung cho hai màn danh sách file của người dùng: File lớn & cũ và File trùng lặp.
namespace CleanBoost.App.Features.LargeOldFiles;

/// <summary>
/// Chữ một dòng cắt ở giữa (<c>truncationMode(.middle)</c> của bản Mac): "~\mashicode\Chess…\win64-bin".
/// WPF chỉ có cắt ở cuối nên tự đo bằng <see cref="FormattedText"/> và tìm điểm cắt bằng chia đôi.
/// </summary>
public sealed class MiddleTrimmedText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(MiddleTrimmedText),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(MiddleTrimmedText),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(typeof(MiddleTrimmedText),
        new FrameworkPropertyMetadata(13.0, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(typeof(MiddleTrimmedText),
        new FrameworkPropertyMetadata(FontWeights.Normal, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }

    private FormattedText? _shown;

    public MiddleTrimmedText()
    {
        SnapsToDevicePixels = true;
        VerticalAlignment = VerticalAlignment.Center;
    }

    private FormattedText Format(string s) => new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface(AppFonts.Text, FontStyles.Normal, FontWeight, FontStretches.Normal), FontSize, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override Size MeasureOverride(Size available)
    {
        var text = Text ?? "";
        var full = Format(text);
        _shown = full;
        if (full.WidthIncludingTrailingWhitespace > available.Width && text.Length > 2)
        {
            // Giữ nhiều ký tự nhất có thể, chia đều hai đầu (đầu được ưu tiên một ký tự).
            int lo = 0, hi = text.Length - 1;
            var best = Format("…");
            while (lo <= hi)
            {
                var keep = (lo + hi) / 2;
                var head = (keep + 1) / 2;
                var candidate = Format(text[..head] + "…" + text[(text.Length - (keep - head))..]);
                if (candidate.WidthIncludingTrailingWhitespace <= available.Width)
                {
                    best = candidate;
                    lo = keep + 1;
                }
                else
                {
                    hi = keep - 1;
                }
            }
            _shown = best;
        }
        return new Size(Math.Min(_shown.WidthIncludingTrailingWhitespace, double.IsInfinity(available.Width) ? double.MaxValue : available.Width), full.Height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_shown is null) return;
        _shown.SetForegroundBrush(Foreground);
        dc.DrawText(_shown, new Point(0, Math.Max(0, (RenderSize.Height - _shown.Height) / 2)));
    }
}

/// <summary>
/// Hàng ngang mà một con (đánh dấu <see cref="ShrinkProperty"/>) co lại theo chỗ còn trống, các con khác giữ kích thước:
/// "icon · tên dài bị cắt… · [nhãn]" với nhãn đi ngay sau tên.
/// </summary>
public sealed class InlineRow : Panel
{
    public static readonly DependencyProperty ShrinkProperty = DependencyProperty.RegisterAttached("Shrink", typeof(bool), typeof(InlineRow),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static bool GetShrink(DependencyObject d) => (bool)d.GetValue(ShrinkProperty);
    public static void SetShrink(DependencyObject d, bool value) => d.SetValue(ShrinkProperty, value);

    public double Spacing { get; set; } = 6;

    protected override Size MeasureOverride(Size available)
    {
        double fixedWidth = 0, height = 0;
        var visible = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        foreach (var c in visible.Where(c => !GetShrink(c)))
        {
            c.Measure(new Size(double.PositiveInfinity, available.Height));
            fixedWidth += c.DesiredSize.Width;
            height = Math.Max(height, c.DesiredSize.Height);
        }
        fixedWidth += Spacing * Math.Max(0, visible.Count - 1);
        var rest = Math.Max(0, available.Width - fixedWidth);
        double shrinkWidth = 0;
        foreach (var c in visible.Where(GetShrink))
        {
            c.Measure(new Size(rest, available.Height));
            shrinkWidth += c.DesiredSize.Width;
            height = Math.Max(height, c.DesiredSize.Height);
        }
        return new Size(Math.Min(fixedWidth + shrinkWidth, double.IsInfinity(available.Width) ? double.MaxValue : available.Width), height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double x = 0;
        foreach (UIElement c in InternalChildren)
        {
            if (c.Visibility == Visibility.Collapsed) continue;
            var w = Math.Min(c.DesiredSize.Width, Math.Max(0, final.Width - x));
            c.Arrange(new Rect(x, (final.Height - c.DesiredSize.Height) / 2, w, c.DesiredSize.Height));
            x += w + Spacing;
        }
        return final;
    }
}

/// <summary>Danh sách thư mục quét do người dùng chỉnh (có khoá: task quét đọc bản chụp từ luồng nền).</summary>
public sealed class ScanRootList(Func<IReadOnlyList<string>> defaults)
{
    private readonly object _lock = new();
    private List<string> _roots = [.. defaults()];

    public event Action? Changed;

    public IReadOnlyList<string> Snapshot
    {
        get { lock (_lock) return _roots.ToArray(); }
    }

    public bool IsDefault
    {
        get
        {
            var d = defaults();
            lock (_lock) return _roots.Count == d.Count && _roots.Zip(d).All(p => p.First.Equals(p.Second, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Add(IEnumerable<string> paths)
    {
        lock (_lock)
        {
            foreach (var p in paths)
            {
                var path = p.Length <= 3 ? p : p.TrimEnd('\\');
                if (!_roots.Any(r => r.Equals(path, StringComparison.OrdinalIgnoreCase))) _roots.Add(path);
            }
        }
        Changed?.Invoke();
    }

    public void Remove(string path)
    {
        lock (_lock) _roots.RemoveAll(r => r.Equals(path, StringComparison.OrdinalIgnoreCase));
        Changed?.Invoke();
    }

    public void Reset()
    {
        lock (_lock) _roots = [.. defaults()];
        Changed?.Invoke();
    }
}

/// <summary>Định dạng ngày như <c>DateFormatter</c> kiểu medium của bản Mac.</summary>
public static class FileDates
{
    public static string Medium(DateTime? utc)
    {
        if (utc is not { } u || u <= DateTime.MinValue.AddDays(1)) return "—";
        var local = u.ToLocalTime();
        return Loc.IsEnglish ? local.ToString("MMM d, yyyy", Loc.Culture) : local.ToString("d 'thg' M, yyyy", Loc.Culture);
    }

    public static string MediumWithTime(DateTime? utc)
    {
        if (utc is not { } u || u <= DateTime.MinValue.AddDays(1)) return "";
        var local = u.ToLocalTime();
        return Loc.IsEnglish ? local.ToString("MMM d, yyyy 'at' h:mm tt", Loc.Culture) : local.ToString("d 'thg' M, yyyy 'lúc' HH:mm", Loc.Culture);
    }
}

/// <summary>Hành động trên file: hiện trong Explorer, mở, sao chép đường dẫn — và menu chuột phải tương ứng.</summary>
public static class FileActions
{
    public static void Reveal(string? path) => NodeOutlineView.Reveal(path);

    public static void Open(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory.UI, "Không mở được file: " + ex.Message);
        }
    }

    public static void CopyPaths(IEnumerable<string> paths)
    {
        var list = paths.Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (list.Count == 0) return;
        try { Clipboard.SetText(string.Join(Environment.NewLine, list)); } catch { }
    }

    /// <summary>Menu chuột phải cho danh sách: <paramref name="selection"/> trả về đường dẫn của các dòng đang chọn.</summary>
    public static ContextMenu Menu(Func<IReadOnlyList<string>> selection)
    {
        var menu = new ContextMenu();
        var reveal = new MenuItem { Header = Loc.T("Hiện trong File Explorer") };
        reveal.Click += (_, _) => Reveal(selection().FirstOrDefault());
        var open = new MenuItem { Header = Loc.T("Mở") };
        open.Click += (_, _) => Open(selection().FirstOrDefault());
        var copy = new MenuItem { Header = Loc.T("Sao chép đường dẫn") };
        copy.Click += (_, _) => CopyPaths(selection());
        menu.Items.Add(reveal);
        menu.Items.Add(open);
        menu.Items.Add(new Separator());
        menu.Items.Add(copy);
        menu.Opened += (_, _) =>
        {
            var any = selection().Count > 0;
            reveal.IsEnabled = open.IsEnabled = copy.IsEnabled = any;
        };
        return menu;
    }
}

/// <summary>Các khối giao diện nhỏ dựng bằng code.</summary>
public static class FileListUi
{
    public static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
    public static Style StyleOf(string key) => (Style)Application.Current.FindResource(key);

    /// <summary>Viên nang (Pill của bản Mac): nền màu 0.2, chữ 10.5 đậm.</summary>
    public static Border Pill(string text, Color? color = null, bool colorText = false)
    {
        var c = color ?? Colors.White;
        return new Border
        {
            Style = StyleOf("PillBorder"),
            Background = Theme.Frozen(Color.FromArgb(0x33, c.R, c.G, c.B)),
            Child = new TextBlock { Text = text, Style = (Style)Application.Current.FindResource("PillText"), Foreground = colorText ? Theme.Frozen(c) : Brushes.White },
        };
    }

    /// <summary>Viên nang loại file ở màn chờ (KindChips của bản Mac).</summary>
    public static WrapPanel Chips(IEnumerable<string> titles)
    {
        var wrap = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 640 };
        foreach (var t in titles)
        {
            wrap.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = Theme.Frozen(Color.FromArgb(0x33, 255, 255, 255)),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(4),
                Child = new TextBlock { Text = t, FontSize = 12, Foreground = Brushes.White },
            });
        }
        return wrap;
    }

    /// <summary>Chọn thư mục (nhiều thư mục một lần).</summary>
    public static IReadOnlyList<string> PickFolders(DependencyObject owner)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Multiselect = true, Title = Loc.T("Thêm thư mục…") };
        return dialog.ShowDialog(Window.GetWindow(owner)) == true ? dialog.FolderNames : [];
    }
}

/// <summary>
/// Thẻ "Thư mục quét" (RootsEditor của bản Mac, rộng tối đa 480): mỗi thư mục một dòng có nút bỏ, ghi chú phạm vi,
/// nút "Thêm thư mục…" và "Mặc định".
/// </summary>
public sealed class RootsEditor : Border
{
    private readonly ScanRootList _roots;
    private readonly StackPanel _rows = new();
    private readonly Button _defaults;

    public RootsEditor(ScanRootList roots, string note)
    {
        _roots = roots;
        Style = FileListUi.StyleOf("Card");
        Padding = new Thickness(14, 12, 14, 12);
        MaxWidth = 480;
        MinWidth = 380;
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = Loc.T("Thư mục quét"), Style = FileListUi.StyleOf("HeadlineText"), Margin = new Thickness(0, 0, 0, 6) });
        stack.Children.Add(_rows);
        stack.Children.Add(new TextBlock
        {
            Text = note, Style = FileListUi.StyleOf("TertiaryCaptionText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10),
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var add = new Button { Content = Loc.T("Thêm thư mục…"), Style = FileListUi.StyleOf("GlassButton"), Margin = new Thickness(0, 0, 8, 0) };
        add.Click += (_, _) =>
        {
            var picked = FileListUi.PickFolders(this);
            if (picked.Count > 0) _roots.Add(picked);
        };
        _defaults = new Button { Content = Loc.T("Mặc định"), Style = FileListUi.StyleOf("GlassButton") };
        _defaults.Click += (_, _) => _roots.Reset();
        buttons.Children.Add(add);
        buttons.Children.Add(_defaults);
        stack.Children.Add(buttons);
        Child = stack;
        roots.Changed += () => Dispatcher.InvokeAsync(Rebuild);
        Rebuild();
    }

    private void Rebuild()
    {
        _rows.Children.Clear();
        foreach (var root in _roots.Snapshot)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var remove = new Button { Style = FileListUi.StyleOf("IconButton"), Width = 24, Height = 24, ToolTip = Loc.T("Bỏ thư mục này") };
            remove.Content = new Icon { Symbol = "minus.circle", Size = 13, Foreground = FileListUi.Res("SecondaryText") };
            remove.Click += (_, _) => _roots.Remove(root);
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            var icon = new Image { Source = ShellIcons.For(root, isDirectory: true), Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(new MiddleTrimmedText { Text = root.AbbreviatingHome(), FontSize = 13, ToolTip = root });
            _rows.Children.Add(row);
        }
        _defaults.IsEnabled = !_roots.IsDefault;
    }
}

/// <summary>
/// Chân trang kết quả của hai màn file (footer của bản Mac): "Đã chọn …", "… file · có thể khôi phục từ Thùng rác",
/// các nút phụ, nhãn DRY RUN, nút chính "Chuyển vào Thùng rác".
/// </summary>
public sealed class SelectionFooter : DockPanel
{
    private readonly TextBlock _selected = new() { Style = FileListUi.StyleOf("HeadlineText"), FontSize = 17 };
    private readonly TextBlock _detail = new() { Style = FileListUi.StyleOf("TertiaryCaptionText") };
    private readonly Border _dryRun;
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

    private Button? _deselect;

    public Button Primary { get; }
    public Button Deselect => _deselect!;

    public SelectionFooter(string primaryTitle, bool dryRun)
    {
        LastChildFill = true;
        Margin = new Thickness(0, 16, 0, 0);
        Primary = new Button { Content = primaryTitle, Style = FileListUi.StyleOf("PrimaryButton"), IsEnabled = false };
        DockPanel.SetDock(Primary, Dock.Right);
        Children.Add(Primary);
        _dryRun = FileListUi.Pill("DRY RUN", Theme.ReviewColor, colorText: true);
        _dryRun.Margin = new Thickness(0, 0, 12, 0);
        _dryRun.Visibility = dryRun ? Visibility.Visible : Visibility.Collapsed;
        DockPanel.SetDock(_dryRun, Dock.Right);
        Children.Add(_dryRun);
        _deselect = AddButton(Loc.T("Bỏ chọn"));
        DockPanel.SetDock(_buttons, Dock.Right);
        Children.Add(_buttons);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(_selected);
        texts.Children.Add(_detail);
        Children.Add(texts);
        Update(ByteCount.Zero, 0);
    }

    /// <summary>Thêm nút kính (trước nút "Bỏ chọn").</summary>
    public Button AddButton(string title, string? tooltip = null)
    {
        var b = new Button { Content = title, Style = FileListUi.StyleOf("GlassButton"), Margin = new Thickness(0, 0, 10, 0), ToolTip = tooltip };
        _buttons.Children.Insert(Math.Max(0, _buttons.Children.Count - (_deselect is null ? 0 : 1)), b);
        return b;
    }

    public void Update(ByteCount bytes, int count)
    {
        _selected.Text = Loc.F("Đã chọn %@", bytes.Formatted);
        _detail.Text = Loc.F("%@ file · có thể khôi phục từ Thùng rác", count.ToString("N0", Loc.Culture));
        Primary.IsEnabled = count > 0;
        Deselect.IsEnabled = count > 0;
    }
}

/// <summary>
/// Checkbox hiển thị trạng thái chọn của mô hình (<see cref="SelectionState"/>) mà không tự đổi giá trị: mọi cách bật/tắt
/// (chuột, phím cách, UI Automation của trình đọc màn hình) đều đi qua <see cref="Toggled"/>; mô hình đổi xong thì binding cập nhật lại.
/// </summary>
public sealed class SelectionCheck : CheckBox
{
    public event EventHandler? Toggled;

    protected override void OnToggle() => Toggled?.Invoke(this, EventArgs.Empty);
}

/// <summary>Trạng thái chọn của một nhóm dưới dạng <c>bool?</c> cho checkbox ba trạng thái.</summary>
public static class CheckStates
{
    public static bool? ToNullable(CheckState s) => s switch { CheckState.On => true, CheckState.Off => false, _ => null };
}
