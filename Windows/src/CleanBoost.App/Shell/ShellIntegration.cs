using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Shell;
using CleanBoost.Core;
using CleanBoost.Engine.Persistence;
using Microsoft.Win32;

namespace CleanBoost.App.Shell;

/// <summary>
/// Tích hợp với Windows (FinderSync + AppIntents + login item của bản Mac). Chỉ ghi vào HKCU nên không cần quyền quản trị:
/// <list type="bullet">
/// <item>URL <c>cleanboost://</c> (<c>HKCU\Software\Classes\cleanboost</c>), luôn đăng ký.</item>
/// <item>Menu chuột phải của File Explorer: "Phân tích bằng Clean Boost" trên thư mục, nền thư mục, ổ đĩa; "Gỡ bằng Clean Boost"
/// trên file .exe/.lnk. Bật/tắt theo <see cref="AppSettings.ExplorerIntegration"/>, chữ theo ngôn ngữ đang chọn.</item>
/// <item>Khởi động cùng Windows: giá trị <c>CleanBoost</c> trong khoá Run = <c>"exe" --background</c> khi
/// <see cref="AppSettings.StartAtLogin"/> và <see cref="AppSettings.MenuBarEnabled"/>.</item>
/// <item>Jump List: Smart Scan, Dọn rác, Space Lens, Giải phóng RAM.</item>
/// </list>
/// Ghi lặp lại không đổi gì (so giá trị trước khi ghi) và luôn trỏ tới exe hiện tại (app bị chuyển chỗ thì cập nhật).
/// Chế độ chụp ảnh kiểm thử (<c>CLEANBOOST_SNAPSHOT</c>) không ghi gì.
/// </summary>
public static class ShellIntegration
{
    public const string RunValueName = "CleanBoost";
    public const string UnregisterArgument = "--unregister-shell";

    private const string ProtocolKey = @"Software\Classes\" + Identifiers.UrlScheme;
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string AnalyzeVerb = "CleanBoost.Analyze";
    private const string UninstallVerb = "CleanBoost.Uninstall";

    /// <summary>Nơi đặt verb "Phân tích bằng Clean Boost": thư mục, nền thư mục đang mở, ổ đĩa.</summary>
    private static readonly string[] AnalyzeParents =
    [
        @"Software\Classes\Directory\shell",
        @"Software\Classes\Directory\Background\shell",
        @"Software\Classes\Drive\shell",
    ];

    /// <summary>Nơi đặt verb "Gỡ bằng Clean Boost": chương trình và lối tắt (theo đuôi file, không phụ thuộc ProgID).</summary>
    private static readonly string[] UninstallParents =
    [
        @"Software\Classes\SystemFileAssociations\.exe\shell",
        @"Software\Classes\SystemFileAssociations\.lnk\shell",
    ];

    private static readonly object s_lock = new();
    private static AppSettings? s_settings;

    /// <summary>
    /// Không đụng registry/Jump List khi chụp ảnh kiểm thử, và ở bản Debug (để bản build thử không tự đăng ký
    /// khởi động cùng Windows trên máy lập trình) trừ khi đặt <c>CLEANBOOST_REGISTER_SHELL=1</c>.
    /// </summary>
    public static bool IsSuspended
    {
        get
        {
            if (Environment.GetEnvironmentVariable("CLEANBOOST_SNAPSHOT") is { Length: > 0 }) return true;
#if DEBUG
            return Environment.GetEnvironmentVariable("CLEANBOOST_REGISTER_SHELL") != "1";
#else
            return false;
#endif
        }
    }

    /// <summary>Đường dẫn CleanBoost.exe đang chạy.</summary>
    public static string ExePath
    {
        get
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path) || !Path.GetFileName(path).Equals(Identifiers.AppExeName, StringComparison.OrdinalIgnoreCase))
                path = Path.Combine(AppContext.BaseDirectory, Identifiers.AppExeName);
            return path;
        }
    }

    // MARK: Vòng đời

    /// <summary>Áp dụng lúc khởi động và theo dõi thay đổi cài đặt. Gọi trên luồng giao diện.</summary>
    public static void Start(AppSettings settings)
    {
        if (IsSuspended)
        {
            Log.Info(LogCategory.UI, "Chế độ chụp ảnh: bỏ qua tích hợp Explorer/khởi động cùng Windows");
            return;
        }
        if (s_settings is not null) return;
        s_settings = settings;
        settings.Changed += OnSettingsChanged;
        _ = Task.Run(() => Apply(settings));
        try
        {
            ApplyJumpList();
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory.UI, "Không tạo được Jump List: " + ex.Message);
        }
    }

    private static void OnSettingsChanged(string key)
    {
        if (s_settings is not { } settings) return;
        switch (key)
        {
            case "startup.atLogin":
                // Bật lại trong Clean Boost thì bỏ trạng thái "Đã tắt" Task Manager đã ghi (chỉ khi người dùng vừa bật).
                var enable = settings.StartAtLogin;
                _ = Task.Run(() =>
                {
                    if (enable) ClearStartupDisabled();
                    Apply(settings);
                });
                break;
            case "explorer.contextMenu":
            case "menuBar.enabled":
            case "app.language":
            case "*":
                _ = Task.Run(() => Apply(settings));
                break;
        }
    }

    /// <summary>Đăng ký/gỡ mọi thứ cho khớp cài đặt hiện tại. Gọi được từ bất kỳ luồng nào.</summary>
    public static void Apply(AppSettings settings)
    {
        if (IsSuspended) return;
        lock (s_lock)
        {
            try
            {
                var exe = ExePath;
                var changed = RegisterProtocol(exe);
                changed |= settings.ExplorerIntegration ? RegisterContextMenu(exe, settings.Language) : UnregisterContextMenu();
                SetStartAtLogin(exe, settings.StartAtLogin && settings.MenuBarEnabled);
                if (changed)
                {
                    NotifyAssociationsChanged();
                    Log.Info(LogCategory.UI, $"Đã cập nhật tích hợp Explorer (menu chuột phải: {(settings.ExplorerIntegration ? "bật" : "tắt")})");
                }
            }
            catch (Exception ex)
            {
                Log.Error(LogCategory.UI, "Không cập nhật được tích hợp Explorer", ex);
            }
        }
    }

    /// <summary>
    /// Gỡ mọi đăng ký theo người dùng (trình gỡ cài đặt gọi <c>CleanBoost.exe --unregister-shell</c>): verb Explorer, URL
    /// <c>cleanboost://</c>, giá trị Run, Jump List.
    /// </summary>
    public static void UnregisterAll()
    {
        lock (s_lock)
        {
            try
            {
                UnregisterContextMenu();
                Registry.CurrentUser.DeleteSubKeyTree(ProtocolKey, throwOnMissingSubKey: false);
                SetStartAtLogin(ExePath, false);
                using (var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedKey, writable: true))
                    approved?.DeleteValue(RunValueName, throwOnMissingValue: false);
                NotifyAssociationsChanged();
            }
            catch (Exception ex)
            {
                Log.Error(LogCategory.UI, "Không gỡ hết được đăng ký Explorer", ex);
            }
        }
        try
        {
            if (Application.Current is { } app)
            {
                var empty = new JumpList();
                JumpList.SetJumpList(app, empty);
                empty.Apply();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory.UI, "Không xoá được Jump List: " + ex.Message);
        }
    }

    // MARK: URL cleanboost://

    private static bool RegisterProtocol(string exe)
    {
        var changed = SetValue(ProtocolKey, null, "URL:Clean Boost");
        changed |= SetValue(ProtocolKey, "URL Protocol", "");
        changed |= SetValue(ProtocolKey + @"\DefaultIcon", null, $"\"{exe}\",0");
        changed |= SetValue(ProtocolKey + @"\shell\open\command", null, $"\"{exe}\" \"%1\"");
        return changed;
    }

    // MARK: Menu chuột phải

    private static bool RegisterContextMenu(string exe, AppLanguage language)
    {
        var changed = false;
        var icon = $"\"{exe}\",0";
        var analyze = Localized("Phân tích bằng Clean Boost", language);
        foreach (var parent in AnalyzeParents)
        {
            var key = parent + "\\" + AnalyzeVerb;
            changed |= SetValue(key, "MUIVerb", analyze);
            changed |= SetValue(key, "Icon", icon);
            changed |= SetValue(key, "MultiSelectModel", "Single");
            // %V: thư mục được chọn, thư mục đang mở (nền) hoặc gốc ổ.
            changed |= SetValue(key + @"\command", null, $"\"{exe}\" --analyze \"%V\"");
        }
        var uninstall = Localized("Gỡ bằng Clean Boost", language);
        foreach (var parent in UninstallParents)
        {
            var key = parent + "\\" + UninstallVerb;
            changed |= SetValue(key, "MUIVerb", uninstall);
            changed |= SetValue(key, "Icon", icon);
            changed |= SetValue(key, "MultiSelectModel", "Single");
            changed |= SetValue(key + @"\command", null, $"\"{exe}\" --uninstall \"%1\"");
        }
        return changed;
    }

    private static bool UnregisterContextMenu()
    {
        var changed = false;
        foreach (var key in AnalyzeParents.Select(p => p + "\\" + AnalyzeVerb).Concat(UninstallParents.Select(p => p + "\\" + UninstallVerb)))
        {
            using (var existing = Registry.CurrentUser.OpenSubKey(key))
            {
                if (existing is null) continue;
            }
            Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
            changed = true;
        }
        if (changed)
        {
            // Dọn các khoá cha rỗng do Clean Boost tạo (vd Drive\shell, SystemFileAssociations\.exe\shell).
            foreach (var parent in AnalyzeParents.Concat(UninstallParents))
            {
                DeleteIfEmpty(parent);
                DeleteIfEmpty(parent[..parent.LastIndexOf('\\')]);
            }
        }
        return changed;
    }

    /// <summary>Xoá khoá nếu không còn giá trị hay khoá con nào.</summary>
    private static void DeleteIfEmpty(string subKey)
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(subKey))
            {
                if (key is null || key.SubKeyCount > 0 || key.ValueCount > 0) return;
            }
            Registry.CurrentUser.DeleteSubKey(subKey, throwOnMissingSubKey: false);
        }
        catch
        {
            // khoá đang được dùng hoặc không có quyền: để nguyên
        }
    }

    /// <summary>Chữ của verb theo ngôn ngữ người dùng chọn (kể cả khi chưa khởi động lại để đổi ngôn ngữ giao diện).</summary>
    private static string Localized(string key, AppLanguage language)
    {
        var english = language switch
        {
            AppLanguage.English => true,
            AppLanguage.Vietnamese => false,
            _ => !CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("vi", StringComparison.OrdinalIgnoreCase),
        };
        return english ? Loc.LookupEnglish(key) : key;
    }

    // MARK: Khởi động cùng Windows

    private static void SetStartAtLogin(string exe, bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        var command = $"\"{exe}\" --background";
        if (enabled)
        {
            if (run.GetValue(RunValueName) as string != command)
            {
                run.SetValue(RunValueName, command, RegistryValueKind.String);
                Log.Info(LogCategory.UI, "Đã bật khởi động cùng Windows");
            }
        }
        else if (run.GetValue(RunValueName) is not null)
        {
            run.DeleteValue(RunValueName, throwOnMissingValue: false);
            Log.Info(LogCategory.UI, "Đã tắt khởi động cùng Windows");
        }
    }

    /// <summary>Task Manager tắt mục khởi động bằng cờ trong StartupApproved (byte đầu lẻ = tắt); xoá cờ đó để bật lại.</summary>
    private static void ClearStartupDisabled()
    {
        try
        {
            using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedKey, writable: true);
            if (approved?.GetValue(RunValueName) is byte[] { Length: > 0 } flags && (flags[0] & 1) == 1)
                approved.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory.UI, "Không bật lại được mục khởi động: " + ex.Message);
        }
    }

    // MARK: Jump List

    private static void ApplyJumpList()
    {
        if (Application.Current is not { } app) return;
        var exe = ExePath;
        JumpTask MakeTask(string title, string description, string arguments) => new()
        {
            Title = title,
            Description = description,
            ApplicationPath = exe,
            Arguments = arguments,
            IconResourcePath = exe,
            IconResourceIndex = 0,
        };
        var list = new JumpList { ShowFrequentCategory = false, ShowRecentCategory = false };
        list.JumpItems.Add(MakeTask("Smart Scan", Loc.T("Mở Clean Boost và chạy Smart Scan."), "--smart-scan"));
        list.JumpItems.Add(MakeTask(Loc.T("Dọn rác"), Loc.T("Mở Clean Boost và quét rác hệ thống."), "--clean-junk"));
        list.JumpItems.Add(MakeTask("Space Lens", Loc.T("Xem thư mục nào đang chiếm nhiều dung lượng."), Identifiers.UrlScheme + "://scan?feature=" + FeatureIDs.SpaceLens));
        list.JumpItems.Add(MakeTask(Loc.T("Giải phóng RAM"), Loc.T("Dọn bộ nhớ đang bị chiếm giữ."), Identifiers.UrlScheme + "://maintenance?task=freeRAM"));
        JumpList.SetJumpList(app, list);
        list.Apply();
    }

    // MARK: Registry

    /// <summary>Ghi giá trị chuỗi nếu khác giá trị đang có; trả về có thay đổi không.</summary>
    private static bool SetValue(string subKey, string? name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true);
        if (key.GetValue(name) is string current && current == value && key.GetValueKind(name ?? "") == RegistryValueKind.String) return false;
        key.SetValue(name, value, RegistryValueKind.String);
        return true;
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    /// <summary>Báo Explorer đọc lại liên kết (SHCNE_ASSOCCHANGED).</summary>
    private static void NotifyAssociationsChanged()
    {
        try
        {
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // không quan trọng
        }
    }
}
