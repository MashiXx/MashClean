using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using CleanBoost.Core;

namespace CleanBoost.Features.Uninstaller;

/// <summary>
/// Đọc đích của lối tắt <c>.lnk</c> bằng <c>IShellLinkW</c> (CLSID_ShellLink). Không gọi <c>Resolve</c> nên không bao giờ
/// tìm kiếm trên ổ hay mạng, không hiện giao diện; chỉ đọc đường dẫn ghi trong file.
/// </summary>
public static class ShellLink
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    private const int STGM_READ = 0;
    private const uint SLGP_RAWPATH = 0x4;

    /// <summary>Thông tin đọc được từ một lối tắt.</summary>
    public sealed record Info(string? Target, string? Arguments, string? IconPath);

    /// <summary>Đích của lối tắt (đã mở rộng biến môi trường), hoặc null nếu không đọc được / lối tắt "quảng cáo" của MSI.</summary>
    public static string? Target(string lnkPath) => Read(lnkPath)?.Target;

    public static Info? Read(string lnkPath)
    {
        if (!lnkPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || !File.Exists(lnkPath)) return null;
        object? obj = null;
        try
        {
            obj = new CShellLink();
            ((IPersistFile)obj).Load(lnkPath, STGM_READ);
            var link = (IShellLinkW)obj;
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, SLGP_RAWPATH);
            var raw = sb.ToString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                sb.Clear();
                link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                raw = sb.ToString();
            }
            var args = new StringBuilder(2048);
            try { link.GetArguments(args, args.Capacity); } catch { args.Clear(); }
            var icon = new StringBuilder(1024);
            try { link.GetIconLocation(icon, icon.Capacity, out _); } catch { icon.Clear(); }
            string? Clean(string s) => string.IsNullOrWhiteSpace(s) ? null : Environment.ExpandEnvironmentVariables(s.Trim().Trim('"'));
            return new Info(Clean(raw), args.Length == 0 ? null : args.ToString(), Clean(icon.ToString()));
        }
        catch (Exception ex)
        {
            Log.DebugPath(LogCategory.Scan, "Không đọc được lối tắt: " + ex.Message, lnkPath);
            return null;
        }
        finally
        {
            if (obj is not null && Marshal.IsComObject(obj)) Marshal.FinalReleaseComObject(obj);
        }
    }
}
