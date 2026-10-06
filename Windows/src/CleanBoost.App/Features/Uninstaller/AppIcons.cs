using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CleanBoost.App.UI;
using CleanBoost.Core;
using CleanBoost.Engine.Apps;
using CleanBoost.Engine.Rules;
using CleanBoost.Features.Uninstaller;
using Windows.Management.Deployment;

namespace CleanBoost.App.Features.Uninstaller;

/// <summary>
/// Biểu tượng thật của app (NSWorkspace.icon(forFile:) của bản Mac): <c>DisplayIcon</c> (exe/dll kèm chỉ số, hoặc .ico),
/// exe chính trong thư mục cài, trình gỡ; app Store lấy logo của gói. Ảnh đã Freeze nên tải được ở luồng nền.
/// </summary>
public static class AppIcons
{
    private static readonly ConcurrentDictionary<string, ImageSource?> s_cache = new(StringComparer.OrdinalIgnoreCase);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIconsW(string szFileName, int nIconIndex, int cxIcon, int cyIcon, IntPtr[] phicon, uint[] piconid, uint nIcons, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    public static ImageSource? For(InstalledApp app, int size = 48) =>
        s_cache.GetOrAdd(app.Id + "|" + size, _ => Load(app, size));

    private static ImageSource? Load(InstalledApp app, int size)
    {
        try
        {
            if (app.IsStore) return StoreLogo(app, size) ?? Generic;
            foreach (var (path, index) in Candidates(app))
            {
                if (FromFile(path, index, size) is { } img) return img;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory.UI, "Không tải được biểu tượng app: " + ex.Message);
        }
        return Generic;
    }

    private static IEnumerable<(string Path, int Index)> Candidates(InstalledApp app)
    {
        if (!string.IsNullOrWhiteSpace(app.DisplayIcon))
        {
            var raw = Environment.ExpandEnvironmentVariables(app.DisplayIcon.Trim());
            var index = 0;
            string path;
            if (raw.StartsWith('"'))
            {
                var end = raw.IndexOf('"', 1);
                path = end > 1 ? raw[1..end] : raw.Trim('"');
                var rest = end > 0 ? raw[(end + 1)..] : "";
                if (rest.TrimStart().StartsWith(',') && int.TryParse(rest.TrimStart()[1..].Trim(), out var i)) index = i;
            }
            else
            {
                var comma = raw.LastIndexOf(',');
                if (comma > 2 && int.TryParse(raw[(comma + 1)..].Trim(), out var i))
                {
                    path = raw[..comma].Trim();
                    index = i;
                }
                else
                {
                    path = raw;
                }
            }
            path = path.Trim('"');
            if (Path.IsPathFullyQualified(path) && File.Exists(path)) yield return (path, index);
        }
        if (app.IsWindowsInstaller && MsiProductIcon(app.AppID) is { } msiIcon) yield return (msiIcon, 0);
        if (app.InstallLocation is { Length: > 3 } dir && Directory.Exists(dir))
        {
            foreach (var exe in app.ExecutableNames)
            {
                var p = Path.Combine(dir, exe + ".exe");
                if (File.Exists(p)) yield return (p, 0);
            }
            if (MainExecutable(app, dir) is { } main) yield return (main, 0);
        }
        else if (RuleEvaluator.IsSafeComponent(app.Name))
        {
            // Gói MSI thường không ghi InstallLocation: thử "Program Files\<Tên app>".
            foreach (var root in new[] { KnownPaths.ProgramFiles, KnownPaths.ProgramFilesX86 })
            {
                var guess = Path.Combine(root, app.Name);
                if (Directory.Exists(guess) && MainExecutable(app, guess) is { } main) yield return (main, 0);
            }
        }
        if (UninstallCommand.Split(app.UninstallString ?? "") is { } u && Path.IsPathFullyQualified(u.Exe) && File.Exists(u.Exe)
            && !Path.GetFileName(u.Exe).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
            yield return (u.Exe, 0);
    }

    /// <summary>
    /// Biểu tượng sản phẩm của gói MSI (ARPPRODUCTICON): <c>HKLM\SOFTWARE\Classes\Installer\Products\&lt;mã đóng gói&gt;\ProductIcon</c>.
    /// Mã đóng gói = GUID đảo từng nhóm (8, 4, 4 ký tự đảo ngược; 16 ký tự cuối đảo từng cặp).
    /// </summary>
    private static string? MsiProductIcon(string productCode)
    {
        if (!Guid.TryParse(productCode, out var guid)) return null;
        var g = guid.ToString("N").ToUpperInvariant();
        static string Rev(string s) => new(s.Reverse().ToArray());
        var packed = Rev(g[..8]) + Rev(g[8..12]) + Rev(g[12..16]);
        for (var i = 16; i < 32; i += 2) packed += $"{g[i + 1]}{g[i]}";
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes\Installer\Products\" + packed);
            if (key?.GetValue("ProductIcon") is not string icon) return null;
            var path = Environment.ExpandEnvironmentVariables(icon.Trim().Trim('"')).Split(',')[0];
            return Path.IsPathFullyQualified(path) && File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Exe chính trong thư mục cài (và thư mục con <c>bin</c>): trùng tên app trước, không thì exe lớn nhất không phải trình gỡ,
    /// trình cập nhật hay trình báo lỗi.
    /// </summary>
    private static string? MainExecutable(InstalledApp app, string dir)
    {
        var keys = LeftoverMatcher.NameKeys(app);
        string[] skip = ["unins", "uninst", "update", "crash", "helper", "setup", "install", "elevat", "notif", "report", "launcher", "service"];
        var exes = new List<(string Path, long Size)>();
        foreach (var folder in new[] { dir, Path.Combine(dir, "bin") }.Where(Directory.Exists))
        {
            try
            {
                foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*.exe")) exes.Add((f.FullName, f.Length));
            }
            catch
            {
                // không đọc được thư mục
            }
        }
        var named = exes.Select(e => (e.Path, Name: LeftoverMatcher.Normalize(Path.GetFileNameWithoutExtension(e.Path)))).Where(e => e.Name.Length >= 3).ToList();
        // Trùng tên chính xác trước ("calibre.exe"), rồi mới tới tiền tố ("calibre-debug.exe" là công cụ dòng lệnh).
        if (named.FirstOrDefault(e => keys.Contains(e.Name)) is { Path: not null } exact) return exact.Path;
        if (named.Where(e => keys.Any(k => k.StartsWith(e.Name, StringComparison.Ordinal) || e.Name.StartsWith(k, StringComparison.Ordinal)))
                .OrderBy(e => e.Name.Length).FirstOrDefault() is { Path: not null } prefix) return prefix.Path;
        return exes.Where(e => !skip.Any(s => Path.GetFileName(e.Path).Contains(s, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(e => e.Size).Select(e => e.Path).FirstOrDefault();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHSTOCKICONINFO
    {
        public uint cbSize;
        public IntPtr hIcon;
        public int iSysImageIndex;
        public int iIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szPath;
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetStockIconInfo(uint siid, uint uFlags, ref SHSTOCKICONINFO psii);

    /// <summary>Biểu tượng "chương trình" mặc định của Windows (SIID_APPLICATION) khi app không có biểu tượng riêng.</summary>
    public static ImageSource? Generic => s_generic.Value;

    private static readonly Lazy<ImageSource?> s_generic = new(() =>
    {
        try
        {
            const uint SIID_APPLICATION = 2, SHGSI_ICON = 0x100, SHGSI_LARGEICON = 0x0;
            var info = new SHSTOCKICONINFO { cbSize = (uint)Marshal.SizeOf<SHSTOCKICONINFO>() };
            if (SHGetStockIconInfo(SIID_APPLICATION, SHGSI_ICON | SHGSI_LARGEICON, ref info) != 0 || info.hIcon == IntPtr.Zero) return null;
            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
        catch
        {
            return null;
        }
    });

    private static ImageSource? FromFile(string path, int index, int size)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".ico")
        {
            try
            {
                var decoder = new IconBitmapDecoder(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames.OrderBy(f => Math.Abs(f.PixelWidth - size)).ThenByDescending(f => f.Format.BitsPerPixel).FirstOrDefault();
                if (frame is not null)
                {
                    frame.Freeze();
                    return frame;
                }
            }
            catch
            {
                // .ico hỏng: thử cách khác
            }
        }
        if (ext is ".exe" or ".dll" or ".ico")
        {
            var icons = new IntPtr[1];
            var ids = new uint[1];
            var n = PrivateExtractIconsW(path, index, size, size, icons, ids, 1, 0);
            if (n == 0 || n == uint.MaxValue || icons[0] == IntPtr.Zero)
            {
                if (index != 0) n = PrivateExtractIconsW(path, 0, size, size, icons, ids, 1, 0);
                // File không có biểu tượng riêng: thử ứng viên kế tiếp (không lấy biểu tượng "tài liệu" chung của shell).
                if (n == 0 || n == uint.MaxValue || icons[0] == IntPtr.Zero) return null;
            }
            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(icons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DestroyIcon(icons[0]);
            }
        }
        if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp")
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = size * 2;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>Logo của gói Store: file logo trong manifest, chọn bản theo tỉ lệ gần nhất (StoreLogo.scale-200.png...).</summary>
    private static ImageSource? StoreLogo(InstalledApp app, int size)
    {
        if (string.IsNullOrEmpty(app.PackageFullName)) return null;
        var package = new PackageManager().FindPackageForUser(string.Empty, app.PackageFullName);
        Uri? logo = null;
        try { logo = package?.Logo; } catch { logo = null; }
        if (logo is null || !logo.IsFile) return null;
        var path = logo.LocalPath;
        if (!File.Exists(path))
        {
            var dir = Path.GetDirectoryName(path);
            var stem = Path.GetFileNameWithoutExtension(path);
            if (dir is null || !Directory.Exists(dir)) return null;
            var files = Directory.GetFiles(dir, stem + ".*" + Path.GetExtension(path));
            path = files.OrderBy(f => f.Contains("scale-200", StringComparison.OrdinalIgnoreCase) ? 0 : f.Contains("scale-100", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                .ThenBy(f => f.Contains("contrast", StringComparison.OrdinalIgnoreCase) ? 1 : 0).FirstOrDefault() ?? "";
            if (path.Length == 0) return null;
        }
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.DecodePixelWidth = size * 2;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}

/// <summary>Thời gian tương đối kiểu ngắn ("3 tuần trước" / "3 wk. ago"), như RelativeDateTimeFormatter của bản Mac.</summary>
public static class RelativeTime
{
    public static string Format(DateTime when, DateTime? now = null)
    {
        var span = (now ?? DateTime.Now) - when;
        if (span < TimeSpan.FromMinutes(1)) return Loc.T("vừa xong");
        if (span < TimeSpan.FromHours(1)) return Loc.F("%lld phút trước", (long)span.TotalMinutes);
        if (span < TimeSpan.FromDays(1)) return Loc.F("%lld giờ trước", (long)span.TotalHours);
        if (span < TimeSpan.FromDays(2)) return Loc.T("hôm qua");
        if (span < TimeSpan.FromDays(7)) return Loc.F("%lld ngày trước", (long)span.TotalDays);
        if (span < TimeSpan.FromDays(30)) return Loc.F("%lld tuần trước", (long)(span.TotalDays / 7));
        if (span < TimeSpan.FromDays(365)) return Loc.F("%lld tháng trước", Math.Max(1, (long)(span.TotalDays / 30)));
        return Loc.F("%lld năm trước", (long)(span.TotalDays / 365));
    }
}
