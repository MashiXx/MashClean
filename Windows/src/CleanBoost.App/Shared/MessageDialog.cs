using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CleanBoost.App.UI;

namespace CleanBoost.App.Shared;

/// <summary>
/// Hộp thoại xác nhận theo design system (nền tối, nút viên nang), thay cho NSAlert/sheet của bản Mac.
/// Trả về <c>true</c> khi người dùng bấm nút chính.
/// </summary>
public sealed class MessageDialog : Window
{
    private bool _result;

    private MessageDialog(string title, string message, string primary, string? secondary, bool destructive, Accent accent)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 440;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Theme.Apply(this, accent);

        var card = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = Theme.Background(Accent.Neutral),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(22),
            Margin = new Thickness(16),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.45 },
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = title, FontFamily = AppFonts.Display, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock
        {
            Text = message, FontSize = 13, Foreground = new SolidColorBrush(Color.FromArgb(0xD9, 255, 255, 255)), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 20), LineHeight = 19,
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (secondary is not null)
        {
            var cancel = new Button { Content = secondary, Style = (Style)Application.Current.FindResource("GlassButton"), IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
            cancel.Click += (_, _) => { _result = false; Close(); };
            buttons.Children.Add(cancel);
        }
        var ok = new Button { Content = primary, Style = (Style)Application.Current.FindResource("PrimaryButton"), IsDefault = secondary is null || !destructive };
        if (destructive) ok.Foreground = Theme.RiskyBrush;
        ok.Click += (_, _) => { _result = true; Close(); };
        buttons.Children.Add(ok);
        stack.Children.Add(buttons);
        card.Child = stack;
        Content = card;
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { _result = false; Close(); } };
    }

    /// <summary>Hiện hộp thoại modal trên cửa sổ cha.</summary>
    public static bool Show(Window? owner, string title, string message, string primary, string? secondary = null, bool destructive = false,
        Accent accent = Accent.Neutral)
    {
        var dialog = new MessageDialog(title, message, primary, secondary, destructive, accent);
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog._result;
    }
}
