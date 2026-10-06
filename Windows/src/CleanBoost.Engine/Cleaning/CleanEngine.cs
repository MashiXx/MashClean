using System.Collections.Concurrent;
using System.Diagnostics;
using CleanBoost.Core;
using CleanBoost.Core.Native;
using CleanBoost.Engine.FileSystem;
using CleanBoost.Engine.Ipc;
using CleanBoost.Engine.Nodes;
using CleanBoost.Engine.Persistence;

namespace CleanBoost.Engine.Cleaning;

/// <summary>
/// Lõi dọn dẹp (mục 7). Luồng: MakePlan → (xác nhận) → Execute theo batch 200 mục → ghi <c>clean_item_log</c> → báo cáo.
/// </summary>
public sealed class CleanEngine
{
    public FileSystemService FileSystem { get; }
    public IHelperClient? Helper { get; }
    public Storage? Storage { get; }
    public PathPolicy UserPolicy { get; }
    public PathPolicy RootPolicy { get; }
    public const int BatchSize = 200;
    private readonly ConcurrentDictionary<string, IRemover> _removers = new();

    /// <summary>Phát sau mỗi lần dọn thật (không phải chạy thử), để màn khác cập nhật số liệu.</summary>
    public static event Action? DidClean;

    public CleanEngine(FileSystemService fileSystem, IHelperClient? helper, Storage? storage, PathPolicy? policy = null)
    {
        FileSystem = fileSystem;
        Helper = helper;
        Storage = storage;
        UserPolicy = policy ?? PathPolicy.CurrentUser;
        RootPolicy = PathPolicy.Root;
        foreach (var r in new IRemover[] { new FileRemover(), new PrivilegedFileRemover(), new RecycleBinRemover(), new DockerRemover() }) Register(r);
    }

    /// <summary>Feature đăng ký remover riêng (vd Maintenance đăng ký <c>maintenance</c>).</summary>
    public void Register(IRemover remover) => _removers[remover.Id] = remover;

    public IRemover? Remover(string id) => _removers.GetValueOrDefault(id);

    // MARK: Lập kế hoạch

    public CleanPlan MakePlan(NodeTree tree, SelectionState selection, string? sessionID = null) =>
        MakePlan(selection.SelectedLeaves(tree), sessionID);

    /// <summary>Kiểm tra từng đường dẫn với PathPolicy; mục không thuộc người dùng thì chuyển sang helper nếu chính sách quản trị cho phép.</summary>
    public CleanPlan MakePlan(IEnumerable<Node> nodes, string? sessionID = null)
    {
        var items = new List<CleanPlan.Item>();
        var blocked = new List<CleanPlan.Blocked>();
        foreach (var node in nodes.Where(n => !n.IsContainer))
        {
            var requiresRoot = node.RequiresRoot;
            var url = node.Url;
            if (node.Removal.IsCustom)
            {
                // Remover chuyên biệt tự kiểm tra; với mục có file thật vẫn chặn vùng cấm.
                if (url is not null && UserPolicy.IsForbidden(url) is { } v)
                {
                    blocked.Add(new CleanPlan.Blocked(url, v, node.Title));
                    continue;
                }
            }
            else if (url is not null)
            {
                var intent = node.Removal.Kind == RemovalKind.DeleteContents ? PathPolicy.Intent.RemoveContents : PathPolicy.Intent.RemoveItem;
                var roots = node.AllowedRoots.Count == 0 ? null : node.AllowedRoots;
                var violation = UserPolicy.Validate(url, intent, roots, checkOwnership: !requiresRoot);
                if (violation is not null)
                {
                    // Chỉ vi phạm quyền sở hữu mới được chuyển sang helper; vùng cấm/bảo vệ của chính sách người dùng luôn chặn.
                    if (violation.IsOwnershipOnly)
                    {
                        if (RootPolicy.Validate(url, intent, roots) is { } rootViolation)
                        {
                            blocked.Add(new CleanPlan.Blocked(url, rootViolation, node.Title));
                            continue;
                        }
                        requiresRoot = true;
                    }
                    else
                    {
                        blocked.Add(new CleanPlan.Blocked(url, violation, node.Title));
                        continue;
                    }
                }
                if (requiresRoot && RootPolicy.Validate(url, intent, roots) is { } rv)
                {
                    blocked.Add(new CleanPlan.Blocked(url, rv, node.Title));
                    continue;
                }
                // Chuyển cả thư mục vào Thùng rác thì không bỏ qua được từng mục bên trong: chặn khi thư mục có thể chứa vùng cấm
                // (vd mật khẩu trình duyệt). Xoá thẳng thì SecureRemover tự giữ lại các mục đó.
                if (node.Removal.Kind == RemovalKind.MoveToTrash && !requiresRoot && FileSystem.IsDirectory(url)
                    && PathPolicy.Canonicalize(url) is { } canonicalDir && UserPolicy.SpecificForbiddenWithin(canonicalDir) is { } zone)
                {
                    blocked.Add(new CleanPlan.Blocked(url, new PathPolicy.Violation(PathPolicy.ViolationKind.ForbiddenZone, canonicalDir, zone), node.Title));
                    continue;
                }
                // Thùng rác không dùng được với file của hệ thống qua helper: mục cần quyền quản trị luôn xoá vĩnh viễn.
            }
            if (requiresRoot && Helper is null)
            {
                var label = url ?? node.Title;
                blocked.Add(new CleanPlan.Blocked(label, new PathPolicy.Violation(PathPolicy.ViolationKind.NeedsAdminHelper, label), node.Title));
                continue;
            }
            var strategy = node.Removal;
            var safety = node.Safety;
            if (requiresRoot && strategy.Kind == RemovalKind.MoveToTrash)
            {
                // Không qua được Thùng rác: thành xoá vĩnh viễn, nên luôn hiện trong hộp xác nhận ở mục "Cần xem lại".
                strategy = RemovalStrategy.Delete;
                safety = safety.Raised(SafetyLevel.Review);
            }
            items.Add(new CleanPlan.Item
            {
                NodeID = node.Id,
                Path = url,
                Title = node.Title,
                Strategy = strategy,
                RequiresRoot = requiresRoot,
                ExpectedSize = node.Size,
                RuleID = node.RuleID,
                Category = node.Category,
                Safety = safety,
                AllowedRoots = node.AllowedRoots,
                Virtual = node.Virtual,
            });
        }
        return new CleanPlan(items, blocked, sessionID);
    }

    // MARK: Thực thi

    public async Task<CleanReport> RunAsync(CleanPlan plan, bool dryRun, Func<string, Task<bool>>? confirmPermanentDelete = null,
        Action<CleanProgress>? onProgress = null, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var emit = onProgress ?? (_ => { });
        var operation = new CleanOperation { SessionId = plan.SessionID, PlannedBytes = plan.TotalSize.Bytes };
        try { Storage?.Insert(operation); } catch (Exception ex) { Log.Error(LogCategory.Clean, "Không ghi được clean_operation", ex); }

        // Đo dung lượng trống trước khi dọn (mục 7.5).
        var volumes = plan.Items.Select(i => i.Path).Where(p => p is not null).Select(p => PathPolicy.VolumeRoot(p!)).Where(v => v is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var before = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in volumes)
        {
            if (FileSystem.Volume(v!) is { } info) before[v!] = info.Available;
        }

        long freed = 0;
        var total = Math.Max(plan.TotalSize.Bytes, 1);
        var totalCount = Math.Max(plan.Items.Count, 1);
        var done = 0;
        var context = new RemoveContext
        {
            FileSystem = FileSystem,
            Helper = Helper,
            Policy = UserPolicy,
            DryRun = dryRun,
            Cancellation = ct,
            ConfirmPermanentDelete = confirmPermanentDelete ?? (_ => Task.FromResult(false)),
            Progress = (path, delta) =>
            {
                var f = Interlocked.Add(ref freed, delta);
                var byBytes = (double)f / total;
                var byCount = (double)Volatile.Read(ref done) / totalCount;
                emit(new CleanProgress(Math.Min(1, Math.Max(byBytes, byCount)), path, new ByteCount(f)));
            },
        };

        var entries = new List<CleanReport.Entry>();
        // Nhóm theo remover, giữ thứ tự xuất hiện.
        var order = new List<string>();
        var groups = new Dictionary<string, List<CleanPlan.Item>>();
        foreach (var item in plan.Items)
        {
            if (!groups.TryGetValue(item.RemoverID, out var list))
            {
                groups[item.RemoverID] = list = [];
                order.Add(item.RemoverID);
            }
            list.Add(item);
        }

        foreach (var removerID in order)
        {
            var items = groups[removerID];
            if (Remover(removerID) is not { } remover)
            {
                entries.AddRange(items.Select(i => new CleanReport.Entry(i, RemoveResult.Skipped(i.Label, SkipReason.Unsupported))));
                continue;
            }
            foreach (var batch in items.Chunk(BatchSize))
            {
                if (ct.IsCancellationRequested) break;
                IReadOnlyList<RemoveResult> results;
                try
                {
                    results = await remover.RemoveAsync(batch, context).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error(LogCategory.Clean, $"Remover {removerID} lỗi", ex);
                    results = batch.Select(i => RemoveResult.Failed(i.Label, Win32.ERROR_IO_DEVICE, ex.Message)).ToList();
                }
                if (removerID == RemoverIDs.File) results = await RetryPermissionFailuresAsync(batch, results, context).ConfigureAwait(false);
                var batchEntries = Pair(batch, results);
                entries.AddRange(batchEntries);
                Interlocked.Add(ref done, batch.Length);
                operation.ItemsOk = entries.Count(e => e.Result.IsSuccess);
                operation.ItemsFailed = entries.Count(e => e.Result.IsFailure);
                LogBatch(batchEntries, operation.Id);
                var f = Math.Max(Interlocked.Read(ref freed), entries.Sum(e => e.Result.FreedBytes));
                emit(new CleanProgress((double)Volatile.Read(ref done) / totalCount, batch[^1].Title, new ByteCount(f)));
            }
        }

        // Đo lại sau khi dọn: con số thực tế bên cạnh con số ước tính.
        long measured = 0;
        foreach (var (root, value) in before)
        {
            if (FileSystem.Volume(root) is { } after) measured += Math.Max(0, after.Available - value);
        }
        // Chạy thử: ước tính là dung lượng sẽ giải phóng được nếu xoá thật.
        var estimated = entries.Where(e => e.Result.IsSuccess || (dryRun && e.Result.Reason == SkipReason.DryRun)).Sum(e => e.Result.FreedBytes);
        operation.FinishedAt = DateTimeOffset.Now;
        operation.FreedBytes = dryRun ? 0 : before.Count == 0 ? estimated : Math.Max(measured, 0);
        try { Storage?.Update(operation); } catch (Exception ex) { Log.Error(LogCategory.Clean, "Không cập nhật được clean_operation", ex); }

        Log.Info(LogCategory.Clean, $"Dọn {entries.Count} mục{(dryRun ? " (dry run)" : "")}: ok {operation.ItemsOk}, lỗi {operation.ItemsFailed}, ước tính {estimated} byte, đo được {measured} byte");
        if (!dryRun) DidClean?.Invoke();
        stopwatch.Stop();
        return new CleanReport
        {
            OperationID = operation.Id,
            Entries = entries,
            EstimatedFreed = new ByteCount(estimated),
            MeasuredFreed = before.Count == 0 || dryRun ? null : new ByteCount(measured),
            Duration = stopwatch.Elapsed,
            DryRun = dryRun,
        };
    }

    /// <summary>Bị từ chối quyền với file người dùng: thử lại qua helper nếu đường dẫn nằm trong vùng quản trị cho phép (mục 7.4).</summary>
    private async Task<IReadOnlyList<RemoveResult>> RetryPermissionFailuresAsync(CleanPlan.Item[] batch, IReadOnlyList<RemoveResult> results, RemoveContext context)
    {
        if (Helper is null || context.DryRun) return results;
        var output = results.ToList();
        var retry = new List<(int Index, CleanPlan.Item Item)>();
        for (var i = 0; i < results.Count && i < batch.Length; i++)
        {
            var r = results[i];
            if (!r.IsFailure || !Win32Error.IsAccessDenied(r.ErrorCode) || batch[i].Path is not { } path || batch[i].Strategy.Kind == RemovalKind.MoveToTrash) continue;
            var intent = batch[i].Strategy.Kind == RemovalKind.DeleteContents ? PathPolicy.Intent.RemoveContents : PathPolicy.Intent.RemoveItem;
            var roots = batch[i].AllowedRoots.Count > 0 ? batch[i].AllowedRoots : null;
            // Cả chính sách người dùng (trừ quyền sở hữu) lẫn quản trị đều phải cho phép, trong vùng rule của mục.
            if (UserPolicy.Validate(path, intent, roots, checkOwnership: false) is null && RootPolicy.Validate(path, intent, roots) is null) retry.Add((i, batch[i]));
        }
        if (retry.Count == 0) return results;
        foreach (var mode in new[] { RemoveMode.Delete, RemoveMode.DeleteContents })
        {
            var subset = retry.Where(x => (x.Item.Strategy.Kind == RemovalKind.DeleteContents) == (mode == RemoveMode.DeleteContents)).ToList();
            if (subset.Count == 0) continue;
            try
            {
                var paths = subset.Select(x => x.Item.Path!).ToArray();
                var roots = subset.Select(x => x.Item.AllowedRoots.Count > 0 ? x.Item.AllowedRoots.ToArray() : null).ToArray();
                var r = await Helper.RemoveItemsAsync(paths, roots, mode, context.Cancellation).ConfigureAwait(false);
                for (var j = 0; j < subset.Count && j < r.Count; j++) output[subset[j].Index] = r[j];
            }
            catch (Exception ex)
            {
                Log.Warning(LogCategory.Clean, "Thử lại qua helper thất bại: " + ex.Message);
            }
        }
        return output;
    }

    internal static List<CleanReport.Entry> Pair(IReadOnlyList<CleanPlan.Item> items, IReadOnlyList<RemoveResult> results)
    {
        var entries = new List<CleanReport.Entry>();
        for (var i = 0; i < items.Count; i++)
        {
            var r = i < results.Count ? results[i] : RemoveResult.Failed(items[i].Label, Win32.ERROR_IO_DEVICE, Loc.T("Remover không trả kết quả"));
            entries.Add(new CleanReport.Entry(items[i], r));
        }
        return entries;
    }

    private void LogBatch(IEnumerable<CleanReport.Entry> entries, string operationID)
    {
        var logs = entries.Select(e => new CleanItemLog
        {
            OperationId = operationID,
            Path = string.IsNullOrEmpty(e.Result.Path) ? e.Item.Label : e.Result.Path,
            RuleId = e.Item.RuleID,
            Strategy = e.Item.Strategy.LogValue,
            TrashedPath = e.Result.TrashedPath,
            Size = e.Result.IsSuccess ? e.Result.FreedBytes : e.Item.ExpectedSize.Bytes,
            Result = e.Result.LogValue,
        });
        try { Storage?.LogItems(logs); } catch (Exception ex) { Log.Error(LogCategory.Clean, "Không ghi được clean_item_log", ex); }
    }

    // MARK: Khôi phục

    public sealed class RestoreException(string message) : Exception(message);

    /// <summary>Chuyển mục từ Thùng rác về vị trí cũ (mục 15.3).</summary>
    public void Restore(CleanItemLog log)
    {
        if (!log.IsRestorable || log.TrashedPath is not { } trashed) throw new RestoreException(Loc.T("Mục này đã bị xoá vĩnh viễn, không khôi phục được"));
        var error = RecycleBin.Restore(trashed, log.Path, out var message);
        switch (error)
        {
            case null:
                Storage?.MarkRestored(log.OperationId, log.Path);
                return;
            case RecycleBin.RestoreError.TrashedItemMissing:
                throw new RestoreException(Loc.T("Không còn tìm thấy mục trong Thùng rác"));
            case RecycleBin.RestoreError.DestinationExists:
                throw new RestoreException(Loc.T("Vị trí cũ đã có file cùng tên"));
            case RecycleBin.RestoreError.NotRestorable:
                throw new RestoreException(Loc.T("Mục này đã bị xoá vĩnh viễn, không khôi phục được"));
            default:
                throw new RestoreException(message ?? Loc.T("Không khôi phục được"));
        }
    }
}
