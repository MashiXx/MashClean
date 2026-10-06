using CleanBoost.Core;
using CleanBoost.Engine.Rules;

namespace CleanBoost.Engine.Apps;

public enum AppSource
{
    /// <summary>Chương trình cài đặt truyền thống (khoá Uninstall trong registry).</summary>
    Desktop,
    /// <summary>App Microsoft Store / MSIX.</summary>
    Store,
}

/// <summary>Phạm vi khoá Uninstall chứa app.</summary>
public enum RegistryScope
{
    /// <summary>HKLM 64-bit — cài cho mọi người dùng.</summary>
    Machine,
    /// <summary>HKLM\WOW6432Node — app 32-bit cài cho mọi người dùng.</summary>
    Machine32,
    /// <summary>HKCU — cài cho riêng người dùng hiện tại.</summary>
    User,
    None,
}

/// <summary>Một ứng dụng đã cài (tương đương <c>InstalledApp</c> của bản Mac).</summary>
public sealed record InstalledApp
{
    /// <summary>Định danh duy nhất trong danh sách: nguồn + khoá registry / tên gói.</summary>
    public required string Id { get; init; }
    /// <summary>Định danh ổn định dùng cho rule và danh sách bỏ qua: tên khoá Uninstall hoặc PackageFamilyName.</summary>
    public required string AppID { get; init; }
    public required string Name { get; init; }
    public string? Version { get; init; }
    public string? Publisher { get; init; }
    public string? InstallLocation { get; init; }
    public string? UninstallString { get; init; }
    public string? QuietUninstallString { get; init; }
    public string? ModifyPath { get; init; }
    public string? DisplayIcon { get; init; }
    public bool IsWindowsInstaller { get; init; }
    public long Size { get; init; }
    public DateTime? InstallDate { get; init; }
    public DateTime? LastUsed { get; init; }
    public AppSource Source { get; init; }
    public RegistryScope Scope { get; init; }
    /// <summary>Đường dẫn khoá Uninstall (không gồm hive), vd <c>SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Foo</c>.</summary>
    public string? RegistryKeyPath { get; init; }
    /// <summary>Tên đầy đủ của gói Store (để gỡ bằng PackageManager).</summary>
    public string? PackageFullName { get; init; }
    public string? PackageFamilyName { get; init; }
    /// <summary>App của chính Windows/Microsoft (theo nhà phát hành trong knowledge): không coi là có file sót.</summary>
    public bool IsSystemPublisher { get; init; }
    /// <summary>Không gỡ được (thành phần hệ thống, gói Store không cho gỡ).</summary>
    public bool IsNonRemovable { get; init; }
    /// <summary>File exe chính của app (từ DisplayIcon hoặc thư mục cài đặt), dùng để biết app đang chạy.</summary>
    public IReadOnlyList<string> ExecutableNames { get; init; } = [];

    public bool IsStore => Source == AppSource.Store;
    /// <summary>Gỡ cần quyền quản trị (cài cho mọi người dùng).</summary>
    public bool RequiresAdminToUninstall => Scope is RegistryScope.Machine or RegistryScope.Machine32;

    public RuleAppInfo RuleInfo => new(AppID, Name, Publisher, InstallLocation, ExecutableNames);

    public string DisplayVersion => string.IsNullOrEmpty(Version) ? "" : Version;
}
