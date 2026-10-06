using Microsoft.Win32;

namespace CleanBoost.Core;

/// <summary>
/// Thao tác ghi với mục khởi động (tương đương <c>launchctl bootout</c> của bản Mac): bật/tắt bằng khoá
/// <c>StartupApproved</c> giống Task Manager, xoá giá trị trong khoá <c>Run</c>/<c>RunOnce</c>, bật/tắt tác vụ lập lịch.
/// Chỉ chấp nhận đúng các khoá đã biết; helper dùng cho phạm vi máy (HKLM), app dùng cho người dùng (HKCU).
/// </summary>
public static class StartupRegistry
{
    public const string ScopeUser = "user";
    public const string ScopeMachine = "machine";
    public const string ScopeMachine32 = "machine32";

    public const string LocationRun = "Run";
    public const string LocationRunOnce = "RunOnce";
    public const string LocationStartupFolder = "StartupFolder";

    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\";
    private const string ApprovedKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    /// <summary>Mở hive theo phạm vi; <c>null</c> nếu phạm vi lạ.</summary>
    private static RegistryKey? OpenBase(string scope) => scope switch
    {
        ScopeUser => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
        ScopeMachine => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
        ScopeMachine32 => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32),
        _ => null,
    };

    /// <summary>
    /// Tên khoá con trong StartupApproved: <c>Run</c>, <c>Run32</c> (app 32-bit trên HKLM) hoặc <c>StartupFolder</c>.
    /// <c>RunOnce</c> không có trạng thái bật/tắt (Explorer không đọc StartupApproved cho nó): trả về <c>null</c> để không ghi
    /// nhầm vào giá trị cùng tên của một mục <c>Run</c>.
    /// </summary>
    public static string? ApprovedSubkey(string scope, string location) => location switch
    {
        LocationRun => scope == ScopeMachine32 ? "Run32" : "Run",
        LocationStartupFolder => "StartupFolder",
        _ => null,
    };

    /// <summary>
    /// Đọc trạng thái bật: dữ liệu nhị phân 12 byte, byte đầu chẵn (0x02/0x06) là bật, lẻ (0x03/0x07) là tắt.
    /// Không có giá trị nghĩa là bật.
    /// </summary>
    public static bool IsEnabled(string scope, string location, string name)
    {
        if (ApprovedSubkey(scope, location) is not { } sub) return true;
        try
        {
            using var root = scope == ScopeUser ? RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64)
                : RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(ApprovedKey + sub);
            return key?.GetValue(name) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;
        }
        catch
        {
            return true;
        }
    }

    public static CommandResult SetEnabled(string scope, string location, string name, bool enabled)
    {
        if (ApprovedSubkey(scope, location) is not { } sub) return CommandResult.Fail("unknown location");
        if (string.IsNullOrEmpty(name) || name.Contains('\0')) return CommandResult.Fail("bad name");
        try
        {
            // StartupApproved luôn nằm ở view 64-bit; app 32-bit dùng khoá con Run32.
            using var root = scope == ScopeUser ? RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64)
                : RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.CreateSubKey(ApprovedKey + sub, writable: true);
            var data = new byte[12];
            data[0] = enabled ? (byte)0x02 : (byte)0x03;
            if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
            key.SetValue(name, data, RegistryValueKind.Binary);
            return CommandResult.Ok();
        }
        catch (Exception ex)
        {
            return CommandResult.Fail(ex.Message);
        }
    }

    /// <summary>Xoá một giá trị trong khoá Run/RunOnce, trả về dữ liệu cũ (để ghi lịch sử).</summary>
    public static CommandResult DeleteRunValue(string scope, string location, string name)
    {
        if (location is not (LocationRun or LocationRunOnce)) return CommandResult.Fail("unknown location");
        try
        {
            using var root = OpenBase(scope);
            if (root is null) return CommandResult.Fail("unknown scope");
            using var key = root.OpenSubKey(RunKey + location, writable: true);
            if (key?.GetValue(name) is not { } old) return CommandResult.Ok(Loc.T("Không còn tồn tại"));
            key.DeleteValue(name, throwOnMissingValue: false);
            if (ApprovedSubkey(scope, location) is { } sub)
            {
                try
                {
                    using var approvedRoot = scope == ScopeUser ? RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64)
                        : RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                    using var approved = approvedRoot.OpenSubKey(ApprovedKey + sub, writable: true);
                    approved?.DeleteValue(name, throwOnMissingValue: false);
                }
                catch
                {
                    // không quan trọng
                }
            }
            return CommandResult.Ok("", old.ToString());
        }
        catch (Exception ex)
        {
            return CommandResult.Fail(ex.Message);
        }
    }

    /// <summary>Đường dẫn tác vụ lập lịch hợp lệ để bật/tắt: tuyệt đối, không thuộc thư mục <c>\Microsoft\</c> của Windows.</summary>
    public static bool IsAllowedTaskPath(string path) =>
        path.StartsWith('\\') && !path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase) && !path.Contains("..", StringComparison.Ordinal) && path.Length < 512;

    /// <summary>Bật/tắt tác vụ lập lịch qua COM <c>Schedule.Service</c>.</summary>
    public static CommandResult SetScheduledTaskEnabled(string path, bool enabled)
    {
        if (!IsAllowedTaskPath(path)) return CommandResult.Fail("task path not allowed");
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler unavailable");
            dynamic service = Activator.CreateInstance(type)!;
            service.Connect();
            var folderPath = path[..(path.LastIndexOf('\\') is var i && i > 0 ? i : 1)];
            var name = path[(path.LastIndexOf('\\') + 1)..];
            dynamic folder = service.GetFolder(folderPath);
            dynamic task = folder.GetTask(name);
            task.Enabled = enabled;
            return CommandResult.Ok();
        }
        catch (Exception ex)
        {
            return CommandResult.Fail(ex.Message);
        }
    }
}
