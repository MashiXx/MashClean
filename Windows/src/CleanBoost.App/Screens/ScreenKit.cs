using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Shell;
using CleanBoost.App.UI;
using CleanBoost.Core;

namespace CleanBoost.App.Screens;

/// <summary>Tiện ích dựng giao diện bằng code cho các màn ứng dụng (cùng design system với <c>UI\Styles.xaml</c>).</summary>
internal static class Kit
{
    private static readonly ScreenStyles s_styles = new();

    public static readonly Brush White = Brushes.White;
    public static readonly Brush Secondary = Theme.Frozen(Color.FromArgb(0xB8, 255, 255, 255));
    public static readonly Brush Tertiary = Theme.Frozen(Color.FromArgb(0x80, 255, 255, 255));
    public static readonly Brush Divider = Theme.Frozen(Color.FromArgb(0x17, 255, 255, 255));
    /// <summary>Màu cam của chấm trạng thái "chưa ổn" (Color.orange của macOS).</summary>
    public static readonly Brush Orange = Theme.Frozen(Color.FromRgb(0xFF, 0x9F, 0x0A));

    /// <summary>Style theo khoá: ưu tiên <see cref="ScreenStyles"/>, sau đó tài nguyên của app.</summary>
    public static Style S(string key) => (Style)(s_styles.Contains(key) ? s_styles[key] : Application.Current.FindResource(key));

    public static DataTemplate Template(string key) => (DataTemplate)s_styles[key];

    public static TextBlock Text(string text, double size = 13, Brush? foreground = null, FontWeight? weight = null, bool wrap = false)
    {
        var t = new TextBlock { Text = text, FontSize = size, Foreground = foreground ?? White, FontWeight = weight ?? FontWeights.Normal };
        if (wrap)
        {
            t.TextWrapping = TextWrapping.Wrap;
            t.LineHeight = Math.Round(size * 1.42);
        }
        else
        {
            t.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        return t;
    }

    /// <summary>Nút viên nang trong suốt (GlassButtonStyle), có thể kèm biểu tượng.</summary>
    public static Button Glass(string text, string? symbol = null, bool small = false)
    {
        var b = new Button { Style = S(small ? "SmallGlassButton" : "GlassButton") };
        b.Content = symbol is null ? text : IconLabel(symbol, text, small ? 11 : 12);
        System.Windows.Automation.AutomationProperties.SetName(b, text);
        return b;
    }

    /// <summary>Nút chính (PrimaryButtonStyle): nền trắng, chữ màu giữa của gradient đang dùng.</summary>
    public static Button Primary(string text) => new() { Style = S("PrimaryButton"), Content = text };

    public static StackPanel IconLabel(string symbol, string text, double iconSize = 12)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        p.Children.Add(new Icon { Symbol = symbol, Size = iconSize, Margin = new Thickness(0, 1, 7, 0) });
        p.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return p;
    }

    /// <summary>FeatureHeader của bản Mac: biểu tượng lớn có bóng, tiêu đề, phụ đề căn giữa.</summary>
    public static StackPanel Header(string symbol, string title, string subtitle, double iconSize = 64, double subtitleWidth = 470)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(new Icon
        {
            Symbol = symbol, Size = iconSize, Foreground = White, HorizontalAlignment = HorizontalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, ShadowDepth = 0, Opacity = 0.25 },
        });
        stack.Children.Add(new TextBlock { Text = title, Style = S("TitleText"), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 12, 0, 0) });
        if (!string.IsNullOrEmpty(subtitle))
        {
            stack.Children.Add(new TextBlock
            {
                Text = subtitle, Style = S("SecondaryBodyText"), TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                MaxWidth = subtitleWidth, Margin = new Thickness(0, 8, 0, 0), LineHeight = 19,
            });
        }
        return stack;
    }

    public static Ellipse Dot(bool ok) => new() { Width = 9, Height = 9, Fill = ok ? Theme.SafeBrush : Orange, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>Công tắc (style <c>Switch</c>): nhãn bên trái, công tắc bên phải.</summary>
    public static CheckBox Toggle(string text, bool isOn, Action<bool> changed, double fontSize = 13)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = fontSize, VerticalAlignment = VerticalAlignment.Center, LineHeight = Math.Round(fontSize * 1.38) };
        var box = new CheckBox { Style = S("Switch"), Content = label, IsChecked = isOn, Foreground = White };
        System.Windows.Automation.AutomationProperties.SetName(box, text);
        box.Checked += (_, _) => changed(true);
        box.Unchecked += (_, _) => changed(false);
        return box;
    }

    /// <summary>Đường kẻ ngang mảnh màu trắng với độ mờ <paramref name="alpha"/>.</summary>
    public static Border HLine(byte alpha = 0x17) => new() { Height = 1, Background = Theme.Frozen(Color.FromArgb(alpha, 255, 255, 255)) };

    /// <summary>Mở thư mục trong File Explorer (tạo nếu chưa có).</summary>
    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.UI, "Không mở được thư mục", ex);
        }
    }

    /// <summary>Hiện file trong File Explorer (chọn sẵn).</summary>
    public static void RevealFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.UI, "Không mở được File Explorer", ex);
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Bo góc cửa sổ trên Windows 11 (Windows 10 bỏ qua).</summary>
    public static void RoundCorners(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var preference = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(int));
        }
        catch
        {
            // không hỗ trợ
        }
    }

    /// <summary>Đổi nội dung với hiệu ứng mờ dần (cross-fade 0.25 s như bản Mac).</summary>
    public static void FadeIn(UIElement element) =>
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
}

/// <summary>
/// Cửa sổ phụ theo design system (Onboarding, Cài đặt): không có thanh tiêu đề hệ thống, nền gradient, bo góc,
/// kéo được bằng nền, có bóng của Windows.
/// </summary>
public abstract class ScreenWindow : Window
{
    protected ScreenWindow(Accent accent, double width, double height)
    {
        Width = width;
        Height = height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = (FontFamily)Application.Current.FindResource("TextFont");
        Foreground = Brushes.White;
        Background = Theme.Background(accent);
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Resources.MergedDictionaries.Add(new ScreenStyles());
        Theme.Apply(this, accent);
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0, GlassFrameThickness = new Thickness(0), ResizeBorderThickness = new Thickness(0), UseAeroCaptionButtons = false,
            CornerRadius = new CornerRadius(0),
        });
        SourceInitialized += (_, _) => Kit.RoundCorners(this);
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); } catch (InvalidOperationException) { }
        };
    }

    /// <summary>Nút đóng kiểu nút tiêu đề Windows ở góc trên phải.</summary>
    protected Button CloseButton()
    {
        var b = new Button
        {
            Style = Kit.S("CloseCaptionButton"), Content = "", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            ToolTip = Loc.T("Đóng"),
        };
        System.Windows.Automation.AutomationProperties.SetName(b, Loc.T("Đóng"));
        b.Click += (_, _) => Close();
        return b;
    }
}

/// <summary>Bước tăng giảm số (Stepper của bản Mac): viên nang [ − | + ], giữ nút để lặp, phím mũi tên lên/xuống.</summary>
public sealed class Stepper : Border
{
    private readonly RepeatButton _minus;
    private readonly RepeatButton _plus;

    public int Value { get; private set; }
    public int Minimum { get; }
    public int Maximum { get; }
    public int Step { get; }
    public event Action<int>? ValueChanged;

    public Stepper(int value, int minimum, int maximum, int step)
    {
        Minimum = minimum;
        Maximum = maximum;
        Step = Math.Max(1, step);
        Value = Math.Clamp(value, minimum, maximum);
        CornerRadius = new CornerRadius(8);
        Background = Theme.Frozen(Color.FromArgb(0x24, 255, 255, 255));
        BorderBrush = Theme.Frozen(Color.FromArgb(0x38, 255, 255, 255));
        BorderThickness = new Thickness(1);
        Padding = new Thickness(2);
        VerticalAlignment = VerticalAlignment.Center;
        Focusable = false;

        _minus = Button("", -1, Loc.T("Giảm"));
        _plus = Button("", +1, Loc.T("Tăng"));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_minus);
        row.Children.Add(new Border { Width = 1, Margin = new Thickness(2, 4, 2, 4), Background = Theme.Frozen(Color.FromArgb(0x33, 255, 255, 255)) });
        row.Children.Add(_plus);
        Child = row;
        UpdateEnabled();
    }

    private RepeatButton Button(string glyph, int direction, string name)
    {
        var b = new RepeatButton
        {
            Style = Kit.S("StepperButton"),
            Content = new TextBlock { Text = glyph, FontFamily = Icons.Font, FontSize = 10 },
        };
        System.Windows.Automation.AutomationProperties.SetName(b, name);
        b.Click += (_, _) => Change(direction * Step);
        b.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Up) { Change(Step); e.Handled = true; }
            else if (e.Key == Key.Down) { Change(-Step); e.Handled = true; }
        };
        return b;
    }

    private void Change(int delta)
    {
        var v = Math.Clamp(Value + delta, Minimum, Maximum);
        if (v == Value) return;
        Value = v;
        UpdateEnabled();
        ValueChanged?.Invoke(v);
    }

    private void UpdateEnabled()
    {
        _minus.IsEnabled = Value > Minimum;
        _plus.IsEnabled = Value < Maximum;
    }
}

/// <summary>Bộ chọn phân đoạn (Picker .segmented của bản Mac).</summary>
public sealed class SegmentedPicker : Border
{
    public string Selected { get; private set; }
    public event Action<string>? SelectionChanged;

    public SegmentedPicker(IEnumerable<(string Value, string Title)> options, string selected)
    {
        Selected = selected;
        CornerRadius = new CornerRadius(9);
        Background = Theme.Frozen(Color.FromArgb(0x1F, 255, 255, 255));
        BorderBrush = Theme.Frozen(Color.FromArgb(0x26, 255, 255, 255));
        BorderThickness = new Thickness(1);
        Padding = new Thickness(2);
        VerticalAlignment = VerticalAlignment.Center;
        var group = "segment-" + Guid.NewGuid().ToString("N");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (value, title) in options)
        {
            var rb = new RadioButton { Style = Kit.S("SegmentButton"), GroupName = group, Content = title, IsChecked = value == selected };
            rb.Checked += (_, _) =>
            {
                if (Selected == value) return;
                Selected = value;
                SelectionChanged?.Invoke(value);
            };
            row.Children.Add(rb);
        }
        Child = row;
    }
}

/// <summary>Vòng quay chờ (ProgressView không xác định của bản Mac).</summary>
public sealed class Spinner : FrameworkElement
{
    private readonly RotateTransform _rotate = new();

    public Spinner()
    {
        Width = Height = 18;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = _rotate;
        Loaded += (_, _) => _rotate.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        Unloaded += (_, _) => _rotate.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    private static readonly Pen Track = new(Theme.Frozen(Color.FromArgb(0x33, 255, 255, 255)), 2);
    private static readonly Pen Arc = new(Brushes.White, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

    protected override void OnRender(DrawingContext dc)
    {
        var r = Math.Min(ActualWidth, ActualHeight) / 2 - 1.5;
        if (r <= 0) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, Track, c, r, r);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(c.X, c.Y - r), false, false);
            ctx.ArcTo(new Point(c.X + r, c.Y), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, Arc, geo);
    }
}

/// <summary>Chữ một dòng rút gọn ở giữa (truncationMode .middle của bản Mac) — giữ được đầu và tên file của đường dẫn dài.</summary>
public sealed class MiddleTrimmedText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(MiddleTrimmedText),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(typeof(MiddleTrimmedText),
        new FrameworkPropertyMetadata(13.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(MiddleTrimmedText),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    private static readonly Typeface s_typeface = new(AppFonts.Text, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private (string Text, double Width, string Result)? _cache;

    private FormattedText Format(string s) => new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, s_typeface, FontSize, Foreground,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override Size MeasureOverride(Size availableSize)
    {
        var ft = Format(string.IsNullOrEmpty(Text) ? " " : Text);
        var w = ft.WidthIncludingTrailingWhitespace;
        if (!double.IsInfinity(availableSize.Width)) w = Math.Min(w, availableSize.Width);
        return new Size(Math.Ceiling(w), Math.Ceiling(ft.Height));
    }

    private string Fit(string text, double width)
    {
        if (_cache is { } c && c.Text == text && Math.Abs(c.Width - width) < 0.5) return c.Result;
        string result;
        if (Format(text).WidthIncludingTrailingWhitespace <= width + 0.5)
        {
            result = text;
        }
        else
        {
            // Tìm số ký tự giữ lại lớn nhất vừa chiều rộng (đầu và cuối chia đôi, cuối được ưu tiên hơn một chút).
            int lo = 0, hi = text.Length;
            result = "…";
            while (lo <= hi)
            {
                var keep = (lo + hi) / 2;
                var tail = (keep + 1) / 2;
                var head = keep - tail;
                var candidate = text[..head] + "…" + text[^tail..];
                if (keep == 0 || Format(candidate).WidthIncludingTrailingWhitespace <= width)
                {
                    result = candidate;
                    lo = keep + 1;
                }
                else
                {
                    hi = keep - 1;
                }
            }
        }
        _cache = (text, width, result);
        return result;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var text = Text ?? "";
        if (text.Length == 0 || ActualWidth <= 0) return;
        var ft = Format(Fit(text, ActualWidth));
        dc.DrawText(ft, new Point(0, Math.Max(0, (ActualHeight - ft.Height) / 2)));
    }
}

/// <summary>Chỉ báo bước của onboarding: mỗi bước một viên nang 36×4, trắng nếu đã tới, trắng 0.25 nếu chưa.</summary>
public sealed class StepIndicator : StackPanel
{
    private static readonly Brush Done = Brushes.White;
    private static readonly Brush Pending = Theme.Frozen(Color.FromArgb(0x40, 255, 255, 255));

    public StepIndicator(int count)
    {
        Orientation = Orientation.Horizontal;
        HorizontalAlignment = HorizontalAlignment.Center;
        for (var i = 0; i < count; i++)
        {
            Children.Add(new Border { Width = 36, Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(4, 0, 4, 0), Background = Pending });
        }
    }

    public void Set(int current)
    {
        for (var i = 0; i < Children.Count; i++) ((Border)Children[i]).Background = i <= current ? Done : Pending;
    }
}
