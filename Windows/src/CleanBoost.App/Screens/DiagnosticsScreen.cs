using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using CleanBoost.App.Services;
using CleanBoost.App.Shared;
using CleanBoost.App.Shell;
using CleanBoost.App.UI;
using CleanBoost.Core;
using CleanBoost.Engine.Ipc;
using CleanBoost.Engine.Persistence;
using Microsoft.Win32;

namespace CleanBoost.App.Screens;

/// <summary>
/// Báo cáo chẩn đoán (DiagnosticReport của bản Mac): phiên bản app/rule/Windows, máy, quyền quản trị và helper,
/// tóm tắt cài đặt, log 24 giờ gần nhất (tối đa 200.000 ký tự cuối mỗi file). Người dùng xem trước rồi mới lưu hoặc gửi.
/// </summary>
public sealed class DiagnosticReport
{
    public const int MaxCharsPerLog = 200_000;

    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;
    public string Rendered { get; }

    private DiagnosticReport(string rendered) => Rendered = rendered;

    public static DiagnosticReport Build(AppEnvironment env)
    {
        var s = new StringBuilder();
        var now = DateTimeOffset.Now;
        s.AppendLine(Loc.T("Clean Boost — báo cáo chẩn đoán"));
        Line(s, Loc.T("Thời điểm"), now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture));
        Line(s, "App", AppVersionText());
        var rules = env.Rules.Snapshot;
        Line(s, "Rule", $"{rules.Version} ({rules.SourceTitle})");
        Line(s, "Windows", WindowsVersionText());
        Line(s, Loc.T("Máy"), MachineText());
        Line(s, "CPU", Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) is string cpu
            ? cpu.Trim() + $" · {Environment.ProcessorCount} " + Loc.T("luồng")
            : Environment.ProcessorCount + " " + Loc.T("luồng"));
        Line(s, "RAM", ByteCount.Format(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes));
        if (env.FileSystem.SystemVolume is { } vol)
            Line(s, Loc.T("Ổ hệ thống"), Loc.F("Còn trống %@ / %@", ByteCount.Format(vol.Available), ByteCount.Format(vol.Total)) + $" ({vol.MountPoint})");
        Line(s, Loc.T("Ngôn ngữ"), $"{Loc.Active} · {CultureInfo.CurrentUICulture.Name}");

        s.AppendLine();
        s.AppendLine(Loc.T("Quyền:"));
        var permissions = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["App"] = HelperClient.IsProcessElevated ? Loc.T("quyền quản trị") : Loc.T("người dùng thường"),
            ["Helper"] = env.Helper.Status switch
            {
                HelperStatus.Running => Loc.T("đang chạy"),
                HelperStatus.NotRunning => Loc.T("chưa chạy"),
                HelperStatus.Declined => Loc.T("bị từ chối (UAC)"),
                HelperStatus.NotFound => Loc.T("không tìm thấy"),
                _ => Loc.T("không kết nối được"),
            },
            ["Helper exe"] = File.Exists(Path.Combine(AppContext.BaseDirectory, Identifiers.HelperExeName)) ? Loc.T("có") : Loc.T("không có"),
            ["Database"] = env.Storage is null ? Loc.T("không mở được") : Loc.T("có"),
        };
        foreach (var (k, v) in permissions) s.AppendLine($"  - {k}: {v}");

        s.AppendLine();
        s.AppendLine(Loc.T("Cài đặt:"));
        var st = env.Settings;
        var settings = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["app.language"] = st.Language.Code(),
            ["menuBar.enabled"] = Bool(st.MenuBarEnabled),
            ["menuBar.statusStyle"] = st.MenuBarStatusStyle is { Length: > 0 } style ? style : "iconOnly",
            ["tray.closeToTray"] = Bool(st.CloseToTray),
            ["startup.atLogin"] = Bool(st.StartAtLogin),
            ["explorer.contextMenu"] = Bool(st.ExplorerIntegration),
            ["alerts.lowDisk"] = Bool(st.LowDiskAlerts),
            ["alerts.trashGB"] = st.TrashAlertGB.ToString(CultureInfo.InvariantCulture),
            ["largeFiles.thresholdMB"] = st.LargeFileThresholdMB.ToString(CultureInfo.InvariantCulture),
            ["largeFiles.oldDays"] = st.OldFileDays.ToString(CultureInfo.InvariantCulture),
            ["scan.showRisky"] = Bool(st.ShowRiskyItems),
            ["updates.channel"] = st.UpdateChannel,
            ["analytics.enabled"] = Bool(st.AnalyticsEnabled),
            ["debug.dryRun"] = Bool(st.DryRunSetting) + (DryRun.IsEnabledByEnvironment ? " (env/--dry-run)" : ""),
            ["onboarding.completed"] = Bool(st.OnboardingCompleted),
        };
        foreach (var (k, v) in settings) s.AppendLine($"  - {k}: {v}");

        s.Append(Loc.F("\nLog 24 giờ gần nhất:\n%@", LogExcerpt()));
        return new DiagnosticReport(s.ToString());

        static string Bool(bool b) => b ? "true" : "false";
    }

    private static void Line(StringBuilder s, string key, string value) => s.Append(key).Append(": ").AppendLine(value);

    private static string AppVersionText()
    {
        var asm = System.Reflection.Assembly.GetEntryAssembly();
        var info = asm?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
        var build = info?.Split('+') is { Length: > 1 } parts ? parts[1][..Math.Min(parts[1].Length, 12)] : asm?.GetName().Version?.ToString() ?? "0";
        var flavor =
#if DEBUG
            "Debug";
#else
            "Release";
#endif
        return $"{AppVersion.Display} ({build}, {flavor}, {RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}, .NET {Environment.Version})";
    }

    /// <summary>Ví dụ "Windows 11 Pro 24H2 (10.0.26100.2314, x64)" — đọc registry, không cần WMI.</summary>
    private static string WindowsVersionText()
    {
        const string key = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var v = Environment.OSVersion.Version;
        var product = Registry.GetValue(key, "ProductName", null) as string ?? "Windows";
        // ProductName vẫn ghi "Windows 10" trên Windows 11.
        if (v.Build >= 22000) product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
        var display = Registry.GetValue(key, "DisplayVersion", null) as string ?? Registry.GetValue(key, "ReleaseId", null) as string;
        var ubr = Registry.GetValue(key, "UBR", null) is int u ? "." + u.ToString(CultureInfo.InvariantCulture) : "";
        return $"{product}{(display is null ? "" : " " + display)} ({v.Major}.{v.Minor}.{v.Build}{ubr}, {RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()})";
    }

    /// <summary>Hãng + model máy từ khoá BIOS của registry (tương đương hw.model của Mac).</summary>
    private static string MachineText()
    {
        const string key = @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS";
        string? Value(string name) => (Registry.GetValue(key, name, null) as string)?.Trim() is { Length: > 0 } v && !IsPlaceholder(v) ? v : null;
        // Máy tự lắp thường để "System Product Name"/"To be filled by O.E.M.": dùng bo mạch chủ thay thế.
        var maker = Value("SystemManufacturer");
        var model = Value("SystemProductName");
        if (model is null)
        {
            maker = Value("BaseBoardManufacturer") ?? maker;
            model = Value("BaseBoardProduct");
        }
        if (model is null) return maker ?? Loc.T("không rõ");
        return maker is null || model.StartsWith(maker, StringComparison.OrdinalIgnoreCase) ? model : maker + " " + model;
    }

    private static bool IsPlaceholder(string v) =>
        v.Equals("System Product Name", StringComparison.OrdinalIgnoreCase) || v.Equals("System manufacturer", StringComparison.OrdinalIgnoreCase) ||
        v.Contains("O.E.M", StringComparison.OrdinalIgnoreCase) || v.Equals("Default string", StringComparison.OrdinalIgnoreCase) ||
        v.Equals("Not Applicable", StringComparison.OrdinalIgnoreCase) || v.Equals("None", StringComparison.OrdinalIgnoreCase);

    /// <summary>Log sửa trong 24 giờ của Clean Boost (app, helper), mỗi file lấy tối đa 200.000 ký tự cuối.</summary>
    private static string LogExcerpt()
    {
        var sb = new StringBuilder();
        foreach (var file in Log.RecentFiles())
        {
            var name = Path.GetFileName(file);
            if (!name.StartsWith("CleanBoost", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("Clean Boost", StringComparison.OrdinalIgnoreCase)) continue;
            string text;
            try
            {
                // Log đang được ghi tiếp: mở chia sẻ đọc/ghi.
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                // UTF-8 tối đa 4 byte/ký tự: đọc đủ phần cuối rồi cắt theo ký tự.
                var maxBytes = (long)MaxCharsPerLog * 4;
                if (fs.Length > maxBytes) fs.Seek(-maxBytes, SeekOrigin.End);
                using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                text = reader.ReadToEnd();
            }
            catch (Exception ex)
            {
                text = "(" + ex.Message + ")";
            }
            if (text.Length > MaxCharsPerLog) text = text[^MaxCharsPerLog..];
            sb.Append("===== ").Append(name).AppendLine(" =====");
            sb.AppendLine(text.TrimEnd());
        }
        return sb.ToString();
    }

    /// <summary>Ghi ra thư mục tạm (để đính kèm email).</summary>
    public string WriteTemp()
    {
        var path = Path.Combine(Path.GetTempPath(), $"CleanBoost-Diagnostics-{CreatedAt.ToUnixTimeSeconds()}.txt");
        File.WriteAllText(path, Rendered, new UTF8Encoding(false));
        return path;
    }
}

/// <summary>
/// Màn "Gửi báo cáo lỗi" (DiagnosticsView của bản Mac): xem trước báo cáo, lưu ra file hoặc mở ứng dụng email
/// tới địa chỉ hỗ trợ (cấu hình lúc build). Không có gì được gửi đi nếu người dùng không bấm.
/// </summary>
public sealed class DiagnosticsScreen : Grid, IAccentScreen
{
    private readonly AppEnvironment _env;
    private readonly TextBox _text;
    private readonly Border _reportBox;
    private readonly StackPanel _loading;
    private readonly TextBlock _saved = new() { Style = Kit.S("CaptionText"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _save;
    private readonly Button _email;
    private readonly Button _refresh;
    private DiagnosticReport? _report;
    private bool _building;

    public Accent Accent => Accent.Neutral;

    public DiagnosticsScreen(AppEnvironment env)
    {
        _env = env;
        Resources.MergedDictionaries.Add(new ScreenStyles());
        Margin = new Thickness(24);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new DockPanel();
        _refresh = Kit.Glass(Loc.T("Làm mới"), "arrow.clockwise");
        _refresh.VerticalAlignment = VerticalAlignment.Top;
        _refresh.Margin = new Thickness(16, 6, 0, 0);
        _refresh.Click += async (_, _) => await BuildAsync();
        DockPanel.SetDock(_refresh, Dock.Right);
        header.Children.Add(_refresh);
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = Loc.T("Gửi báo cáo lỗi"), Style = Kit.S("TitleText") });
        titles.Children.Add(new TextBlock
        {
            Text = Loc.T("Xem trước nội dung bên dưới. Đường dẫn chi tiết không có trong log ở mức thông thường. Không có gì được gửi đi nếu bạn không bấm."),
            Style = Kit.S("SecondaryBodyText"), Margin = new Thickness(0, 4, 0, 0), LineHeight = 19,
        });
        header.Children.Add(titles);
        Children.Add(header);

        _text = new TextBox { Style = Kit.S("ReportTextBox") };
        _reportBox = new Border
        {
            CornerRadius = new CornerRadius(12), Background = Theme.Frozen(System.Windows.Media.Color.FromArgb(0x4D, 0, 0, 0)), Margin = new Thickness(0, 14, 0, 0),
            Child = _text, Visibility = Visibility.Collapsed, ClipToBounds = true,
        };
        SetRow(_reportBox, 1);
        Children.Add(_reportBox);

        _loading = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _loading.Children.Add(new Spinner { Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Center });
        _loading.Children.Add(new TextBlock { Text = Loc.T("Đang gom thông tin…"), Style = Kit.S("CaptionText"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) });
        SetRow(_loading, 1);
        Children.Add(_loading);

        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        _email = Kit.Primary(Loc.T("Gửi qua email…"));
        _email.Margin = new Thickness(10, 0, 0, 0);
        _email.Click += (_, _) => Email();
        if (AppConfig.SupportEmail is not { Length: > 0 })
        {
            _email.IsEnabled = false;
            ToolTipService.SetShowOnDisabled(_email, true);
            _email.ToolTip = Loc.T("Bản build này chưa cấu hình địa chỉ email hỗ trợ. Hãy lưu báo cáo ra file rồi gửi cho chúng tôi.");
        }
        _save = Kit.Glass(Loc.T("Lưu ra file…"));
        _save.Click += (_, _) => Save();
        DockPanel.SetDock(_email, Dock.Right);
        DockPanel.SetDock(_save, Dock.Right);
        footer.Children.Add(_email);
        footer.Children.Add(_save);
        footer.Children.Add(_saved);
        SetRow(footer, 2);
        Children.Add(footer);
        UpdateButtons();

        // Gom lại khi mở màn (báo cáo cũ hơn 1 phút thì làm mới).
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible && (_report is null || DateTimeOffset.Now - _report.CreatedAt > TimeSpan.FromMinutes(1))) await BuildAsync();
        };
    }

    private void UpdateButtons()
    {
        _save.IsEnabled = _report is not null && !_building;
        _email.IsEnabled = _report is not null && !_building && AppConfig.SupportEmail is { Length: > 0 };
        _refresh.IsEnabled = !_building;
    }

    public async Task BuildAsync()
    {
        if (_building) return;
        _building = true;
        UpdateButtons();
        if (_report is null)
        {
            _loading.Visibility = Visibility.Visible;
            _reportBox.Visibility = Visibility.Collapsed;
        }
        try
        {
            var env = _env;
            _report = await Task.Run(() => DiagnosticReport.Build(env));
            _text.Text = _report.Rendered;
            _text.ScrollToHome();
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.UI, "Không gom được báo cáo chẩn đoán", ex);
            _text.Text = ex.ToString();
        }
        finally
        {
            _building = false;
            _loading.Visibility = Visibility.Collapsed;
            _reportBox.Visibility = Visibility.Visible;
            UpdateButtons();
        }
    }

    private void Save()
    {
        if (_report is null) return;
        var dialog = new SaveFileDialog
        {
            FileName = "CleanBoost-Diagnostics.txt",
            DefaultExt = ".txt",
            Filter = Loc.T("Văn bản") + " (*.txt)|*.txt|" + Loc.T("Mọi file") + " (*.*)|*.*",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, _report.Rendered, new UTF8Encoding(false));
            _saved.Text = Loc.F("Đã lưu: %@", Path.GetFileName(dialog.FileName));
            _saved.ToolTip = dialog.FileName;
            Log.Info(LogCategory.UI, "Đã lưu báo cáo chẩn đoán");
        }
        catch (Exception ex)
        {
            MessageDialog.Show(Window.GetWindow(this), Loc.T("Không lưu được file"), ex.Message, "OK");
        }
    }

    /// <summary>
    /// Windows không có "compose email kèm file" chung như NSSharingService: ghi báo cáo ra thư mục tạm, mở ứng dụng email
    /// bằng <c>mailto:</c> (người nhận, tiêu đề, nội dung) rồi chọn sẵn file trong File Explorer để người dùng kéo vào thư.
    /// </summary>
    private void Email()
    {
        if (_report is null || AppConfig.SupportEmail is not { Length: > 0 } to) return;
        string file;
        try
        {
            file = _report.WriteTemp();
        }
        catch (Exception ex)
        {
            MessageDialog.Show(Window.GetWindow(this), Loc.T("Không lưu được file"), ex.Message, "OK");
            return;
        }
        var subject = Uri.EscapeDataString(Loc.T("Clean Boost — báo cáo lỗi"));
        var body = Uri.EscapeDataString(Loc.T("Báo cáo chẩn đoán đính kèm.") + "\r\n\r\n" + Loc.F("(Hãy đính kèm file %@)", Path.GetFileName(file)));
        var opened = false;
        try
        {
            Process.Start(new ProcessStartInfo($"mailto:{to}?subject={subject}&body={body}") { UseShellExecute = true });
            opened = true;
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory.UI, "Không mở được ứng dụng email: " + ex.Message);
        }
        _saved.Text = Loc.F("Đã lưu: %@", Path.GetFileName(file));
        _saved.ToolTip = file;
        Kit.RevealFile(file);
        MessageDialog.Show(Window.GetWindow(this),
            opened ? Loc.T("Đính kèm báo cáo vào email") : Loc.T("Không mở được ứng dụng email"),
            opened
                ? Loc.F("Đã mở thư mới tới %@. Hãy kéo file %@ (đang được chọn trong File Explorer) vào thư rồi gửi.", to, Path.GetFileName(file))
                : Loc.F("Hãy gửi file %@ (đang được chọn trong File Explorer) tới %@.", Path.GetFileName(file), to),
            "OK");
    }
}
