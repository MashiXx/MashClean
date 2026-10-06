using System.Runtime.InteropServices;
using CleanBoost.Core;
using CleanBoost.Core.Native;

namespace CleanBoost.Features.StartupItems;

/// <summary>
/// Giải lệnh của mục khởi động ra chương trình đích (tương đương <c>LaunchdJob.executablePath</c> của bản Mac): xử lý dấu ngoặc
/// kép, đường dẫn có khoảng trắng không được bọc ngoặc (cách <c>CreateProcess</c> thử từng tiền tố), biến môi trường, tên
/// chương trình không có thư mục (tìm trong System32/Windows/PATH) và các trình bọc như <c>rundll32</c>, <c>cmd /c</c>,
/// <c>wscript</c>, <c>powershell -File</c>.
/// </summary>
public static class StartupCommand
{
    /// <summary>Kết quả giải lệnh: chương trình đích, có tồn tại không (<c>null</c> = không xác định), có nằm trên ổ cố định không.</summary>
    public sealed record Resolution(string? Target, bool? Exists, bool OnFixedDrive)
    {
        public static readonly Resolution Unknown = new(null, null, false);
    }

    private static readonly string[] s_executableExtensions = [".exe", ".com", ".bat", ".cmd", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".ps1", ".scr", ".msc", ".lnk", ".pif", ".dll", ".cpl"];

    /// <summary>Giải lệnh đầy đủ (vd <c>"C:\a b\x.exe" --min</c>), có thể kèm thư mục làm việc (tác vụ lập lịch).</summary>
    public static Resolution Resolve(string? command, string? workingDirectory = null)
    {
        // Lệnh rỗng: Windows không chạy gì cả → mục chắc chắn hỏng.
        if (string.IsNullOrWhiteSpace(command)) return new Resolution(null, false, true);
        var expanded = Environment.ExpandEnvironmentVariables(command.Trim());
        var (exe, args) = Split(expanded, workingDirectory);
        if (string.IsNullOrEmpty(exe)) return new Resolution(null, false, true);
        return ResolveProgram(exe, args, workingDirectory, depth: 0);
    }

    /// <summary>Giải một chương trình + tham số (lối tắt .lnk: đích và tham số đã tách sẵn).</summary>
    public static Resolution Resolve(string program, string? arguments, string? workingDirectory)
    {
        var exe = Environment.ExpandEnvironmentVariables(program.Trim().Trim('"'));
        var args = Environment.ExpandEnvironmentVariables(arguments ?? "").Trim();
        if (exe.Length == 0) return Resolution.Unknown;
        return ResolveProgram(exe, args, workingDirectory, depth: 0);
    }

    private static Resolution ResolveProgram(string exe, string args, string? workingDirectory, int depth)
    {
        var full = Locate(exe, workingDirectory);
        if (full is null) return Resolution.Unknown;

        // Trình bọc: chương trình thật nằm trong tham số.
        if (depth < 2 && Unwrap(full, args) is { } inner)
        {
            var (innerExe, innerArgs) = inner;
            var nested = ResolveProgram(innerExe, innerArgs, workingDirectory, depth + 1);
            if (nested.Target is not null) return nested;
        }
        return Check(full);
    }

    /// <summary>Kiểm tra sự tồn tại của chương trình đích (ba trạng thái) và loại ổ.</summary>
    public static Resolution Check(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return new Resolution(path, null, false);
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return new Resolution(path, null, false);
        var root = Path.GetPathRoot(path);
        var driveType = string.IsNullOrEmpty(root) ? 0u : Win32.GetDriveTypeW(root);
        // Ổ mạng/ổ quang: không chạm vào (có thể treo hoặc đang vắng mặt) — coi như không xác định.
        if (driveType is Win32.DRIVE_REMOTE or Win32.DRIVE_CDROM) return new Resolution(path, null, false);
        var fixedDrive = driveType is Win32.DRIVE_FIXED or Win32.DRIVE_RAMDISK;
        var state = Exists(path);
        if (state == false)
        {
            // Trước khi kết luận "hỏng", thử các vị trí Windows tự chuyển hướng (app 32-bit, System32 ↔ SysWOW64).
            foreach (var alt in Alternates(path))
            {
                var a = Exists(alt);
                if (a == true) return new Resolution(path, true, fixedDrive);
                if (a is null) state = null;
            }
        }
        return new Resolution(path, state, fixedDrive);
    }

    /// <summary>
    /// Tồn tại hay không theo mã lỗi của <c>GetFileAttributesEx</c>: chỉ "không tìm thấy file/thư mục" mới là <c>false</c>;
    /// bị từ chối quyền, ổ chưa sẵn sàng, tên lạ... là <c>null</c> (không đủ chắc để gọi là hỏng).
    /// </summary>
    public static bool? Exists(string path)
    {
        try
        {
            if (Win32.GetFileAttributesExW(LongPath.Prefix(path), 0, out _)) return true;
            var err = Marshal.GetLastWin32Error();
            return err is Win32.ERROR_FILE_NOT_FOUND or Win32.ERROR_PATH_NOT_FOUND ? false : null;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> Alternates(string path)
    {
        var win = KnownPaths.Windows;
        string[][] pairs =
        [
            [win + @"\System32\", win + @"\SysWOW64\"],
            [win + @"\Sysnative\", win + @"\System32\"],
            [KnownPaths.ProgramFiles + @"\", KnownPaths.ProgramFilesX86 + @"\"],
            [KnownPaths.ProgramFilesX86 + @"\", KnownPaths.ProgramFiles + @"\"],
        ];
        foreach (var pair in pairs)
        {
            if (path.StartsWith(pair[0], StringComparison.OrdinalIgnoreCase)) yield return pair[1] + path[pair[0].Length..];
        }
    }

    // MARK: Tách chương trình và tham số

    /// <summary>
    /// Tách token chương trình khỏi tham số. Có ngoặc kép thì lấy phần trong ngoặc; không có thì thử từng tiền tố kết thúc
    /// trước một khoảng trắng như <c>CreateProcess</c> (<c>C:\Program Files\A\a.exe -x</c>), rồi đến tiền tố có đuôi chạy được.
    /// </summary>
    public static (string Exe, string Args) Split(string commandLine, string? workingDirectory = null)
    {
        var s = commandLine.Trim();
        if (s.Length == 0) return ("", "");
        if (s[0] == '"')
        {
            var end = s.IndexOf('"', 1);
            return end < 0 ? (s[1..].Trim(), "") : (s[1..end].Trim(), s[(end + 1)..].Trim());
        }
        var spaces = new List<int>();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] is ' ' or '\t') spaces.Add(i);
        }
        if (spaces.Count == 0) return (s, "");
        // Chỉ thử tiền tố có khoảng trắng khi trông như đường dẫn tuyệt đối (tránh dò ổ đĩa với lệnh kiểu "foo bar").
        if (Path.IsPathFullyQualified(s[..spaces[0]]) || (s.Length > 2 && s[1] == ':'))
        {
            foreach (var i in spaces.Append(s.Length))
            {
                var candidate = s[..i];
                if (Exists(candidate) == true && !Directory.Exists(candidate)) return (candidate, s[Math.Min(i, s.Length)..].Trim());
                if (!Path.HasExtension(candidate) && Exists(candidate + ".exe") == true) return (candidate + ".exe", s[Math.Min(i, s.Length)..].Trim());
            }
            foreach (var i in spaces.Append(s.Length))
            {
                var candidate = s[..i];
                if (s_executableExtensions.Any(e => candidate.EndsWith(e, StringComparison.OrdinalIgnoreCase))) return (candidate, s[Math.Min(i, s.Length)..].Trim());
            }
        }
        return (s[..spaces[0]], s[spaces[0]..].Trim());
    }

    /// <summary>Đường dẫn đầy đủ của chương trình: tuyệt đối thì giữ, tương đối thì tìm theo thư mục làm việc, System32, Windows, PATH.</summary>
    public static string? Locate(string exe, string? workingDirectory = null)
    {
        var e = exe.Trim().Trim('"');
        if (e.Length == 0) return null;
        if (Path.IsPathFullyQualified(e)) return e;
        if (e.Contains(':') || e.StartsWith('\\')) return null;
        var names = Path.HasExtension(e) ? new[] { e } : new[] { e + ".exe", e + ".com", e + ".bat", e + ".cmd", e };
        var dirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(workingDirectory)) dirs.Add(Environment.ExpandEnvironmentVariables(workingDirectory.Trim('"')));
        dirs.Add(Path.Combine(KnownPaths.Windows, "System32"));
        dirs.Add(KnownPaths.Windows);
        dirs.Add(Path.Combine(KnownPaths.Windows, "System32", "WindowsPowerShell", "v1.0"));
        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var d = Environment.ExpandEnvironmentVariables(p.Trim('"'));
            if (Path.IsPathFullyQualified(d) && !d.StartsWith(@"\\", StringComparison.Ordinal)) dirs.Add(d);
        }
        foreach (var dir in dirs)
        {
            foreach (var n in names)
            {
                string candidate;
                try { candidate = Path.GetFullPath(Path.Combine(dir, n)); } catch { continue; }
                if (Exists(candidate) == true) return candidate;
            }
        }
        return null;
    }

    // MARK: Trình bọc

    /// <summary>Chương trình thật bên trong một trình bọc (rundll32, cmd /c, wscript/cscript, powershell -File, conhost).</summary>
    private static (string Exe, string Args)? Unwrap(string hostPath, string args)
    {
        var host = Path.GetFileName(hostPath).ToLowerInvariant();
        var tokens = Tokenize(args);
        switch (host)
        {
            case "rundll32.exe":
            {
                // rundll32 "C:\x\y.dll",Entry tham số  |  rundll32 C:\x\y.dll,Entry
                if (tokens.Count == 0) return null;
                var first = tokens[0];
                var comma = first.IndexOf(',');
                var dll = (comma >= 0 ? first[..comma] : first).Trim().Trim('"');
                return dll.Length == 0 ? null : (dll, "");
            }
            case "cmd.exe":
            {
                var i = tokens.FindIndex(t => t.Equals("/c", StringComparison.OrdinalIgnoreCase) || t.Equals("/k", StringComparison.OrdinalIgnoreCase));
                if (i < 0) return null;
                var rest = string.Join(' ', tokens.Skip(i + 1).Select(Quote)).Trim();
                if (rest.StartsWith("start ", StringComparison.OrdinalIgnoreCase))
                {
                    // start "" "C:\x.exe" → bỏ "start" và tiêu đề cửa sổ.
                    var st = tokens.Skip(i + 2).ToList();
                    while (st.Count > 0 && (st[0].Length == 0 || st[0].StartsWith('/'))) st.RemoveAt(0);
                    rest = string.Join(' ', st.Select(Quote));
                }
                if (rest.Length == 0) return null;
                var (exe, a) = Split(rest);
                return exe.Length == 0 ? null : (exe, a);
            }
            case "wscript.exe" or "cscript.exe":
            {
                var script = tokens.FirstOrDefault(t => !t.StartsWith('/'));
                return script is null ? null : (script, "");
            }
            case "powershell.exe" or "pwsh.exe":
            {
                var i = tokens.FindIndex(t => t.Equals("-File", StringComparison.OrdinalIgnoreCase) || t.Equals("-f", StringComparison.OrdinalIgnoreCase));
                return i < 0 || i + 1 >= tokens.Count ? null : (tokens[i + 1], "");
            }
            case "conhost.exe":
            {
                var rest = tokens.Where(t => !t.StartsWith("--", StringComparison.Ordinal)).ToList();
                return rest.Count == 0 ? null : (rest[0], string.Join(' ', rest.Skip(1).Select(Quote)));
            }
            default:
                return null;
        }
    }

    private static string Quote(string s) => s.Contains(' ') ? "\"" + s + "\"" : s;

    /// <summary>Tách tham số theo khoảng trắng, tôn trọng ngoặc kép (đủ dùng cho trình bọc; không cần đúng tuyệt đối như CommandLineToArgvW).</summary>
    public static List<string> Tokenize(string args)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var has = false;
        foreach (var c in args)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                has = true;
                continue;
            }
            if (!inQuotes && c is ' ' or '\t')
            {
                if (has) result.Add(current.ToString());
                current.Clear();
                has = false;
                continue;
            }
            current.Append(c);
            has = true;
        }
        if (has) result.Add(current.ToString());
        return result;
    }
}
