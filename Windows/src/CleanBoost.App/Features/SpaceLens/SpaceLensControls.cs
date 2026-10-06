using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CleanBoost.App.UI;
using CleanBoost.Core;
using CleanBoost.Engine.FileSystem;
using CleanBoost.Features.SpaceLens;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CleanBoost.App.Features.SpaceLens;

/// <summary>Một dòng trong danh sách con của thư mục đang xem.</summary>
public sealed partial class LensRow : ObservableObject
{
    private ImageSource? _icon;
    private bool _iconLoaded;

    public LensRow(DiskTree tree, int index, long parentSize)
    {
        Name = tree.Name(index);
        Path = tree.Path(index);
        Apply(tree, index, parentSize);
    }

    public string Name { get; }
    public string Path { get; }
    public int Index { get; private set; }

    [ObservableProperty] private long _size;
    [ObservableProperty] private double _fraction;
    [ObservableProperty] private bool _isDirectory;
    [ObservableProperty] private bool _hasChildren;
    [ObservableProperty] private DiskNodeFlags _flags;
    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isHighlighted;

    /// <summary>Cập nhật số liệu tại chỗ (giữ nguyên dòng để danh sách không nhảy khi cây được làm mới).</summary>
    public void Apply(DiskTree tree, int index, long parentSize)
    {
        Index = index;
        Size = tree.Size(index);
        Fraction = parentSize <= 0 ? 0 : Math.Clamp((double)Size / parentSize, 0, 1);
        IsDirectory = tree.IsDirectory(index);
        HasChildren = IsDirectory && tree.HasChildren(index);
        Flags = tree.Flags(index);
    }

    partial void OnSizeChanged(long value) => OnPropertyChanged(nameof(SizeText));
    partial void OnFlagsChanged(DiskNodeFlags value)
    {
        OnPropertyChanged(nameof(BadgeGlyph));
        OnPropertyChanged(nameof(BadgeTooltip));
        OnPropertyChanged(nameof(BadgeBrush));
        OnPropertyChanged(nameof(HasBadge));
    }

    public string SizeText => ByteCount.Format(Size);

    /// <summary>Biểu tượng thật của shell (thư mục/loại file dùng chung bộ nhớ đệm), nạp khi dòng hiện ra.</summary>
    public ImageSource? Icon
    {
        get
        {
            if (!_iconLoaded)
            {
                _iconLoaded = true;
                _icon = ShellIcons.For(Path, IsDirectory);
            }
            return _icon;
        }
    }

    public bool HasIcon => Icon is not null;
    public string FallbackGlyph => Icons.Glyph(IsDirectory ? "folder" : "doc");

    private (string Glyph, string Tooltip, Brush Brush)? Badge
    {
        get
        {
            if ((Flags & DiskNodeFlags.Unreadable) != 0) return ("", Loc.T("Không đọc được (cần quyền quản trị)"), Theme.ReviewBrush);
            if ((Flags & DiskNodeFlags.OtherVolume) != 0) return (Icons.Glyph("externaldrive"), Loc.T("Volume khác, không tính"), TertiaryBrush);
            if ((Flags & DiskNodeFlags.Symlink) != 0) return (Icons.Glyph("link"), Loc.T("Liên kết (symlink/junction), không đi theo"), TertiaryBrush);
            if ((Flags & DiskNodeFlags.CloudOnly) != 0) return ("", Loc.T("Chỉ có trên đám mây, không chiếm dung lượng ổ"), TertiaryBrush);
            if ((Flags & DiskNodeFlags.Opaque) != 0) return (Icons.Glyph("trash"), Loc.T("Thùng rác của ổ này: chỉ đo tổng. Dọn ở màn Rác hệ thống."), TertiaryBrush);
            if ((Flags & DiskNodeFlags.HardLinkDuplicate) != 0) return (Icons.Glyph("link"), Loc.T("Hard link: dung lượng đã tính ở chỗ khác"), TertiaryBrush);
            return null;
        }
    }

    public bool HasBadge => Badge is not null;
    public string BadgeGlyph => Badge?.Glyph ?? "";
    public string BadgeTooltip => Badge?.Tooltip ?? "";
    public Brush BadgeBrush => Badge?.Brush ?? TertiaryBrush;

    private static readonly Brush TertiaryBrush = Theme.Frozen(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
}

/// <summary>Một ổ trên màn chọn.</summary>
public sealed record LensVolume(VolumeInfo Info)
{
    public string Name => Info.Name;
    public string Glyph => Icons.Glyph(Info.DriveType == DriveType.Fixed ? "internaldrive" : "externaldrive");
    public double UsedFraction => Info.UsedFraction;
    public string FreeText => Loc.F("Còn trống %@ / %@", ByteCount.Format(Info.Available), ByteCount.Format(Info.Total));
    public Brush BarBrush => Info.UsedFraction > 0.9 ? Theme.RiskyBrush : Brushes.White;
}

/// <summary>Thanh dung lượng tương đối 3 px dưới tên (viên nang trắng 0.5, tối thiểu 2 px).</summary>
public sealed class SizeBar : FrameworkElement
{
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction), typeof(double), typeof(SizeBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush Fill = Theme.Frozen(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));

    public double Fraction { get => (double)GetValue(FractionProperty); set => SetValue(FractionProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var h = ActualHeight;
        if (h <= 0 || ActualWidth <= 0) return;
        var w = Math.Max(2, ActualWidth * Math.Clamp(Fraction, 0, 1));
        dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, w, h), h / 2, h / 2);
    }
}

/// <summary>Vòng quay chờ (khi quét một thư mục không biết trước tổng): rãnh trắng 0.15, cung trắng quay liên tục.</summary>
public sealed class Spinner : FrameworkElement
{
    private static readonly Brush TrackBrush = Theme.Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    private readonly RotateTransform _rotation = new();

    public double LineWidth { get; set; } = 12;

    public Spinner()
    {
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = _rotation;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
            else _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        };
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var r = (size - LineWidth) / 2;
        dc.DrawEllipse(null, new Pen(TrackBrush, LineWidth), c, r, r);
        var brush = new LinearGradientBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), Colors.White, new Point(0, 1), new Point(1, 0));
        var pen = new Pen(brush, LineWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X + r, c.Y);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }
}
