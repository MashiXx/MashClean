using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CleanBoost.Core;

public enum LogCategory
{
    Scan,
    Clean,
    Rules,
    UI,
    Storage,
    Ipc,
    Permissions,
    Menu,
    Helper,
}

/// <summary>
/// Log ra file xoay vòng (SYSTEM_DESIGN mục 17): <c>%LOCALAPPDATA%\CleanBoost\Logs\&lt;tiến trình&gt;.log</c>,
/// xoay ở 5 MB, giữ 5 file. Dòng: <c>&lt;ISO8601&gt; [INFO|WARN|ERROR] [category] message</c>.
/// Không ghi đường dẫn đầy đủ ở mức INFO; dùng <see cref="DebugPath"/> cho chi tiết.
/// </summary>
public static class Log
{
    private static readonly object s_lock = new();
    private static string? s_file;
    private const long MaxBytes = 5 * 1024 * 1024;
    private const int Keep = 5;

    public static string Directory { get; private set; } = Path.Combine(KnownPaths.AppData, "Logs");

    /// <summary>Bật log chi tiết (đường dẫn đầy đủ) — bản Debug hoặc biến môi trường <c>CLEANBOOST_DEBUG_LOG=1</c>.</summary>
    public static bool Verbose { get; set; } =
#if DEBUG
        true;
#else
        Environment.GetEnvironmentVariable("CLEANBOOST_DEBUG_LOG") == "1";
#endif

    public static void Configure(string processName, string? directory = null)
    {
        lock (s_lock)
        {
            if (directory is not null) Directory = directory;
            System.IO.Directory.CreateDirectory(Directory);
            s_file = Path.Combine(Directory, processName + ".log");
        }
    }

    public static void Info(LogCategory category, string message) => Write("INFO", category, message);
    public static void Warning(LogCategory category, string message) => Write("WARN", category, message);
    public static void Error(LogCategory category, string message) => Write("ERROR", category, message);

    public static void Error(LogCategory category, string message, Exception ex) => Write("ERROR", category, message + ": " + ex.GetType().Name + ": " + ex.Message);

    /// <summary>Chỉ ghi đường dẫn khi bật log chi tiết.</summary>
    public static void DebugPath(LogCategory category, string message, string path)
    {
        if (Verbose) Write("DEBUG", category, message + ": " + path);
    }

    private static void Write(string level, LogCategory category, string message)
    {
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTimeOffset.Now:yyyy-MM-ddTHH:mm:ss.fffzzz} [{level}] [{category.ToString().ToLowerInvariant()}] {message}");
        Debug.WriteLine(line);
        lock (s_lock)
        {
            if (s_file is null) return;
            try
            {
                Rotate();
                File.AppendAllText(s_file, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Log không được làm hỏng luồng chính.
            }
        }
    }

    private static void Rotate()
    {
        if (s_file is null) return;
        var info = new FileInfo(s_file);
        if (!info.Exists || info.Length < MaxBytes) return;
        for (var i = Keep - 1; i >= 1; i--)
        {
            var src = i == 1 ? s_file : s_file + "." + (i - 1);
            var dst = s_file + "." + i;
            if (!File.Exists(src)) continue;
            File.Delete(dst);
            File.Move(src, dst);
        }
    }

    /// <summary>Các file log sửa trong 24 giờ gần nhất, sắp theo tên (dùng cho báo cáo chẩn đoán).</summary>
    public static IReadOnlyList<string> RecentFiles(TimeSpan? within = null)
    {
        var cutoff = DateTime.Now - (within ?? TimeSpan.FromHours(24));
        try
        {
            return System.IO.Directory.EnumerateFiles(Directory)
                .Where(f => File.GetLastWriteTime(f) >= cutoff)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }
}
