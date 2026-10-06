using CleanBoost.Core;
using CleanBoost.Engine.Ipc;
using Microsoft.Win32;

namespace CleanBoost.Features.StartupItems;

/// <summary>
/// Bật/tắt một mục khởi động (LoginItemsController của bản Mac). Giống Task Manager: không sửa lệnh, chỉ ghi trạng thái vào
/// <c>StartupApproved</c> (Run, thư mục Startup) hoặc đổi cờ Enabled của tác vụ lập lịch. Phạm vi máy đi qua helper quản trị (UAC).
/// </summary>
public static class StartupItemsController
{
    /// <summary>Bật hoặc tắt; trả về lỗi (đã dịch) nếu không làm được.</summary>
    public static async Task<CommandResult> SetEnabledAsync(StartupItem item, bool enabled, IHelperClient? helper, CancellationToken ct = default)
    {
        if (!item.CanToggle) return CommandResult.Fail(item.ToggleUnavailableReason ?? Loc.T("Không hỗ trợ"));
        try
        {
            switch (item.Source)
            {
                case StartupSource.Run or StartupSource.StartupFolder:
                {
                    var location = item.RegistryLocation!;
                    if (item.Scope == StartupScope.User)
                        return await Task.Run(() => StartupRegistry.SetEnabled(StartupRegistry.ScopeUser, location, item.ApprovedName, enabled), ct).ConfigureAwait(false);
                    if (helper is null) return CommandResult.Fail(Loc.T("Chưa có helper quản trị"));
                    return await helper.SetStartupApprovedAsync(item.Scope.RegistryValue(), location, item.ApprovedName, enabled, ct).ConfigureAwait(false);
                }
                case StartupSource.ScheduledTask:
                    return await SetTaskEnabledAsync(item, enabled, helper, ct).ConfigureAwait(false);
                default:
                    return CommandResult.Fail(Loc.T("Không hỗ trợ"));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CommandResult.Fail(ex.Message);
        }
    }

    /// <summary>Tác vụ của người dùng: thử trực tiếp, bị từ chối thì qua helper. Tác vụ của hệ thống: qua helper.</summary>
    public static async Task<CommandResult> SetTaskEnabledAsync(StartupItem item, bool enabled, IHelperClient? helper, CancellationToken ct = default)
    {
        if (item.TaskPath is not { } path) return CommandResult.Fail(Loc.T("Không hỗ trợ"));
        if (!item.RequiresAdmin)
        {
            var local = await Task.Run(() => StartupRegistry.SetScheduledTaskEnabled(path, enabled), ct).ConfigureAwait(false);
            if (local.Success || helper is null) return local;
            Log.Info(LogCategory.UI, "Đổi trạng thái tác vụ lập lịch cần quyền quản trị, chuyển sang helper");
        }
        if (helper is null) return CommandResult.Fail(Loc.T("Chưa có helper quản trị"));
        return await helper.SetScheduledTaskEnabledAsync(path, enabled, ct).ConfigureAwait(false);
    }

    /// <summary>Lệnh hiện tại của một giá trị Run/RunOnce (đọc lại trước khi xoá); <c>null</c> nếu không còn.</summary>
    public static string? CurrentRunCommand(StartupItem item)
    {
        if (item.RegistryLocation is not { } location || item.Source is not (StartupSource.Run or StartupSource.RunOnce)) return null;
        var (hive, view) = item.Scope switch
        {
            StartupScope.User => (RegistryHive.CurrentUser, RegistryView.Registry64),
            StartupScope.Machine => (RegistryHive.LocalMachine, RegistryView.Registry64),
            _ => (RegistryHive.LocalMachine, RegistryView.Registry32),
        };
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\" + location);
            return key is null ? null : StartupItemScanner.ReadCommand(key, item.Name);
        }
        catch
        {
            return null;
        }
    }
}
