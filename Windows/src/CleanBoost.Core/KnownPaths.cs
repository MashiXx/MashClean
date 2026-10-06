using System.Runtime.InteropServices;
using CleanBoost.Core.Native;

namespace CleanBoost.Core;

/// <summary>Các thư mục chuẩn của Windows, chuẩn hoá về đường dẫn dài (không dùng tên 8.3).</summary>
public static class KnownPaths
{
    public static string UserHome { get; } = Long(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    public static string LocalAppData { get; } = Long(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    public static string RoamingAppData { get; } = Long(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
    public static string LocalLowAppData { get; } = KnownFolder(new Guid("A520A1A4-1780-4FF6-BD18-167343C5AF16")) ?? Path.Combine(UserHome, "AppData", "LocalLow");
    public static string ProgramData { get; } = Long(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
    public static string Windows { get; } = Long(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    public static string ProgramFiles { get; } = Long(Environment.GetEnvironmentVariable("ProgramW6432") is { Length: > 0 } pf ? pf : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
    public static string ProgramFilesX86 { get; } = Long(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
    public static string SystemDrive { get; } = (Environment.GetEnvironmentVariable("SystemDrive") is { Length: 2 } d ? d : Path.GetPathRoot(Windows)!.TrimEnd('\\')).ToUpperInvariant();
    public static string UsersRoot { get; } = Path.GetDirectoryName(UserHome) ?? SystemDrive + @"\Users";
    public static string PublicProfile { get; } = Long(Environment.GetEnvironmentVariable("PUBLIC") ?? Path.Combine(UsersRoot, "Public"));
    /// <summary>
    /// Thư mục tạm của người dùng, đã kiểm tra: biến TMP/TEMP cấu hình sai (trỏ vào gốc ổ, thư mục home, Program Files…
    /// hoặc tên không giống thư mục tạm) thì dùng <c>%LOCALAPPDATA%\Temp</c>, để rule "tệp tạm" không bao giờ nhắm vào dữ liệu thật.
    /// </summary>
    public static string Temp { get; } = ValidatedTemp(Long(Path.GetTempPath()).TrimEnd('\\'));

    internal static string ValidatedTemp(string candidate)
    {
        var fallback = Path.Combine(LocalAppData, "Temp");
        if (string.IsNullOrEmpty(candidate) || candidate.Length <= 3) return fallback;
        var leaf = Path.GetFileName(candidate);
        if (!(leaf.Contains("temp", StringComparison.OrdinalIgnoreCase) || leaf.Contains("tmp", StringComparison.OrdinalIgnoreCase))) return fallback;
        string[] critical = [UserHome, UsersRoot, Windows, ProgramFiles, ProgramFilesX86, ProgramData, LocalAppData, RoamingAppData];
        foreach (var c in critical.Concat(UserKnownFoldersRaw()))
        {
            if (string.IsNullOrEmpty(c)) continue;
            // Chính nó hoặc thư mục tổ tiên của một thư mục quan trọng thì không phải thư mục tạm.
            if (candidate.Equals(c.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || PathPolicy.IsSameOrInside(c, candidate)) return fallback;
        }
        return candidate;
    }

    private static IEnumerable<string> UserKnownFoldersRaw() =>
        [Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
         Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
         Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)];
    public static string Downloads { get; } = KnownFolder(new Guid("374DE290-123F-4565-9164-39C4925E467B")) ?? Path.Combine(UserHome, "Downloads");
    public static string Documents { get; } = Long(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
    public static string Desktop { get; } = Long(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
    public static string Pictures { get; } = Long(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
    public static string Videos { get; } = Long(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
    public static string Music { get; } = Long(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
    public static string StartMenuUser { get; } = Long(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu));
    public static string StartMenuCommon { get; } = Long(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu));
    public static string StartupUser { get; } = Long(Environment.GetFolderPath(Environment.SpecialFolder.Startup));
    public static string StartupCommon { get; } = Long(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup));

    /// <summary>Thư mục dữ liệu riêng của Clean Boost: <c>%LOCALAPPDATA%\CleanBoost</c>.</summary>
    public static string AppData { get; } = Path.Combine(LocalAppData, "CleanBoost");

    /// <summary>Các thư mục người dùng thực tế (có thể đã chuyển sang ổ khác), dùng cho PathPolicy.</summary>
    public static IReadOnlyList<string> UserKnownFolders { get; } =
        new[] { Documents, Desktop, Pictures, Videos, Music, Downloads }.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>
    /// Đổi tên 8.3 (vd <c>C:\Users\MASHI~1</c>) thành tên dài, và lấy đúng hoa thường thật trên ổ
    /// (<c>C:\WINDOWS</c> → <c>C:\Windows</c>) để hiển thị gọn gàng.
    /// </summary>
    public static string Long(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var p = path;
        if (p.Contains('~'))
        {
            var buffer = new char[1024];
            var n = Win32.GetLongPathNameW(p, buffer, (uint)buffer.Length);
            if (n > 0 && n < buffer.Length) p = new string(buffer, 0, (int)n);
        }
        var trimmed = p.TrimEnd('\\');
        // Chỉ nhận kết quả khi trùng đường dẫn (không phải đích của junction/symlink).
        if (trimmed.Length > 2 && PathPolicy.FinalPath(trimmed) is { } real && real.Equals(trimmed, StringComparison.OrdinalIgnoreCase)) return real;
        return p;
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    public static string? KnownFolder(Guid id)
    {
        try
        {
            if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var ptr) != 0) return null;
            try { return Long(Marshal.PtrToStringUni(ptr) ?? ""); } finally { Marshal.FreeCoTaskMem(ptr); }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Rút gọn đường dẫn trong thư mục người dùng thành <c>~\...</c> để hiển thị.</summary>
    public static string AbbreviatingHome(this string path)
    {
        if (path.Equals(UserHome, StringComparison.OrdinalIgnoreCase)) return "~";
        if (path.StartsWith(UserHome + "\\", StringComparison.OrdinalIgnoreCase)) return "~" + path[UserHome.Length..];
        return path;
    }

    /// <summary>Mở rộng <c>~</c> đầu chuỗi.</summary>
    public static string ExpandingTilde(this string path)
    {
        if (path == "~") return UserHome;
        if (path.StartsWith("~\\", StringComparison.Ordinal) || path.StartsWith("~/", StringComparison.Ordinal)) return UserHome + "\\" + path[2..].Replace('/', '\\');
        return path;
    }
}
