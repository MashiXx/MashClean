using CleanBoost.Core;
using CleanBoost.Engine.Nodes;

namespace CleanBoost.Features.StartupItems;

/// <summary>Nơi đăng ký một mục khởi động (tương đương thư mục LaunchAgents/LaunchDaemons của bản Mac).</summary>
public enum StartupSource
{
    /// <summary>Giá trị trong khoá <c>...\CurrentVersion\Run</c>.</summary>
    Run,
    /// <summary>Giá trị trong khoá <c>...\CurrentVersion\RunOnce</c>: chạy một lần ở lần đăng nhập tới rồi tự xoá.</summary>
    RunOnce,
    /// <summary>File (thường là lối tắt .lnk) trong thư mục Startup.</summary>
    StartupFolder,
    /// <summary>Tác vụ lập lịch có trình kích hoạt lúc đăng nhập hoặc lúc khởi động máy.</summary>
    ScheduledTask,
}

/// <summary>Nhóm hiển thị trên màn Mục khởi động.</summary>
public enum StartupGroup
{
    Registry,
    StartupFolder,
    ScheduledTask,
}

/// <summary>Phạm vi: của riêng người dùng hiện tại (HKCU) hay của cả máy (HKLM, HKLM 32-bit, thư mục Startup chung, tác vụ hệ thống).</summary>
public enum StartupScope
{
    User,
    Machine,
    Machine32,
}

public static class StartupEnums
{
    /// <summary>Giá trị phạm vi dùng với <see cref="StartupRegistry"/> và helper (<c>user</c>/<c>machine</c>/<c>machine32</c>).</summary>
    public static string RegistryValue(this StartupScope scope) => scope switch
    {
        StartupScope.User => StartupRegistry.ScopeUser,
        StartupScope.Machine => StartupRegistry.ScopeMachine,
        _ => StartupRegistry.ScopeMachine32,
    };

    public static StartupGroup Group(this StartupSource source) => source switch
    {
        StartupSource.Run or StartupSource.RunOnce => StartupGroup.Registry,
        StartupSource.StartupFolder => StartupGroup.StartupFolder,
        _ => StartupGroup.ScheduledTask,
    };

    public static string Title(this StartupGroup group) => group switch
    {
        StartupGroup.Registry => Loc.T("Khởi động cùng Windows (Run)"),
        StartupGroup.StartupFolder => Loc.T("Thư mục Startup"),
        _ => Loc.T("Tác vụ lập lịch"),
    };

    /// <summary>Nhãn ngắn của nguồn + phạm vi, dùng làm badge ("Run · máy").</summary>
    public static string ShortTitle(this StartupItem item)
    {
        var source = item.Source switch
        {
            StartupSource.Run => "Run",
            StartupSource.RunOnce => "RunOnce",
            StartupSource.StartupFolder => Loc.T("Thư mục Startup"),
            _ => Loc.T("Tác vụ lập lịch"),
        };
        return item.Scope == StartupScope.User ? Loc.F("%@ (người dùng)", source) : Loc.F("%@ (máy)", source);
    }
}

/// <summary>
/// Một mục khởi động cùng Windows (LaunchItem của bản Mac): lệnh, chương trình đích đã giải, app sở hữu, trạng thái bật/tắt
/// và cờ <see cref="IsBroken"/> khi chương trình đích không còn tồn tại.
/// </summary>
public sealed record StartupItem
{
    /// <summary>Khoá duy nhất: nguồn + phạm vi + tên/đường dẫn.</summary>
    public required string Id { get; init; }
    public required StartupSource Source { get; init; }
    public required StartupScope Scope { get; init; }
    /// <summary>Tên giá trị registry, tên file trong thư mục Startup (gồm đuôi) hoặc tên tác vụ.</summary>
    public required string Name { get; init; }
    /// <summary>Lệnh đầy đủ như Windows sẽ chạy (chưa mở biến môi trường).</summary>
    public string Command { get; init; } = "";
    /// <summary>Chương trình đích đã giải (exe, hoặc dll/script khi lệnh là rundll32/wscript/cmd /c...).</summary>
    public string? TargetPath { get; init; }
    /// <summary><c>true</c>/<c>false</c> khi chắc chắn; <c>null</c> khi không xác định được (đường dẫn tương đối, ổ mạng, bị từ chối quyền đọc).</summary>
    public bool? TargetExists { get; init; }
    /// <summary>Chương trình đích nằm trên ổ cứng cố định của máy (không phải ổ mạng/USB/ổ đang vắng mặt).</summary>
    public bool TargetOnFixedDrive { get; init; }
    /// <summary>File trong thư mục Startup (với <see cref="StartupSource.StartupFolder"/>).</summary>
    public string? ShortcutPath { get; init; }
    /// <summary>Đường dẫn đầy đủ của tác vụ lập lịch, vd <c>\ASUS\Framework Service</c>.</summary>
    public string? TaskPath { get; init; }
    public bool IsEnabled { get; init; } = true;
    /// <summary>Đổi trạng thái hoặc xoá cần quyền quản trị (HKLM, thư mục Startup chung, tác vụ của hệ thống).</summary>
    public bool RequiresAdmin { get; init; }
    /// <summary>Tên app sở hữu: app đã cài có thư mục chứa chương trình đích, hoặc ProductName của file.</summary>
    public string? OwnerName { get; init; }
    /// <summary>App sở hữu tìm thấy trong danh sách app đã cài.</summary>
    public bool OwnerIsInstalledApp { get; init; }
    public string? Publisher { get; init; }
    /// <summary>Mô tả của file chương trình (FileDescription), như cột "Tên" của Task Manager.</summary>
    public string? FileDescription { get; init; }
    /// <summary>Mô tả của tác vụ lập lịch (RegistrationInfo.Description).</summary>
    public string? TaskDescription { get; init; }
    public bool RunsAtLogon { get; init; }
    public bool RunsAtBoot { get; init; }
    public DateTime? LastRunTime { get; init; }
    /// <summary>Chương trình đích đang chạy lúc quét.</summary>
    public bool IsRunning { get; init; }
    /// <summary>Thành phần của Windows: chương trình đích (còn tồn tại) nằm trong <c>%WINDIR%</c>. Không cho xoá, chỉ cho tắt.</summary>
    public bool IsSystemComponent { get; init; }
    /// <summary>Dung lượng dữ liệu của mục (giá trị registry hoặc file lối tắt).</summary>
    public long DataSize { get; init; }

    public bool IsBroken => TargetExists == false;
    public StartupGroup Group => Source.Group();

    /// <summary>Tên gọn: tên file lối tắt không đuôi, còn lại là tên giá trị/tác vụ.</summary>
    public string EntryName => Source == StartupSource.StartupFolder ? Path.GetFileNameWithoutExtension(Name) : Name;

    /// <summary>
    /// Tên hiển thị giống Task Manager: mục Run dùng mô tả của file chương trình (thường là tên sản phẩm), lối tắt và tác vụ
    /// dùng chính tên của chúng (do người dùng/bộ cài đặt tên, dễ nhận ra hơn).
    /// </summary>
    public string DisplayName => Source is StartupSource.Run or StartupSource.RunOnce ? FirstNonEmpty(FileDescription, EntryName) : FirstNonEmpty(EntryName, FileDescription);

    /// <summary>Nhóm khởi động dùng với <see cref="StartupRegistry"/>: <c>Run</c>, <c>RunOnce</c>, <c>StartupFolder</c>.</summary>
    public string? RegistryLocation => Source switch
    {
        StartupSource.Run => StartupRegistry.LocationRun,
        StartupSource.RunOnce => StartupRegistry.LocationRunOnce,
        StartupSource.StartupFolder => StartupRegistry.LocationStartupFolder,
        _ => null,
    };

    /// <summary>Tên giá trị trong <c>StartupApproved</c>: tên giá trị Run, hoặc tên file (gồm đuôi) trong thư mục Startup.</summary>
    public string ApprovedName => Name;

    /// <summary>Khoá registry chứa mục (dạng hiển thị).</summary>
    public string? RegistryKeyDisplay => Source is StartupSource.Run or StartupSource.RunOnce
        ? (Scope switch
        {
            StartupScope.User => @"HKCU\Software\Microsoft\Windows\CurrentVersion\",
            StartupScope.Machine => @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\",
            _ => @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\",
        }) + RegistryLocation
        : null;

    /// <summary>Vị trí đầy đủ của mục (ghi vào lịch sử dọn): khoá\tên giá trị, đường dẫn file hoặc đường dẫn tác vụ.</summary>
    public string Label => Source switch
    {
        StartupSource.Run or StartupSource.RunOnce => RegistryKeyDisplay + "\\" + Name,
        StartupSource.StartupFolder => ShortcutPath ?? Name,
        _ => TaskPath ?? Name,
    };

    /// <summary>Tắt/bật được không: mục hỏng chỉ có thể xoá; RunOnce không có trạng thái bật/tắt.</summary>
    public bool CanToggle => !IsBroken && Source != StartupSource.RunOnce;

    public string? ToggleUnavailableReason =>
        IsBroken ? Loc.T("Chương trình không còn tồn tại, chỉ có thể xoá")
        : Source == StartupSource.RunOnce ? Loc.T("Mục RunOnce chỉ chạy một lần ở lần đăng nhập tới, chỉ có thể xoá")
        : null;

    /// <summary>Đường dẫn để "Hiện trong File Explorer": lối tắt, hoặc chương trình đích nếu còn.</summary>
    public string? RevealPath => ShortcutPath ?? (TargetExists == true ? TargetPath : null);

    /// <summary>Mô tả thời điểm chạy của tác vụ lập lịch.</summary>
    public string? TriggerDescription => (RunsAtLogon, RunsAtBoot) switch
    {
        (true, true) => Loc.T("Khi khởi động máy và khi đăng nhập"),
        (false, true) => Loc.T("Khi khởi động máy"),
        (true, false) => Loc.T("Khi đăng nhập"),
        _ => null,
    };

    private static string FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";
}

/// <summary>Mục khởi động đưa vào cây kết quả (Smart Scan, xoá mục hỏng), xử lý bởi <see cref="StartupItemRemover"/>.</summary>
public sealed record StartupItemEntry(StartupItem Item) : VirtualItem
{
    public override string? AssociatedPath => Item.ShortcutPath;
    public override string DefaultTitle => Item.DisplayName;
}
