using System.Diagnostics;
using CleanBoost.Core.Native;

namespace CleanBoost.Engine.Apps;

/// <summary>Tiến trình đang chạy: tên (chữ thường, không đuôi) và đường dẫn exe đọc được.</summary>
public static class RunningApps
{
    public sealed record Snapshot(IReadOnlySet<string> Names, IReadOnlyList<string> Paths, IReadOnlyList<(int Pid, string Path)> Processes);

    public static Snapshot Capture()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new List<string>();
        var processes = new List<(int, string)>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    names.Add(p.ProcessName.ToLowerInvariant());
                    if (p.Id <= 4) continue;
                    if (Win32.ProcessImagePath((uint)p.Id) is { } path)
                    {
                        paths.Add(path);
                        processes.Add((p.Id, path));
                    }
                }
                catch
                {
                    // tiến trình đã thoát hoặc không đọc được
                }
            }
        }
        return new Snapshot(names, paths, processes);
    }

    /// <summary>Tiến trình có exe nằm trong thư mục cho trước.</summary>
    public static IReadOnlyList<(int Pid, string Path)> ProcessesUnder(string directory) =>
        Capture().Processes.Where(p => Core.PathPolicy.IsSameOrInside(p.Path, directory)).ToList();
}
