using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using CleanBoost.Core;
using CleanBoost.Core.Ipc;
using CleanBoost.Core.Native;

namespace CleanBoost.Engine.Ipc;

public enum HelperStatus
{
    /// <summary>Chưa chạy; sẽ hỏi UAC khi cần.</summary>
    NotRunning,
    /// <summary>Đang chạy với quyền quản trị.</summary>
    Running,
    /// <summary>Người dùng từ chối hộp thoại UAC lần gần nhất.</summary>
    Declined,
    /// <summary>Không tìm thấy <c>CleanBoost.Helper.exe</c> cạnh app.</summary>
    NotFound,
    /// <summary>Không kết nối được hoặc helper không qua kiểm tra.</summary>
    Unreachable,
}

/// <summary>Kênh tới helper quản trị (SYSTEM_DESIGN mục 9). Remover và feature chỉ thấy interface này.</summary>
public interface IHelperClient
{
    HelperStatus Status { get; }
    event Action<HelperStatus>? StatusChanged;
    Task<bool> EnsureRunningAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RemoveResult>> RemoveItemsAsync(string[] paths, RemoveMode mode, CancellationToken ct = default);
    /// <summary>Như trên, kèm vùng rule cho phép của từng đường dẫn để helper kiểm tra lại sau khi chuẩn hoá.</summary>
    Task<IReadOnlyList<RemoveResult>> RemoveItemsAsync(string[] paths, string[]?[] itemRoots, RemoveMode mode, CancellationToken ct = default) =>
        RemoveItemsAsync(paths, mode, ct);
    Task<CommandResult> RunMaintenanceAsync(MaintenanceTaskName task, CancellationToken ct = default);
    Task<CommandResult> SetStartupApprovedAsync(string scope, string location, string name, bool enabled, CancellationToken ct = default);
    Task<CommandResult> DeleteRunValueAsync(string scope, string location, string name, CancellationToken ct = default);
    Task<CommandResult> SetScheduledTaskEnabledAsync(string taskPath, bool enabled, CancellationToken ct = default);
    void Shutdown();
}

public sealed class HelperException(string message) : Exception(message);

/// <summary>
/// Helper quản trị bản Windows: một tiến trình <c>CleanBoost.Helper.exe</c> chạy qua UAC khi lần đầu cần, sống trong
/// phiên làm việc rồi tự thoát. Kênh là named pipe chỉ người dùng hiện tại mở được; app kiểm tra tiến trình nối vào đúng
/// là helper nó vừa chạy (PID + đường dẫn exe), helper kiểm tra ngược lại tiến trình tạo pipe là <c>CleanBoost.exe</c>
/// cùng thư mục (tương đương kiểm tra chữ ký hai chiều của XPC).
/// </summary>
public sealed class HelperClient : IHelperClient, IDisposable
{
    private readonly string _helperPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeServerStream? _pipe;
    private Process? _process;
    private int _nextId;
    private HelperStatus _status;
    private Timer? _idleTimer;

    /// <summary>Đóng helper sau khoảng rảnh này (lần sau cần thì hỏi UAC lại).</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);

    public event Action<HelperStatus>? StatusChanged;

    public HelperClient(string? helperPath = null)
    {
        _helperPath = helperPath ?? Path.Combine(AppContext.BaseDirectory, Identifiers.HelperExeName);
        _status = File.Exists(_helperPath) ? HelperStatus.NotRunning : HelperStatus.NotFound;
    }

    public HelperStatus Status
    {
        get => _status;
        private set
        {
            if (_status == value) return;
            _status = value;
            StatusChanged?.Invoke(value);
        }
    }

    /// <summary>App đang chạy với quyền quản trị (khi đó helper chạy mà không hỏi UAC).</summary>
    public static bool IsProcessElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public async Task<bool> EnsureRunningAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await EnsureRunningLockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> EnsureRunningLockedAsync(CancellationToken ct)
    {
        if (_pipe is { IsConnected: true } && _process is { HasExited: false }) return true;
        Disconnect();
        if (!File.Exists(_helperPath))
        {
            Status = HelperStatus.NotFound;
            return false;
        }
        var name = "CleanBoost.Helper." + Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Process? process;
        try
        {
            var psi = new ProcessStartInfo(_helperPath)
            {
                UseShellExecute = true,
                Verb = IsProcessElevated ? "" : "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"--pipe {name} --parent {Environment.ProcessId}",
                WorkingDirectory = Path.GetDirectoryName(_helperPath)!,
            };
            process = Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Win32.ERROR_CANCELLED)
        {
            pipe.Dispose();
            Status = HelperStatus.Declined;
            Log.Warning(LogCategory.Ipc, "Người dùng từ chối UAC cho helper");
            return false;
        }
        catch (Exception ex)
        {
            pipe.Dispose();
            Status = HelperStatus.Unreachable;
            Log.Error(LogCategory.Ipc, "Không chạy được helper", ex);
            return false;
        }
        if (process is null)
        {
            pipe.Dispose();
            Status = HelperStatus.Unreachable;
            return false;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);

            // Kiểm tra tiến trình nối vào đúng là helper vừa chạy.
            if (!Win32.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid) || clientPid != (uint)process.Id)
                throw new HelperException("Tiến trình nối vào pipe không phải helper");
            var image = Win32.ProcessImagePath(clientPid);
            if (image is null || !string.Equals(Path.GetFullPath(image), Path.GetFullPath(_helperPath), StringComparison.OrdinalIgnoreCase))
                throw new HelperException("Đường dẫn helper không khớp");

            _pipe = pipe;
            _process = process;
            var hello = await SendLockedAsync(new HelperRequest { Command = HelperCommands.Hello }, ct).ConfigureAwait(false);
            if (hello.Version != Identifiers.HelperProtocolVersion) throw new HelperException($"Helper phiên bản {hello.Version}, cần {Identifiers.HelperProtocolVersion}");
            Status = HelperStatus.Running;
            Log.Info(LogCategory.Ipc, $"Helper sẵn sàng (pid {process.Id})");
            TouchIdle();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Ipc, "Không kết nối được helper", ex);
            try { if (!process.HasExited) process.Kill(); } catch { }
            pipe.Dispose();
            _pipe = null;
            _process = null;
            Status = HelperStatus.Unreachable;
            return false;
        }
    }

    private async Task<HelperResponse> SendAsync(HelperRequest request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await EnsureRunningLockedAsync(ct).ConfigureAwait(false))
                throw new HelperException(Status == HelperStatus.Declined ? Loc.T("Bạn đã không cấp quyền quản trị") : Loc.T("Không kết nối được helper quản trị"));
            var response = await SendLockedAsync(request, ct).ConfigureAwait(false);
            TouchIdle();
            return response;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HelperResponse> SendLockedAsync(HelperRequest request, CancellationToken ct)
    {
        var pipe = _pipe ?? throw new HelperException(Loc.T("Không kết nối được helper quản trị"));
        var withId = request with { Id = Interlocked.Increment(ref _nextId) };
        HelperResponse response;
        try
        {
            await PipeMessage.WriteAsync(pipe, withId, ct).ConfigureAwait(false);
            response = await PipeMessage.ReadAsync<HelperResponse>(pipe, ct).ConfigureAwait(false)
                ?? throw new HelperException(Loc.T("Helper đã đóng kết nối"));
            if (response.Id != withId.Id) throw new HelperException("Helper trả lời sai thứ tự");
        }
        catch (Exception ex)
        {
            // Huỷ giữa chừng, khung đọc dở hay trả lời lệch: kênh không còn đồng bộ. Đóng hẳn để lần sau mở helper mới
            // (helper thấy pipe đóng sẽ tự thoát sau lệnh đang chạy).
            Disconnect();
            Status = HelperStatus.Unreachable;
            if (ex is OperationCanceledException) throw;
            throw ex as HelperException ?? new HelperException(ex.Message);
        }
        if (!response.Ok) throw new HelperException(response.Error ?? "Helper error");
        return response;
    }

    public Task<IReadOnlyList<RemoveResult>> RemoveItemsAsync(string[] paths, RemoveMode mode, CancellationToken ct = default) =>
        RemoveItemsAsync(paths, new string[]?[paths.Length], mode, ct);

    public async Task<IReadOnlyList<RemoveResult>> RemoveItemsAsync(string[] paths, string[]?[] itemRoots, RemoveMode mode, CancellationToken ct = default)
    {
        if (itemRoots.Length != paths.Length) throw new ArgumentException("itemRoots must match paths", nameof(itemRoots));
        var results = new List<RemoveResult>();
        for (var start = 0; start < paths.Length; start += HelperCommands.MaxPathsPerCall)
        {
            var count = Math.Min(HelperCommands.MaxPathsPerCall, paths.Length - start);
            var request = new HelperRequest
            {
                Command = HelperCommands.RemoveItems,
                Paths = paths[start..(start + count)],
                ItemRoots = itemRoots[start..(start + count)],
                Mode = mode,
            };
            var r = await SendAsync(request, ct).ConfigureAwait(false);
            results.AddRange(r.Results ?? []);
        }
        return results;
    }

    public async Task<CommandResult> RunMaintenanceAsync(MaintenanceTaskName task, CancellationToken ct = default) =>
        (await SendAsync(new HelperRequest { Command = HelperCommands.RunMaintenance, Task = task.RawValue() }, ct).ConfigureAwait(false)).Command
        ?? CommandResult.Fail("no result");

    public async Task<CommandResult> SetStartupApprovedAsync(string scope, string location, string name, bool enabled, CancellationToken ct = default) =>
        (await SendAsync(new HelperRequest { Command = HelperCommands.SetStartupApproved, Scope = scope, Location = location, Name = name, Enabled = enabled }, ct)
            .ConfigureAwait(false)).Command ?? CommandResult.Fail("no result");

    public async Task<CommandResult> DeleteRunValueAsync(string scope, string location, string name, CancellationToken ct = default) =>
        (await SendAsync(new HelperRequest { Command = HelperCommands.DeleteRunValue, Scope = scope, Location = location, Name = name }, ct)
            .ConfigureAwait(false)).Command ?? CommandResult.Fail("no result");

    public async Task<CommandResult> SetScheduledTaskEnabledAsync(string taskPath, bool enabled, CancellationToken ct = default) =>
        (await SendAsync(new HelperRequest { Command = HelperCommands.SetScheduledTaskEnabled, Name = taskPath, Enabled = enabled }, ct)
            .ConfigureAwait(false)).Command ?? CommandResult.Fail("no result");

    private void TouchIdle()
    {
        _idleTimer ??= new Timer(_ => Shutdown());
        _idleTimer.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Bảo helper thoát và đóng pipe.</summary>
    public void Shutdown()
    {
        if (!_gate.Wait(TimeSpan.FromSeconds(2))) return;
        try
        {
            if (_pipe is { IsConnected: true } pipe)
            {
                try { PipeMessage.WriteAsync(pipe, new HelperRequest { Command = HelperCommands.Quit, Id = -1 }).Wait(TimeSpan.FromSeconds(1)); } catch { }
            }
            Disconnect();
            if (Status == HelperStatus.Running) Status = HelperStatus.NotRunning;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Disconnect()
    {
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
        _process?.Dispose();
        _process = null;
    }

    public void Dispose()
    {
        Shutdown();
        _idleTimer?.Dispose();
    }
}
