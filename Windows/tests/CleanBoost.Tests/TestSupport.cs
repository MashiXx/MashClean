using System.Diagnostics;
using System.Security.Principal;
using CleanBoost.Core;
using CleanBoost.Core.FileSystem;
using CleanBoost.Engine.Rules;
using Microsoft.Win32;

namespace CleanBoost.Tests;

/// <summary>
/// Thư mục tạm riêng cho một test, dưới <c>%TEMP%</c>, tự xoá khi Dispose. Đường dẫn gốc đã chuẩn hoá
/// (<see cref="PathPolicy.FinalPath"/>) để so sánh được với đường dẫn PathPolicy trả về.
/// Dọn dẹp không bao giờ đi theo junction/symlink: liên kết bị xoá trước, đích của nó không bị đụng.
/// </summary>
internal sealed class TempDir : IDisposable
{
    public string Root { get; }

    public TempDir(string tag = "t")
    {
        var raw = Path.Combine(Path.GetTempPath(), $"cb-test-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(raw);
        Root = PathPolicy.FinalPath(raw) ?? raw;
    }

    /// <summary>Đường dẫn tuyệt đối từ đường dẫn tương đối (chấp nhận cả <c>/</c>).</summary>
    public string P(string relative) => relative.Length == 0 ? Root : Path.Combine(Root, relative.Replace('/', '\\'));

    public string Dir(string relative)
    {
        var p = P(relative);
        Directory.CreateDirectory(p);
        return p;
    }

    /// <summary>Tạo file với <paramref name="size"/> byte; tuổi (ngày) đặt cho thời gian sửa và truy cập nếu có.</summary>
    public string File(string relative, int size = 16, int? ageDays = null)
    {
        var p = P(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var data = new byte[size];
        new Random(size).NextBytes(data);
        System.IO.File.WriteAllBytes(p, data);
        if (ageDays is { } d) Age(p, d);
        return p;
    }

    public bool Exists(string relative) => DirectoryReader.Exists(P(relative));

    /// <summary>Lùi thời gian tạo/sửa/truy cập của file hoặc thư mục.</summary>
    public static void Age(string path, int days)
    {
        var t = DateTime.UtcNow.AddDays(-days);
        if (Directory.Exists(path))
        {
            Directory.SetCreationTimeUtc(path, t);
            Directory.SetLastWriteTimeUtc(path, t);
            Directory.SetLastAccessTimeUtc(path, t);
        }
        else
        {
            System.IO.File.SetCreationTimeUtc(path, t);
            System.IO.File.SetLastWriteTimeUtc(path, t);
            System.IO.File.SetLastAccessTimeUtc(path, t);
        }
    }

    public void Dispose() => SafeDelete(Root);

    /// <summary>Xoá cây thư mục: liên kết xoá như liên kết (không đi vào), bỏ thuộc tính chỉ đọc.</summary>
    public static void SafeDelete(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists) return;
            DeleteChildren(info);
            info.Attributes = FileAttributes.Directory;
            info.Delete(false);
        }
        catch
        {
            // Dọn dẹp không được làm hỏng test; thư mục tạm còn sót sẽ do hệ điều hành dọn.
        }
    }

    private static void DeleteChildren(DirectoryInfo dir)
    {
        foreach (var entry in dir.EnumerateFileSystemInfos())
        {
            try
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    // Junction / symlink: chỉ xoá chính liên kết.
                    if (entry is DirectoryInfo d) Directory.Delete(d.FullName, false);
                    else entry.Delete();
                    continue;
                }
                entry.Attributes &= ~FileAttributes.ReadOnly;
                if (entry is DirectoryInfo sub)
                {
                    DeleteChildren(sub);
                    sub.Delete(false);
                }
                else
                {
                    entry.Delete();
                }
            }
            catch
            {
                // bỏ qua mục không xoá được
            }
        }
    }
}

internal static class Links
{
    /// <summary>Tạo junction bằng <c>mklink /J</c> (không cần quyền đặc biệt). Trả <c>false</c> nếu không tạo được.</summary>
    public static bool TryCreateJunction(string link, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
            return p.ExitCode == 0 && DirectoryReader.Stat(link) is { IsLink: true };
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Symlink thư mục (cần Developer Mode hoặc quyền SeCreateSymbolicLink).</summary>
    public static bool TryCreateDirectorySymlink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return DirectoryReader.Stat(link) is { IsLink: true };
        }
        catch
        {
            return false;
        }
    }

    public static bool TryCreateFileSymlink(string link, string target)
    {
        try
        {
            System.IO.File.CreateSymbolicLink(link, target);
            return DirectoryReader.Stat(link) is { IsLink: true };
        }
        catch
        {
            return false;
        }
    }

    public const string SymlinkSkipReason =
        "Không tạo được symlink: cần bật Developer Mode hoặc quyền SeCreateSymbolicLinkPrivilege.";

    public const string JunctionSkipReason = "Không tạo được junction (mklink /J) trên ổ chứa %TEMP%.";
}

internal static class TestPolicy
{
    private static readonly WindowsIdentity s_identity = WindowsIdentity.GetCurrent();

    public static SecurityIdentifier Sid { get; } = s_identity.User!;

    public static bool IsAdminMember { get; } = s_identity.Groups?.Any(g => g.Value == "S-1-5-32-544") ?? false;

    /// <summary>SID không phải của người dùng hiện tại, để mô phỏng file thuộc người khác.</summary>
    public static SecurityIdentifier ForeignSid { get; } = new("S-1-5-21-1111111111-2222222222-3333333333-1001");

    /// <summary>Chính sách người dùng với hồ sơ giả lập trong thư mục tạm.</summary>
    public static PathPolicy For(string home, params string[] extraProtected) =>
        new(PathPolicy.PolicyMode.User, Sid, IsAdminMember, home, extraProtected);
}

internal static class TestRules
{
    public static Rule Make(
        string id,
        string category = RuleCategory.UserCaches,
        string[]? paths = null,
        SafetyLevel safety = SafetyLevel.Safe,
        RemovalStrategy? removal = null,
        string[]? exclude = null,
        int? minAgeDays = null,
        long? minSizeBytes = null,
        bool? forEachApp = null,
        string[]? appNotRunning = null,
        string? minOS = null,
        string? provider = null,
        bool? requiresRoot = null,
        string[]? appIDs = null,
        string[]? appNames = null,
        string[]? publishers = null,
        LocalizedText? title = null,
        LocalizedText? reason = null) => new()
        {
            Id = id,
            Category = category,
            Title = title,
            Reason = reason,
            Safety = safety,
            Removal = removal ?? RemovalStrategy.Delete,
            RequiresRoot = requiresRoot,
            Exclude = exclude?.ToList(),
            Match = new Rule.MatchSpec
            {
                Paths = (paths ?? []).ToList(),
                ForEachInstalledApp = forEachApp,
                MinAgeDays = minAgeDays,
                MinSizeBytes = minSizeBytes,
                Provider = provider,
                AppIDs = appIDs?.ToList(),
                AppNames = appNames?.ToList(),
                Publishers = publishers?.ToList(),
            },
            Conditions = appNotRunning is null && minOS is null ? null : new Rule.ConditionSpec
            {
                AppNotRunning = appNotRunning?.ToList(),
                MinOS = minOS,
            },
        };
}

/// <summary>Thùng rác thật chỉ dùng khi chắc chắn không có hộp thoại "xoá vĩnh viễn" (sẽ treo test).</summary>
internal static class RecycleBinGuard
{
    public static string? SkipReason(string path)
    {
        if (!RecycleBin.IsAvailable(path)) return "Ổ chứa %TEMP% không có Thùng rác dùng được.";
        try
        {
            using var policy = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer");
            if (policy?.GetValue("NoRecycleFiles") is int n && n != 0) return "Chính sách NoRecycleFiles đang bật.";
            using var volumes = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume");
            if (volumes is not null)
            {
                foreach (var name in volumes.GetSubKeyNames())
                {
                    using var v = volumes.OpenSubKey(name);
                    if (v?.GetValue("NukeOnDelete") is int nuke && nuke != 0) return "Thùng rác được đặt xoá vĩnh viễn (NukeOnDelete) — hộp thoại cảnh báo sẽ treo test.";
                }
            }
        }
        catch
        {
            return "Không đọc được cấu hình Thùng rác.";
        }
        return null;
    }
}
