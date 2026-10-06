using CleanBoost.Core;
using CleanBoost.Core.Native;
using CleanBoost.Engine.Cleaning;
using CleanBoost.Engine.Ipc;

namespace CleanBoost.Features.StartupItems;

/// <summary>
/// Remover <c>startupItem</c> (LaunchItemRemover của bản Mac): gỡ một mục khởi động.
/// <list type="bullet">
/// <item>Run/RunOnce của người dùng: xoá giá trị trong HKCU (lệnh cũ ghi vào kết quả để tra lại trong lịch sử); của máy: qua helper.</item>
/// <item>Lối tắt trong thư mục Startup của người dùng: chuyển vào Thùng rác; thư mục Startup chung: xoá qua helper.</item>
/// <item>Tác vụ lập lịch: chỉ <b>tắt</b>, không bao giờ xoá tác vụ.</item>
/// </list>
/// Trước khi xoá luôn đọc lại: giá trị Run đã đổi lệnh, hay chương trình của mục "hỏng" đã có lại, thì giữ nguyên.
/// </summary>
public sealed class StartupItemRemover : IRemover
{
    public string Id => RemoverIDs.StartupItem;

    public async Task<IReadOnlyList<RemoveResult>> RemoveAsync(IReadOnlyList<CleanPlan.Item> items, RemoveContext context)
    {
        var results = new List<RemoveResult>();
        foreach (var plan in items)
        {
            if (context.Cancellation.IsCancellationRequested) break;
            if (plan.Virtual is not StartupItemEntry { Item: var item })
            {
                results.Add(RemoveResult.Failed(plan.Label, Win32.ERROR_INVALID_PARAMETER, Loc.T("Không phải mục khởi động")));
                continue;
            }
            try
            {
                var r = await RemoveOneAsync(item, plan, context).ConfigureAwait(false);
                if (r.IsSuccess && !context.DryRun) Log.Info(LogCategory.Clean, $"Đã gỡ mục khởi động ({item.Source}, {item.Scope})");
                results.Add(r);
            }
            catch (HelperException ex)
            {
                results.Add(context.Helper?.Status == HelperStatus.Declined
                    ? RemoveResult.Skipped(item.Label, SkipReason.UserDeclined) with { Message = ex.Message }
                    : RemoveResult.Failed(item.Label, Win32.ERROR_ACCESS_DENIED, ex.Message));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error(LogCategory.Clean, "Gỡ mục khởi động lỗi", ex);
                results.Add(RemoveResult.Failed(item.Label, Win32.ERROR_IO_DEVICE, ex.Message));
            }
        }
        return results;
    }

    private static Task<RemoveResult> RemoveOneAsync(StartupItem item, CleanPlan.Item plan, RemoveContext context)
    {
        var label = item.Label;
        if (item.IsSystemComponent)
            return Task.FromResult(RemoveResult.Skipped(label, SkipReason.BlockedByPolicy) with { Message = Loc.T("Thành phần của Windows: chỉ có thể tắt") });
        // Mục đề xuất vì hỏng: kiểm tra lại ngay trước khi xoá — nếu chương trình đã có lại (vd vừa cài lại app) thì giữ nguyên.
        if (item.IsBroken && item.TargetPath is { } target && StartupCommand.Check(target).Exists != false)
            return Task.FromResult(RemoveResult.Skipped(label, SkipReason.BlockedByPolicy) with { Message = Loc.T("Chương trình đã có lại, giữ nguyên mục khởi động") });
        return item.Source switch
        {
            StartupSource.Run or StartupSource.RunOnce => RemoveRunValueAsync(item, plan, context),
            StartupSource.StartupFolder => RemoveStartupFileAsync(item, plan, context),
            _ => DisableTaskAsync(item, context),
        };
    }

    // MARK: Run / RunOnce

    private static async Task<RemoveResult> RemoveRunValueAsync(StartupItem item, CleanPlan.Item plan, RemoveContext context)
    {
        var label = item.Label;
        var size = plan.ExpectedSize.Bytes;
        var location = item.RegistryLocation!;
        var current = StartupItemsController.CurrentRunCommand(item);
        if (current is null) return RemoveResult.Skipped(label, SkipReason.NotFound);
        // App đã ghi lại lệnh khác từ lúc quét: không xoá nhầm mục mới.
        if (!string.Equals(current.Trim(), item.Command.Trim(), StringComparison.Ordinal))
            return RemoveResult.Skipped(label, SkipReason.BlockedByPolicy) with { Message = Loc.T("Mục đã thay đổi kể từ lần quét, giữ nguyên") };
        if (context.DryRun) return RemoveResult.Skipped(label, SkipReason.DryRun, size);

        CommandResult r;
        if (item.Scope == StartupScope.User)
        {
            r = StartupRegistry.DeleteRunValue(StartupRegistry.ScopeUser, location, item.Name);
        }
        else
        {
            if (context.Helper is null) return RemoveResult.Failed(label, Win32.ERROR_ACCESS_DENIED, Loc.T("Chưa có helper quản trị"));
            r = await context.Helper.DeleteRunValueAsync(item.Scope.RegistryValue(), location, item.Name, context.Cancellation).ConfigureAwait(false);
        }
        if (!r.Success) return RemoveResult.Failed(label, Win32.ERROR_ACCESS_DENIED, r.Message);
        if (r.Output is null) return RemoveResult.Skipped(label, SkipReason.NotFound);
        context.Progress(label, size);
        // Lệnh cũ đi kèm kết quả để tra lại (và tự thêm lại nếu cần).
        return RemoveResult.Ok(label, size) with { Message = Loc.F("Lệnh cũ: %@", r.Output) };
    }

    // MARK: Thư mục Startup

    private static async Task<RemoveResult> RemoveStartupFileAsync(StartupItem item, CleanPlan.Item plan, RemoveContext context)
    {
        if (item.ShortcutPath is not { } path) return RemoveResult.Failed(item.Label, Win32.ERROR_INVALID_PARAMETER, Loc.T("Mục không có đường dẫn"));
        var size = plan.ExpectedSize.Bytes;
        if (!context.FileSystem.Exists(path)) return RemoveResult.Skipped(path, SkipReason.NotFound);
        if (context.DryRun) return RemoveResult.Skipped(path, SkipReason.DryRun, size);

        if (item.Scope == StartupScope.User)
        {
            var violation = context.Policy.Validate(path);
            if (violation is null)
            {
                var trash = await Task.Run(() => RecycleBin.MoveToTrash(path)).ConfigureAwait(false);
                if (trash.Success)
                {
                    context.Progress(path, size);
                    return RemoveResult.Ok(path, size, trash.TrashedPath);
                }
                if (trash.Error == RecycleBin.TrashError.Cancelled) return RemoveResult.Skipped(path, SkipReason.UserDeclined);
                if (trash.Error == RecycleBin.TrashError.Unavailable)
                {
                    if (!await context.ConfirmPermanentDelete(path).ConfigureAwait(false)) return RemoveResult.Skipped(path, SkipReason.UserDeclined);
                    return SecureRemover.Remove(path, context.Policy);
                }
                return SecureRemover.ResultFor(trash.Code, path);
            }
            // Lối tắt do bộ cài chạy quyền quản trị tạo ra (không thuộc người dùng): xoá qua helper; vi phạm khác thì chặn.
            if (!violation.IsOwnershipOnly) return RemoveResult.Skipped(path, SkipReason.BlockedByPolicy);
        }

        // Thư mục Startup chung (ProgramData): helper kiểm tra lại PathPolicy phía quản trị rồi xoá (không qua Thùng rác).
        if (context.Helper is null) return RemoveResult.Failed(path, Win32.ERROR_ACCESS_DENIED, Loc.T("Chưa có helper quản trị"));
        var results = await context.Helper.RemoveItemsAsync([path], RemoveMode.Delete, context.Cancellation).ConfigureAwait(false);
        var result = results.FirstOrDefault() ?? RemoveResult.Failed(path, Win32.ERROR_IO_DEVICE, Loc.T("Helper không trả kết quả"));
        if (result.IsSuccess) context.Progress(path, size);
        return result;
    }

    // MARK: Tác vụ lập lịch

    private static async Task<RemoveResult> DisableTaskAsync(StartupItem item, RemoveContext context)
    {
        var label = item.Label;
        if (!item.IsEnabled) return RemoveResult.Ok(label, 0) with { Message = Loc.T("Tác vụ đã tắt sẵn") };
        if (context.DryRun) return RemoveResult.Skipped(label, SkipReason.DryRun);
        var r = await StartupItemsController.SetTaskEnabledAsync(item, false, context.Helper, context.Cancellation).ConfigureAwait(false);
        return r.Success
            ? RemoveResult.Ok(label, 0) with { Message = Loc.T("Đã tắt tác vụ lập lịch (không xoá tác vụ)") }
            : RemoveResult.Failed(label, Win32.ERROR_ACCESS_DENIED, r.Message);
    }
}
