using System.Runtime.InteropServices;
using System.Text;

namespace CleanBoost.Features.StartupItems;

/// <summary>
/// Đọc lối tắt <c>.lnk</c> bằng <c>IShellLinkW</c> (không gọi <c>Resolve</c> để không bị shell đi tìm file đã di chuyển).
/// Lối tắt "quảng cáo" của Windows Installer (Darwin ID) không có đường dẫn đích thật: trả về <see cref="Info.IsAdvertised"/>.
/// </summary>
public static class ShellLink
{
    public sealed record Info(string? TargetPath, string Arguments, string? WorkingDirectory, bool IsAdvertised);

    private const uint STGM_READ = 0;
    private const uint SLGP_RAWPATH = 0x4;
    private const uint SLDF_HAS_DARWINID = 0x1000;

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
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

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile(out IntPtr ppszFileName);
    }

    [ComImport, Guid("45e2b4ae-b1c3-11d0-b92f-00a0c90312e1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkDataList
    {
        void AddDataBlock(IntPtr pDataBlock);
        void CopyDataBlock(uint dwSig, out IntPtr ppDataBlock);
        void RemoveDataBlock(uint dwSig);
        void GetFlags(out uint pdwFlags);
        void SetFlags(uint dwFlags);
    }

    /// <summary>Đọc đích, tham số và thư mục làm việc của lối tắt; <c>null</c> nếu không đọc được.</summary>
    public static Info? Read(string path)
    {
        object? link = null;
        try
        {
            link = new ShellLinkCoClass();
            ((IPersistFile)link).Load(path, STGM_READ);
            var shell = (IShellLinkW)link;
            var advertised = false;
            try
            {
                ((IShellLinkDataList)link).GetFlags(out var flags);
                advertised = (flags & SLDF_HAS_DARWINID) != 0;
            }
            catch
            {
                // không có IShellLinkDataList: bỏ qua
            }
            var sb = new StringBuilder(1024);
            shell.GetPath(sb, sb.Capacity, IntPtr.Zero, SLGP_RAWPATH);
            var target = sb.ToString();
            if (string.IsNullOrWhiteSpace(target))
            {
                sb.Clear();
                shell.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                target = sb.ToString();
            }
            sb.Clear();
            shell.GetArguments(sb, sb.Capacity);
            var args = sb.ToString();
            sb.Clear();
            shell.GetWorkingDirectory(sb, sb.Capacity);
            var dir = sb.ToString();
            return new Info(string.IsNullOrWhiteSpace(target) ? null : Environment.ExpandEnvironmentVariables(target.Trim()), args.Trim(),
                string.IsNullOrWhiteSpace(dir) ? null : Environment.ExpandEnvironmentVariables(dir.Trim()), advertised);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (link is not null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link);
        }
    }
}
