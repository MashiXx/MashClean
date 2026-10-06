using CleanBoost.Core;
using CleanBoost.Core.Ipc;

namespace CleanBoost.Helper;

/// <summary>
/// Xử lý lệnh có tên từ app (mục 9.2). Không bao giờ chạy lệnh shell tuỳ ý; kiểm tra lại PathPolicy phía quản trị cho
/// mọi đường dẫn; ghi log mọi lệnh kèm PID của client (mục 9.4).
/// </summary>
public sealed class HelperService(uint clientPid)
{
    private readonly SemaphoreSlim _maintenance = new(1, 1);

    public async Task<HelperResponse> HandleAsync(HelperRequest request)
    {
        try
        {
            switch (request.Command)
            {
                case HelperCommands.Hello:
                    return new HelperResponse { Id = request.Id, Ok = true, Version = Identifiers.HelperProtocolVersion };

                case HelperCommands.RemoveItems:
                {
                    var paths = request.Paths ?? [];
                    if (paths.Length > HelperCommands.MaxPathsPerCall) return Fail(request, "too many paths");
                    if (request.ItemRoots is { } itemRoots && itemRoots.Length != paths.Length) return Fail(request, "roots/paths mismatch");
                    Log.Info(LogCategory.Helper, $"removeItems {paths.Length} mục (mode {request.Mode}) từ pid {clientPid}");
                    var results = new RemoveResult[paths.Length];
                    for (var i = 0; i < paths.Length; i++)
                    {
                        var p = paths[i];
                        var roots = request.ItemRoots?[i] is { Length: > 0 } r ? r : null;
                        results[i] = string.IsNullOrEmpty(p)
                            ? RemoveResult.Skipped(p, SkipReason.BlockedByPolicy)
                            : SecureRemover.Remove(p, PathPolicy.Root, contentsOnly: request.Mode == RemoveMode.DeleteContents, allowedRoots: roots, checkOwnership: false);
                        if (results[i].Reason == SkipReason.BlockedByPolicy) Log.Warning(LogCategory.Helper, "Bị chặn bởi PathPolicy phía quản trị");
                    }
                    return new HelperResponse { Id = request.Id, Ok = true, Results = results };
                }

                case HelperCommands.RunMaintenance:
                {
                    if (MaintenanceTaskNames.Parse(request.Task) is not { } task) return Fail(request, "unknown task");
                    // Mỗi lần tối đa 1 tác vụ bảo trì (mục 9.4).
                    if (!await _maintenance.WaitAsync(0)) return Fail(request, "busy");
                    try
                    {
                        Log.Info(LogCategory.Helper, $"runMaintenance {task.RawValue()} từ pid {clientPid}");
                        var result = await MaintenanceExecutor.RunAsync(task);
                        return new HelperResponse { Id = request.Id, Ok = true, Command = result };
                    }
                    finally
                    {
                        _maintenance.Release();
                    }
                }

                case HelperCommands.SetStartupApproved:
                {
                    if (request.Scope is not (StartupRegistry.ScopeMachine or StartupRegistry.ScopeMachine32)) return Fail(request, "scope not allowed");
                    Log.Info(LogCategory.Helper, $"setStartupApproved {request.Scope}/{request.Location} enabled={request.Enabled} từ pid {clientPid}");
                    var r = StartupRegistry.SetEnabled(request.Scope, request.Location ?? "", request.Name ?? "", request.Enabled);
                    return new HelperResponse { Id = request.Id, Ok = true, Command = r };
                }

                case HelperCommands.DeleteRunValue:
                {
                    if (request.Scope is not (StartupRegistry.ScopeMachine or StartupRegistry.ScopeMachine32)) return Fail(request, "scope not allowed");
                    Log.Info(LogCategory.Helper, $"deleteRunValue {request.Scope}/{request.Location} từ pid {clientPid}");
                    var r = StartupRegistry.DeleteRunValue(request.Scope, request.Location ?? "", request.Name ?? "");
                    return new HelperResponse { Id = request.Id, Ok = true, Command = r };
                }

                case HelperCommands.SetScheduledTaskEnabled:
                {
                    var path = request.Name ?? "";
                    if (!StartupRegistry.IsAllowedTaskPath(path)) return Fail(request, "task path not allowed");
                    Log.Info(LogCategory.Helper, $"setScheduledTaskEnabled enabled={request.Enabled} từ pid {clientPid}");
                    var r = StartupRegistry.SetScheduledTaskEnabled(path, request.Enabled);
                    return new HelperResponse { Id = request.Id, Ok = true, Command = r };
                }

                default:
                    return Fail(request, "unknown command");
            }
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Helper, "Lệnh " + request.Command + " lỗi", ex);
            return Fail(request, ex.Message);
        }
    }

    private static HelperResponse Fail(HelperRequest r, string error) => new() { Id = r.Id, Ok = false, Error = error };
}
