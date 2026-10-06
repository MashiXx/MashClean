using System.Windows.Threading;
using CleanBoost.Core;
using CleanBoost.Engine.Cleaning;
using CleanBoost.Engine.Metrics;
using CleanBoost.Engine.Persistence;

namespace CleanBoost.App.Tray;

/// <summary>Lịch sử 60 mẫu gần nhất của một chỉ số (MetricHistory của bản Mac).</summary>
public sealed class MetricHistory(int capacity = 60)
{
    private readonly Queue<double> _values = new(capacity + 1);

    public int Capacity { get; } = capacity;

    /// <summary>Bản chụp mới sau mỗi lần thêm (để control vẽ lại khi gán).</summary>
    public IReadOnlyList<double> Values { get; private set; } = [];

    public void Append(double value)
    {
        _values.Enqueue(double.IsFinite(value) ? value : 0);
        while (_values.Count > Capacity) _values.Dequeue();
        Values = _values.ToArray();
    }

    public double MaxValue => Values.Count == 0 ? 0 : Values.Max();
}

/// <summary>Loại cảnh báo của khay hệ thống; mỗi loại tối đa 1 lần / 24 giờ (MenuBarAlert của bản Mac).</summary>
public enum TrayAlertKind
{
    LowDisk,
    LargeTrash,
    MemoryPressure,
}

public static class TrayAlertKinds
{
    /// <summary>Tên lưu trong <c>alerts.last.&lt;kind&gt;</c>, giống bản Mac.</summary>
    public static string RawValue(this TrayAlertKind kind) => kind switch
    {
        TrayAlertKind.LowDisk => "lowDisk",
        TrayAlertKind.LargeTrash => "largeTrash",
        _ => "memoryPressure",
    };

    public static string Title(this TrayAlertKind kind) => kind switch
    {
        TrayAlertKind.LowDisk => Loc.T("Ổ đĩa sắp đầy"),
        TrayAlertKind.LargeTrash => Loc.T("Thùng rác đang chiếm nhiều dung lượng"),
        _ => Loc.T("Máy đang thiếu bộ nhớ"),
    };
}

/// <summary>
/// Trạng thái khay hệ thống (MenuBarModel của bản Mac): lấy mẫu số liệu trên luồng nền theo chu kỳ, giữ lịch sử cho biểu đồ,
/// đo Thùng rác (tối đa 10 phút/lần), "Đã dọn tháng này", ổ đĩa ngoài, và cảnh báo. Mọi thuộc tính đổi trên luồng giao diện;
/// <see cref="Changed"/> phát sau mỗi lần cập nhật.
/// </summary>
public sealed class TrayModel : IDisposable
{
    /// <summary>Chu kỳ: 2 giây khi popup mở; khi đóng: 3 giây nếu biểu tượng hiện mạng, 5 giây nếu hiện CPU/RAM, 30 giây nếu chỉ biểu tượng.</summary>
    public static readonly TimeSpan OpenInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan ClosedInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan LiveNetworkInterval = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan LiveMetricsInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TrashInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CriticalPressureDuration = TimeSpan.FromMinutes(5);

    private readonly AppSettings _settings;
    private readonly Storage? _storage;
    private readonly Dispatcher _dispatcher;
    private readonly SystemSampler _sampler = new();
    private readonly Timer _timer;
    private readonly HashSet<string> _ejecting = new(StringComparer.OrdinalIgnoreCase);
    private TrayStatusParts _style;
    private TimeSpan _currentInterval = Timeout.InfiniteTimeSpan;
    private bool _running;
    private int _sampling;
    private bool _measuringTrash;
    private DateTime? _trashMeasuredAt;
    private long? _trashBytes;
    private DateTime? _criticalSince;
    private IReadOnlyList<ExternalVolume>? _demoVolumes;

    public TrayModel(AppSettings settings, Storage? storage, Dispatcher dispatcher)
    {
        _settings = settings;
        _storage = storage;
        _dispatcher = dispatcher;
        _style = TrayStatusStyle.Parse(settings.MenuBarStatusStyle);
        _timer = new Timer(_ => _dispatcher.InvokeAsync(Tick, DispatcherPriority.Background));
    }

    public SystemSnapshot? Snapshot { get; private set; }
    public MetricHistory CpuHistory { get; } = new();
    public MetricHistory MemoryHistory { get; } = new();
    public MetricHistory DiskHistory { get; } = new();
    public MetricHistory BatteryHistory { get; } = new();
    public MetricHistory NetworkInHistory { get; } = new();
    public MetricHistory NetworkOutHistory { get; } = new();
    public long FreedThisMonth { get; private set; }
    public IReadOnlyList<ExternalVolume> ExternalVolumes { get; private set; } = [];
    public bool TrashUnreadable { get; private set; }
    public string? EjectError { get; private set; }
    public bool IsEjecting(ExternalVolume volume) => _ejecting.Contains(volume.Id);
    public bool IsPopupOpen { get; private set; }

    /// <summary>Tắt khi chụp ảnh kiểm thử để không ghi <c>alerts.last.*</c> và không hiện thông báo.</summary>
    public bool AlertsEnabled { get; set; } = true;

    /// <summary>Ghi đè chu kỳ lấy mẫu (chụp ảnh kiểm thử).</summary>
    public TimeSpan? IntervalOverride { get; set; }

    public event Action? Changed;
    public event Action<TrayAlertKind, string>? AlertRaised;

    public TrayStatusParts Style
    {
        get => _style;
        set
        {
            if (_style == value) return;
            _style = value;
            if (_running) Schedule();
            Changed?.Invoke();
        }
    }

    // MARK: Vòng đời

    public void Start()
    {
        if (_running) return;
        _running = true;
        CleanEngine.DidClean += OnDidClean;
        RefreshVolumes();
        RefreshFreed();
        Tick();
        Schedule();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        CleanEngine.DidClean -= OnDidClean;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        _currentInterval = Timeout.InfiniteTimeSpan;
    }

    public void Dispose()
    {
        Stop();
        _timer.Dispose();
    }

    public void SetPopupOpen(bool open)
    {
        if (open == IsPopupOpen) return;
        IsPopupOpen = open;
        if (open)
        {
            Tick();
            RefreshVolumes();
            RefreshFreed();
        }
        if (_running) Schedule();
    }

    public TimeSpan CurrentInterval =>
        IntervalOverride ?? (IsPopupOpen ? OpenInterval
            : _style.HasFlag(TrayStatusParts.Network) ? LiveNetworkInterval
            : _style == TrayStatusParts.None ? ClosedInterval
            : LiveMetricsInterval);

    private void Schedule()
    {
        var interval = CurrentInterval;
        if (interval == _currentInterval) return;
        _currentInterval = interval;
        _timer.Change(interval, interval);
    }

    private void OnDidClean() => _dispatcher.InvokeAsync(() =>
    {
        _trashMeasuredAt = null;
        RefreshFreed();
    });

    // MARK: Lấy mẫu

    /// <summary>Lấy một mẫu trên luồng nền (bỏ qua nếu mẫu trước chưa xong).</summary>
    public void Tick()
    {
        if (!_running && !IsPopupOpen) return;
        if (Interlocked.Exchange(ref _sampling, 1) == 1) return;
        var trash = _trashBytes;
        _ = Task.Run(() =>
        {
            try
            {
                var snap = _sampler.Sample(trash);
                _dispatcher.InvokeAsync(() => Apply(snap));
            }
            catch (Exception ex)
            {
                Log.Warning(LogCategory.Menu, "Lấy mẫu số liệu lỗi: " + ex.Message);
            }
            finally
            {
                Volatile.Write(ref _sampling, 0);
            }
        });
        MeasureTrashIfNeeded();
    }

    private void Apply(SystemSnapshot snap)
    {
        snap = snap with { TrashBytes = _trashBytes };
        if (DemoPressure is { } pressure) snap = snap with { Memory = snap.Memory with { Pressure = pressure } };
        Snapshot = snap;
        if (snap.Cpu is { } cpu) CpuHistory.Append(cpu.Usage);
        if (snap.Memory.Total > 0) MemoryHistory.Append(snap.Memory.UsedFraction);
        if (snap.Disk is { } disk) DiskHistory.Append(disk.UsedFraction);
        if (snap.Battery.HasBattery) BatteryHistory.Append(snap.Battery.Level);
        if (snap.Network is { } net)
        {
            NetworkInHistory.Append(net.InPerSecond);
            NetworkOutHistory.Append(net.OutPerSecond);
        }
        PressureChanged(snap.Memory.Pressure);
        EvaluateAlerts(snap);
        Changed?.Invoke();
    }

    private void MeasureTrashIfNeeded()
    {
        if (_measuringTrash) return;
        if (_trashMeasuredAt is { } at && DateTime.UtcNow - at < TrashInterval) return;
        _measuringTrash = true;
        _ = Task.Run(() =>
        {
            (long Size, long Count)? result = null;
            try { result = RecycleBin.Query(); } catch (Exception ex) { Log.Warning(LogCategory.Menu, "Không đọc được Thùng rác: " + ex.Message); }
            _dispatcher.InvokeAsync(() =>
            {
                _measuringTrash = false;
                _trashMeasuredAt = DateTime.UtcNow;
                _trashBytes = result?.Size;
                TrashUnreadable = result is null;
                if (Snapshot is { } snap) Snapshot = snap with { TrashBytes = _trashBytes };
                if (_trashBytes is { } bytes) EvaluateTrash(bytes);
                Changed?.Invoke();
            });
        });
    }

    // MARK: Cảnh báo

    private void PressureChanged(MemoryPressure level)
    {
        if (level == MemoryPressure.Critical) _criticalSince ??= DateTime.UtcNow;
        else _criticalSince = null;
    }

    private void EvaluateAlerts(SystemSnapshot snap)
    {
        if (_settings.LowDiskAlerts && snap.Disk is { Total: > 0 } disk && (disk.FreeFraction < 0.10 || disk.Available < 10_000_000_000))
            Raise(TrayAlertKind.LowDisk, Loc.F("Chỉ còn %@ trống trên %@. Bấm để quét và dọn dẹp.", ByteCount.Format(disk.Available), disk.Name));
        if (_criticalSince is { } since && DateTime.UtcNow - since >= CriticalPressureDuration)
            Raise(TrayAlertKind.MemoryPressure, Loc.T("Memory pressure ở mức nguy cấp hơn 5 phút. Hãy đóng bớt ứng dụng hoặc kiểm tra với Clean Boost."));
    }

    private void EvaluateTrash(long bytes)
    {
        var limitGB = _settings.TrashAlertGB;
        if (limitGB <= 0 || bytes <= limitGB * 1_000_000_000L) return;
        Raise(TrayAlertKind.LargeTrash, Loc.F("Thùng rác đang chiếm %@. Bấm để dọn dẹp.", ByteCount.Format(bytes)));
    }

    private void Raise(TrayAlertKind kind, string body)
    {
        if (!AlertsEnabled || AlertRaised is null) return;
        var raw = kind.RawValue();
        if (!_settings.ShouldAlert(raw)) return;
        _settings.MarkAlerted(raw);
        Log.Info(LogCategory.Menu, "Cảnh báo " + raw);
        AlertRaised.Invoke(kind, body);
    }

    // MARK: Đã dọn tháng này

    public void RefreshFreed()
    {
        if (_storage is not { } storage) return;
        _ = Task.Run(() =>
        {
            try
            {
                var value = storage.FreedThisMonth();
                _dispatcher.InvokeAsync(() =>
                {
                    FreedThisMonth = value;
                    Changed?.Invoke();
                });
            }
            catch (Exception ex)
            {
                Log.Warning(LogCategory.Menu, "Không đọc được thống kê dọn dẹp: " + ex.Message);
            }
        });
    }

    // MARK: Ổ đĩa ngoài

    public void RefreshVolumes()
    {
        if (_demoVolumes is not null) return;
        _ = Task.Run(() =>
        {
            IReadOnlyList<ExternalVolume> volumes;
            try { volumes = ExternalDrives.List(); } catch (Exception ex) { Log.Warning(LogCategory.Menu, "Không liệt kê được ổ ngoài: " + ex.Message); return; }
            _dispatcher.InvokeAsync(() =>
            {
                if (_demoVolumes is not null || volumes.SequenceEqual(ExternalVolumes)) return;
                ExternalVolumes = volumes;
                Changed?.Invoke();
            });
        });
    }

    public void Eject(ExternalVolume volume)
    {
        if (!_ejecting.Add(volume.Id)) return;
        EjectError = null;
        Changed?.Invoke();
        _ = Task.Run(() =>
        {
            var error = ExternalDrives.Eject(volume.Root);
            Log.Info(LogCategory.Menu, error is null ? "Đã tháo ổ ngoài" : "Không tháo được ổ ngoài: " + error);
            _dispatcher.InvokeAsync(() =>
            {
                _ejecting.Remove(volume.Id);
                EjectError = error is null ? null : Loc.F("Không tháo được %@: %@", volume.Name, error);
                Changed?.Invoke();
                RefreshVolumes();
            });
        });
    }

    /// <summary>Mức RAM giả cho ảnh chụp kiểm thử (hiện nhãn "RAM: Cao").</summary>
    internal MemoryPressure? DemoPressure { get; set; }

    /// <summary>Dữ liệu mẫu cho ảnh chụp kiểm thử: một ổ ngoài và một lỗi tháo ổ.</summary>
    internal void UseDemoVolumes()
    {
        _demoVolumes =
        [
            new ExternalVolume("E:\\", "SANDISK (E:)", 64_000_000_000, 41_200_000_000),
            new ExternalVolume("F:\\", "Backup (F:)", 1_000_000_000_000, 312_000_000_000),
        ];
        ExternalVolumes = _demoVolumes;
        EjectError = Loc.F("Không tháo được %@: %@", "Backup (F:)", Loc.T("Ổ đang được một chương trình khác sử dụng."));
        Changed?.Invoke();
    }

    // MARK: Chú thích biểu tượng khay

    /// <summary>Một chỉ số hiện trên khay: tên đầy đủ + giá trị (StatusPart của bản Mac).</summary>
    public readonly record struct StatusPart(string Label, string Value);

    public IReadOnlyList<StatusPart> StatusParts
    {
        get
        {
            var parts = new List<StatusPart>(4);
            var snap = Snapshot;
            if (_style.HasFlag(TrayStatusParts.Cpu))
                parts.Add(new StatusPart("CPU", snap?.Cpu is { } cpu ? TrayFormat.Percent(cpu.Usage) : "–"));
            if (_style.HasFlag(TrayStatusParts.Memory))
                parts.Add(new StatusPart("RAM", snap is { Memory.Total: > 0 } ? TrayFormat.Percent(snap.Memory.UsedFraction) : "–"));
            if (_style.HasFlag(TrayStatusParts.Network))
            {
                var net = snap?.Network;
                parts.Add(new StatusPart(Loc.T("Tải về"), net is null ? "–" : TrayFormat.CompactRate(net.InPerSecond)));
                parts.Add(new StatusPart(Loc.T("Tải lên"), net is null ? "–" : TrayFormat.CompactRate(net.OutPerSecond)));
            }
            return parts;
        }
    }

    /// <summary>"Clean Boost — CPU 12% · RAM 48% · Tải về 850 KB/s · Tải lên 12 KB/s", hoặc "Clean Boost" khi chỉ hiện biểu tượng.</summary>
    public string StatusText
    {
        get
        {
            var parts = StatusParts;
            return parts.Count == 0 ? "Clean Boost" : "Clean Boost — " + string.Join(" · ", parts.Select(p => p.Label + " " + p.Value));
        }
    }
}
