using System.Diagnostics;
using System.Security.Principal;
using CleanBoost.Core;
using CleanBoost.Engine.Apps;
using Microsoft.Win32;

namespace CleanBoost.Features.StartupItems;

/// <summary>
/// Đọc mọi mục khởi động cùng Windows (LaunchItemScanner của bản Mac):
/// <list type="number">
/// <item>khoá <c>Run</c>/<c>RunOnce</c> của HKCU, HKLM (64-bit) và HKLM\WOW6432Node (32-bit), trạng thái bật/tắt từ <c>StartupApproved</c>;</item>
/// <item>thư mục Startup của người dùng và thư mục Startup chung (đọc đích của lối tắt .lnk);</item>
/// <item>tác vụ lập lịch có trình kích hoạt khi đăng nhập (9) hoặc khi khởi động máy (8), bỏ qua thư mục <c>\Microsoft\</c>.</item>
/// </list>
/// Rồi tra chương trình đích: còn tồn tại không (cờ hỏng), app sở hữu, nhà phát hành.
/// </summary>
public sealed class StartupItemScanner
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\";
    private const int TriggerBoot = 8;
    private const int TriggerLogon = 9;
    private const int ActionExec = 0;
    private const int ActionComHandler = 5;
    private const int RunLevelLua = 0;

    /// <summary>Quét mọi nguồn. <paramref name="apps"/> để tra app sở hữu (có thể bỏ trống), <paramref name="runningPaths"/> để biết mục nào đang chạy.</summary>
    public List<StartupItem> Scan(IReadOnlyList<InstalledApp>? apps = null, IEnumerable<string>? runningPaths = null, CancellationToken ct = default)
    {
        var raw = new List<StartupItem>();
        ScanRegistry(raw, ct);
        ScanStartupFolders(raw, ct);
        ScanScheduledTasks(raw, ct);
        return Enrich(raw, apps, runningPaths);
    }

    /// <summary>Tra app sở hữu, nhà phát hành, mô tả file và trạng thái đang chạy cho danh sách mục (gọi lại được khi có danh sách app).</summary>
    public static List<StartupItem> Enrich(IEnumerable<StartupItem> items, IReadOnlyList<InstalledApp>? apps, IEnumerable<string>? runningPaths)
    {
        var running = new HashSet<string>(runningPaths ?? [], StringComparer.OrdinalIgnoreCase);
        var versions = new Dictionary<string, FileVersionInfo?>(StringComparer.OrdinalIgnoreCase);
        var result = new List<StartupItem>();
        foreach (var item in items)
        {
            var target = item.TargetPath;
            string? owner = null, company = null, product = null, description = null;
            var installed = false;
            var system = false;
            if (target is not null && Path.IsPathFullyQualified(target))
            {
                if (apps is not null && MatchApp(target, apps) is { } app)
                {
                    owner = app.Name;
                    company = app.Publisher;
                    installed = true;
                }
                if (item.TargetExists == true)
                {
                    if (!versions.TryGetValue(target, out var v)) versions[target] = v = VersionInfo(target);
                    if (v is not null)
                    {
                        description = Clean(v.FileDescription);
                        product = Clean(v.ProductName);
                        company ??= Clean(v.CompanyName);
                    }
                    system = PathPolicy.IsSameOrInside(target, KnownPaths.Windows);
                }
            }
            result.Add(item with
            {
                OwnerName = owner ?? product,
                OwnerIsInstalledApp = installed,
                Publisher = company ?? item.Publisher,
                FileDescription = description,
                IsSystemComponent = system,
                IsRunning = target is not null && running.Contains(target),
            });
        }
        return result;
    }

    /// <summary>App đã cài có thư mục cài đặt chứa chương trình đích (thư mục dài nhất thắng).</summary>
    public static InstalledApp? MatchApp(string target, IReadOnlyList<InstalledApp> apps)
    {
        InstalledApp? best = null;
        foreach (var app in apps)
        {
            if (app.InstallLocation is not { Length: > 3 } dir) continue;
            if (!PathPolicy.IsSameOrInside(target, dir)) continue;
            if (best is null || dir.Length > best.InstallLocation!.Length) best = app;
        }
        return best;
    }

    private static FileVersionInfo? VersionInfo(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".exe" or ".dll" or ".scr" or ".com" or ".cpl")) return null;
        try { return FileVersionInfo.GetVersionInfo(path); } catch { return null; }
    }

    private static string? Clean(string? s)
    {
        var t = s?.Trim().Trim('\0').Trim();
        return string.IsNullOrEmpty(t) ? null : t;
    }

    // MARK: Registry

    private static void ScanRegistry(List<StartupItem> result, CancellationToken ct)
    {
        var sources = new List<(StartupScope Scope, RegistryHive Hive, RegistryView View)>
        {
            (StartupScope.User, RegistryHive.CurrentUser, RegistryView.Registry64),
            (StartupScope.Machine, RegistryHive.LocalMachine, RegistryView.Registry64),
        };
        // Trên Windows 32-bit hai view là một: không đọc lại.
        if (Environment.Is64BitOperatingSystem) sources.Add((StartupScope.Machine32, RegistryHive.LocalMachine, RegistryView.Registry32));
        foreach (var (scope, hive, view) in sources)
        {
            foreach (var source in new[] { StartupSource.Run, StartupSource.RunOnce })
            {
                ct.ThrowIfCancellationRequested();
                var location = source == StartupSource.Run ? StartupRegistry.LocationRun : StartupRegistry.LocationRunOnce;
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var key = root.OpenSubKey(CurrentVersionKey + location);
                    if (key is null) continue;
                    foreach (var name in key.GetValueNames())
                    {
                        if (string.IsNullOrEmpty(name)) continue;
                        if (ReadCommand(key, name) is not { } command) continue;
                        var res = StartupCommand.Resolve(command);
                        result.Add(new StartupItem
                        {
                            Id = $"reg:{scope.RegistryValue()}:{location}:{name}",
                            Source = source,
                            Scope = scope,
                            Name = name,
                            Command = command.Trim(),
                            TargetPath = res.Target,
                            TargetExists = res.Exists,
                            TargetOnFixedDrive = res.OnFixedDrive,
                            IsEnabled = source != StartupSource.Run || StartupRegistry.IsEnabled(scope.RegistryValue(), location, name),
                            RequiresAdmin = scope != StartupScope.User,
                            DataSize = (name.Length + command.Length + 2) * 2L,
                        });
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warning(LogCategory.Scan, $"Không đọc được khoá {hive}/{view} {location}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>Lệnh trong giá trị registry, giữ nguyên biến môi trường (REG_EXPAND_SZ) để hiển thị đúng như đã đăng ký.</summary>
    public static string? ReadCommand(RegistryKey key, string name)
    {
        try
        {
            return key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch
            {
                string s => s.Trim('\0'),
                string[] lines => string.Join(" ", lines),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    // MARK: Thư mục Startup

    private static void ScanStartupFolders(List<StartupItem> result, CancellationToken ct)
    {
        foreach (var (dir, scope) in new[] { (KnownPaths.StartupUser, StartupScope.User), (KnownPaths.StartupCommon, StartupScope.Machine) })
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir).ToList(); } catch { continue; }
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (ReadStartupFile(file, scope) is { } item) result.Add(item);
                }
                catch (Exception ex)
                {
                    Log.Warning(LogCategory.Scan, $"Không đọc được mục khởi động {file}: {ex.Message}");
                }
            }
        }
    }

    public static StartupItem? ReadStartupFile(string file, StartupScope scope)
    {
        var name = Path.GetFileName(file);
        var attributes = File.GetAttributes(file);
        if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) return null;
        if (attributes.HasFlag(FileAttributes.Hidden) && attributes.HasFlag(FileAttributes.System)) return null;
        string command;
        StartupCommand.Resolution res;
        switch (Path.GetExtension(file).ToLowerInvariant())
        {
            case ".lnk":
            {
                var link = ShellLink.Read(file);
                if (link?.TargetPath is not { } target || link.IsAdvertised)
                {
                    // Lối tắt "quảng cáo" của Windows Installer hoặc trỏ vào mục ảo của shell: không có đường dẫn để kiểm tra.
                    command = link?.TargetPath ?? name;
                    res = StartupCommand.Resolution.Unknown;
                }
                else
                {
                    command = (target.Contains(' ') ? "\"" + target + "\"" : target) + (link.Arguments.Length > 0 ? " " + link.Arguments : "");
                    res = StartupCommand.Resolve(target, link.Arguments, link.WorkingDirectory);
                }
                break;
            }
            case ".url":
                command = ReadUrl(file) ?? name;
                res = StartupCommand.Resolution.Unknown;
                break;
            default:
                // exe, bat, cmd, vbs... đặt thẳng trong thư mục: chính file là chương trình.
                command = file.Contains(' ') ? "\"" + file + "\"" : file;
                res = StartupCommand.Check(file);
                break;
        }
        long size = 0;
        try { size = new FileInfo(file).Length; } catch { }
        return new StartupItem
        {
            Id = "folder:" + file,
            Source = StartupSource.StartupFolder,
            Scope = scope,
            Name = name,
            Command = command,
            TargetPath = res.Target,
            TargetExists = res.Exists,
            TargetOnFixedDrive = res.OnFixedDrive,
            ShortcutPath = file,
            IsEnabled = StartupRegistry.IsEnabled(scope.RegistryValue(), StartupRegistry.LocationStartupFolder, name),
            RequiresAdmin = scope != StartupScope.User,
            DataSize = size,
        };
    }

    private static string? ReadUrl(string file)
    {
        try
        {
            return File.ReadLines(file).Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))?[4..];
        }
        catch
        {
            return null;
        }
    }

    // MARK: Tác vụ lập lịch

    private static bool IsMicrosoftFolder(string path) =>
        path.Equals(@"\Microsoft", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase);

    private static void ScanScheduledTasks(List<StartupItem> result, CancellationToken ct)
    {
        dynamic service;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return;
            service = Activator.CreateInstance(type)!;
            service.Connect();
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory.Scan, "Không kết nối được Task Scheduler: " + ex.Message);
            return;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var user = new UserNames(identity.Name, identity.User?.Value);
        var folders = new Stack<object>();
        try { folders.Push((object)service.GetFolder(@"\")); } catch { return; }
        while (folders.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            dynamic folder = folders.Pop();
            string folderPath;
            try { folderPath = folder.Path; } catch { continue; }
            if (IsMicrosoftFolder(folderPath)) continue;
            try
            {
                dynamic tasks = folder.GetTasks(1); // TASK_ENUM_HIDDEN
                int count = tasks.Count;
                for (var i = 1; i <= count; i++)
                {
                    try
                    {
                        if (ReadTask(tasks[i], user) is StartupItem item) result.Add(item);
                    }
                    catch
                    {
                        // Tác vụ không đọc được định nghĩa (thiếu quyền): bỏ qua.
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(LogCategory.Scan, $"Không đọc được thư mục tác vụ {folderPath}: {ex.Message}");
            }
            try
            {
                dynamic subs = folder.GetFolders(0);
                int n = subs.Count;
                for (var i = 1; i <= n; i++) folders.Push((object)subs[i]);
            }
            catch
            {
                // không liệt kê được thư mục con
            }
        }
    }

    private sealed record UserNames(string FullName, string? Sid)
    {
        public string ShortName => FullName.Contains('\\') ? FullName[(FullName.LastIndexOf('\\') + 1)..] : FullName;

        public bool Matches(string? id) => !string.IsNullOrEmpty(id) &&
            (id.Equals(FullName, StringComparison.OrdinalIgnoreCase) || id.Equals(ShortName, StringComparison.OrdinalIgnoreCase)
             || (Sid is not null && id.Equals(Sid, StringComparison.OrdinalIgnoreCase)));
    }

    private static StartupItem? ReadTask(dynamic task, UserNames user)
    {
        string path = task.Path;
        if (!StartupRegistry.IsAllowedTaskPath(path)) return null;
        dynamic definition = task.Definition;

        bool logon = false, boot = false;
        dynamic triggers = definition.Triggers;
        int triggerCount = triggers.Count;
        for (var i = 1; i <= triggerCount; i++)
        {
            dynamic trigger = triggers[i];
            bool triggerEnabled = trigger.Enabled;
            if (!triggerEnabled) continue;
            int type = trigger.Type;
            if (type == TriggerLogon) logon = true;
            else if (type == TriggerBoot) boot = true;
        }
        if (!logon && !boot) return null;

        string? program = null, arguments = null, workingDirectory = null, comClass = null;
        dynamic actions = definition.Actions;
        int actionCount = actions.Count;
        for (var i = 1; i <= actionCount && program is null; i++)
        {
            dynamic action = actions[i];
            int type = action.Type;
            if (type == ActionExec)
            {
                program = action.Path;
                arguments = action.Arguments;
                workingDirectory = action.WorkingDirectory;
            }
            else if (type == ActionComHandler && comClass is null)
            {
                comClass = action.ClassId;
            }
        }

        string? userId = null;
        var runLevel = 1;
        try
        {
            dynamic principal = definition.Principal;
            userId = principal.UserId;
            runLevel = principal.RunLevel;
        }
        catch
        {
            // giữ mặc định: coi như của hệ thống
        }
        string? author = null, description = null;
        try
        {
            dynamic info = definition.RegistrationInfo;
            author = info.Author;
            description = info.Description;
        }
        catch
        {
            // không có thông tin đăng ký
        }
        DateTime? lastRun = null;
        try
        {
            DateTime last = task.LastRunTime;
            if (last.Year > 2000) lastRun = last;
        }
        catch
        {
            // chưa chạy lần nào
        }
        bool enabled = task.Enabled;
        string name = task.Name;

        // Tác vụ do chính người dùng tạo, chạy với quyền thường: tự bật/tắt được. Còn lại (SYSTEM, nhóm Users, quyền cao nhất) cần quản trị.
        var ownedByUser = user.Matches(userId) && runLevel == RunLevelLua;
        string command;
        StartupCommand.Resolution res;
        if (program is not null)
        {
            var p = program.Trim().Trim('"');
            command = (p.Contains(' ') ? "\"" + p + "\"" : p) + (string.IsNullOrWhiteSpace(arguments) ? "" : " " + arguments.Trim());
            res = StartupCommand.Resolve(p, arguments, workingDirectory);
        }
        else
        {
            command = comClass is null ? "" : "COM " + comClass;
            res = StartupCommand.Resolution.Unknown;
        }
        return new StartupItem
        {
            Id = "task:" + path,
            Source = StartupSource.ScheduledTask,
            Scope = ownedByUser ? StartupScope.User : StartupScope.Machine,
            Name = name,
            Command = command,
            TargetPath = res.Target,
            TargetExists = res.Exists,
            TargetOnFixedDrive = res.OnFixedDrive,
            TaskPath = path,
            IsEnabled = enabled,
            RequiresAdmin = !ownedByUser,
            Publisher = string.IsNullOrWhiteSpace(author) ? null : author.Trim(),
            TaskDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            RunsAtLogon = logon,
            RunsAtBoot = boot,
            LastRunTime = lastRun,
        };
    }
}
