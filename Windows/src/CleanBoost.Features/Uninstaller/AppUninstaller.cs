using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using CleanBoost.Core;
using CleanBoost.Core.Native;
using CleanBoost.Engine.Apps;
using CleanBoost.Engine.Cleaning;
using CleanBoost.Engine.Nodes;
using CleanBoost.Engine.Rules;
using Microsoft.Win32;
using Windows.Management.Deployment;

namespace CleanBoost.Features.Uninstaller;

/// <summary>
/// Mục ảo "chạy trình gỡ của app" (tương đương node bundle <c>.app</c> + AppRemover của bản Mac, spec mục 4.7).
/// <see cref="Location"/> là thư mục cài đặt khi nó không nằm trong vùng cấm, để hiện đường dẫn và đo dung lượng ổ.
/// </summary>
public sealed record InstalledAppItem(InstalledApp App, string? Location) : VirtualItem
{
    public override string? AssociatedPath => Location;
    public override string DefaultTitle => App.Name;
}

public enum UninstallKind
{
    /// <summary>Gói Microsoft Store: <c>PackageManager.RemovePackageAsync</c>.</summary>
    Store,
    /// <summary>Windows Installer: <c>msiexec.exe /x {ProductCode}</c>.</summary>
    WindowsInstaller,
    /// <summary>Trình gỡ riêng của hãng (UninstallString).</summary>
    Executable,
}

/// <summary>
/// Lệnh gỡ đã phân tích từ khoá Uninstall. Không bao giờ chạy qua <c>cmd.exe</c>: đường dẫn exe tách khỏi tham số,
/// tham số giữ nguyên văn như hãng ghi (trình gỡ NSIS, Inno Setup... tự phân tích dòng lệnh của mình).
/// </summary>
public sealed record UninstallCommand(UninstallKind Kind, string? Executable, string Arguments, string? ProductCode, string? PackageFullName)
{
    private static readonly Regex s_guid = new(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}", RegexOptions.Compiled);

    /// <summary>Trình thông dịch lệnh: từ chối để không chạy lệnh shell lấy từ registry.</summary>
    private static readonly HashSet<string> s_interpreters = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe", "bash.exe", "wsl.exe", "conhost.exe", "explorer.exe",
    };

    public static string MsiExec => ProcessRunner.System32("msiexec.exe");

    /// <summary>Chuỗi hiển thị (chỉ để log/hiện cho người dùng).</summary>
    public string Display => Kind switch
    {
        UninstallKind.Store => "PackageManager.RemovePackage " + PackageFullName,
        UninstallKind.WindowsInstaller => $"msiexec.exe /x {ProductCode}",
        _ => $"\"{Executable}\" {Arguments}".TrimEnd(),
    };

    /// <summary>Lệnh gỡ của app, hoặc null kèm lý do (bằng ngôn ngữ giao diện) nếu không chạy được an toàn.</summary>
    public static UninstallCommand? For(InstalledApp app, out string? problem)
    {
        problem = null;
        if (app.IsStore)
        {
            if (!string.IsNullOrEmpty(app.PackageFullName)) return new UninstallCommand(UninstallKind.Store, null, "", null, app.PackageFullName);
            problem = Loc.T("Không đọc được tên gói Microsoft Store");
            return null;
        }
        if (app.IsWindowsInstaller && s_guid.Match(app.AppID) is { Success: true } keyGuid && keyGuid.Value.Length == app.AppID.Length)
            return new UninstallCommand(UninstallKind.WindowsInstaller, MsiExec, "/x " + keyGuid.Value, keyGuid.Value, null);
        var raw = !string.IsNullOrWhiteSpace(app.UninstallString) ? app.UninstallString : app.QuietUninstallString;
        if (string.IsNullOrWhiteSpace(raw))
        {
            problem = Loc.T("Chương trình không đăng ký lệnh gỡ cài đặt");
            return null;
        }
        if (Split(raw) is not { } parts)
        {
            problem = Loc.T("Không đọc được lệnh gỡ cài đặt");
            return null;
        }
        var (exe, args) = parts;
        var fileName = Path.GetFileName(exe);
        if (fileName.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) || fileName.Equals("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            if (s_guid.Match(args) is { Success: true } g) return new UninstallCommand(UninstallKind.WindowsInstaller, MsiExec, "/x " + g.Value, g.Value, null);
            problem = Loc.T("Không đọc được lệnh gỡ cài đặt");
            return null;
        }
        if (s_interpreters.Contains(fileName) || s_interpreters.Contains(fileName + ".exe"))
        {
            problem = Loc.F("Lệnh gỡ chạy qua %@. Hãy gỡ chương trình này trong Cài đặt Windows", fileName);
            return null;
        }
        if (!Path.IsPathFullyQualified(exe))
        {
            // Chỉ chấp nhận chương trình hệ thống quen thuộc khi không có đường dẫn đầy đủ. Gói portable của WinGet ghi
            // "winget uninstall --product-code ..." (alias trong %LOCALAPPDATA%\Microsoft\WindowsApps).
            if (fileName.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase)) exe = ProcessRunner.System32("rundll32.exe");
            else if (fileName.Equals("winget", StringComparison.OrdinalIgnoreCase) || fileName.Equals("winget.exe", StringComparison.OrdinalIgnoreCase))
                exe = Path.Combine(KnownPaths.LocalAppData, "Microsoft", "WindowsApps", "winget.exe");
            else
            {
                problem = Loc.T("Không đọc được lệnh gỡ cài đặt");
                return null;
            }
        }
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            problem = Loc.T("Không đọc được lệnh gỡ cài đặt");
            return null;
        }
        if (!File.Exists(exe))
        {
            problem = Loc.F("Không tìm thấy trình gỡ cài đặt: %@", exe);
            return null;
        }
        return new UninstallCommand(UninstallKind.Executable, exe, args, null, null);
    }

    /// <summary>
    /// Tách đường dẫn exe và phần tham số: <c>"C:\a b\x.exe" /S</c>, <c>C:\Program Files\X\uninst.exe /S</c> (không ngoặc kép,
    /// có khoảng trắng: lấy tới ".exe" đầu tiên), <c>MsiExec.exe /X{GUID}</c>.
    /// </summary>
    public static (string Exe, string Args)? Split(string command)
    {
        var c = Environment.ExpandEnvironmentVariables(command.Trim().Trim('\0'));
        if (c.Length == 0) return null;
        if (c[0] == '"')
        {
            var end = c.IndexOf('"', 1);
            if (end <= 1) return null;
            return (c[1..end].Trim(), c[(end + 1)..].Trim());
        }
        var search = 0;
        while (true)
        {
            var i = c.IndexOf(".exe", search, StringComparison.OrdinalIgnoreCase);
            if (i < 0) break;
            var after = i + 4;
            if (after == c.Length || char.IsWhiteSpace(c[after]) || c[after] == '"')
                return (c[..after].Trim(), c[after..].Trim());
            search = after;
        }
        var space = c.IndexOf(' ');
        return space < 0 ? (c, "") : (c[..space], c[(space + 1)..].Trim());
    }
}

/// <summary>
/// Chạy trình gỡ của app rồi chờ tới khi gỡ xong (bước "chuyển app vào Thùng rác" của bản Mac). Thành công = khoá Uninstall
/// không còn (hoặc gói Store không còn). Trình gỡ chạy qua ShellExecute để tự hỏi UAC nếu cần.
/// </summary>
public static class AppUninstallRunner
{
    /// <summary>Thời gian chờ tối đa sau khi trình gỡ chính thoát mà vẫn còn tiến trình con (NSIS chép mình sang %TEMP%...).</summary>
    public static TimeSpan MaxWait { get; set; } = TimeSpan.FromMinutes(10);

    private const int ERROR_CANCELLED = 1223;
    private const int ERROR_INSTALL_USEREXIT = 1602;
    private const int ERROR_UNKNOWN_PRODUCT = 1605;
    private const int ERROR_SUCCESS_REBOOT_INITIATED = 1641;
    private const int ERROR_SUCCESS_REBOOT_REQUIRED = 3010;

    private static readonly string[] s_uninstallerHints = ["unins", "uninst", "un_a", "au_", "setup", "install", "remove", "cleanup"];

    /// <summary>App không còn được đăng ký: khoá Uninstall đã xoá / gói Store đã gỡ.</summary>
    public static bool IsGone(InstalledApp app)
    {
        try
        {
            if (app.IsStore)
            {
                // Theo họ gói, không theo tên đầy đủ: app Store tự cập nhật đổi phiên bản trong tên đầy đủ nhưng vẫn còn cài.
                if (!string.IsNullOrEmpty(app.PackageFamilyName)) return CurrentStorePackage(app) is null;
                if (string.IsNullOrEmpty(app.PackageFullName)) return false;
                return new PackageManager().FindPackageForUser(string.Empty, app.PackageFullName) is null;
            }
            if (app.RegistryKeyPath is not { Length: > 0 } path) return false;
            var hive = app.Scope == RegistryScope.User ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(path);
            return key is null;
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory.Clean, "Không kiểm tra được trạng thái gỡ: " + ex.Message);
            return false;
        }
    }

    /// <summary>Tên đầy đủ của gói Store đang cài thuộc họ gói của app (null nếu không còn).</summary>
    private static string? CurrentStorePackage(InstalledApp app)
    {
        if (string.IsNullOrEmpty(app.PackageFamilyName)) return null;
        foreach (var p in new PackageManager().FindPackagesForUser(string.Empty, app.PackageFamilyName))
        {
            if (!p.IsFramework && !p.IsResourcePackage) return p.Id.FullName;
        }
        return null;
    }

    public static async Task<RemoveResult> RunAsync(InstalledApp app, UninstallCommand command, CancellationToken ct)
    {
        var label = app.Name;
        if (IsGone(app)) return RemoveResult.Ok(label, app.Size);
        if (command.Kind == UninstallKind.Store) return await RemoveStoreAsync(app, command, ct).ConfigureAwait(false);

        var exe = command.Executable!;
        var before = ProcessIDs();
        var started = DateTime.UtcNow;
        Process? process;
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Arguments = command.Arguments,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            };
            Log.Info(LogCategory.Clean, $"Chạy trình gỡ của {app.Name} ({command.Kind})");
            process = Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            return RemoveResult.Skipped(label, SkipReason.UserDeclined);
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Clean, "Không chạy được trình gỡ", ex);
            return RemoveResult.Failed(label, Win32.ERROR_IO_DEVICE, Loc.F("Không chạy được trình gỡ cài đặt: %@", ex.Message));
        }

        int? exitCode = null;
        try
        {
            if (process is not null)
            {
                using (process)
                {
                    await process.WaitForExitAsync(ct).ConfigureAwait(false);
                    try { exitCode = process.ExitCode; } catch { exitCode = null; }
                }
            }
        }
        catch (OperationCanceledException)
        {
            return IsGone(app) ? RemoveResult.Ok(label, app.Size) : RemoveResult.Skipped(label, SkipReason.UserDeclined);
        }

        if (IsGone(app)) return RemoveResult.Ok(label, app.Size);
        if (exitCode == ERROR_INSTALL_USEREXIT) return RemoveResult.Skipped(label, SkipReason.UserDeclined);
        if (exitCode == ERROR_UNKNOWN_PRODUCT && command.Kind == UninstallKind.WindowsInstaller)
            return RemoveResult.Failed(label, ERROR_UNKNOWN_PRODUCT, Loc.T("Windows Installer không còn biết sản phẩm này. Hãy gỡ trong Cài đặt Windows."));

        // Trình gỡ chính đã thoát nhưng app vẫn được đăng ký: thường là trình gỡ chép mình sang %TEMP% rồi chạy bản sao
        // (NSIS, Inno Setup), hoặc tự nâng quyền. Chờ khi còn tiến trình kiểu "trình gỡ" mới xuất hiện.
        var idleChecks = 0;
        while (DateTime.UtcNow - started < MaxWait)
        {
            try
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return IsGone(app) ? RemoveResult.Ok(label, app.Size) : RemoveResult.Skipped(label, SkipReason.UserDeclined);
            }
            if (IsGone(app)) return RemoveResult.Ok(label, app.Size);
            if (HasActiveUninstaller(app, exe, before)) idleChecks = 0;
            else if (++idleChecks >= 4) break;
        }
        if (IsGone(app)) return RemoveResult.Ok(label, app.Size);
        if (exitCode is ERROR_SUCCESS_REBOOT_REQUIRED or ERROR_SUCCESS_REBOOT_INITIATED)
            return RemoveResult.Failed(label, ERROR_SUCCESS_REBOOT_REQUIRED, Loc.F("Cần khởi động lại máy để gỡ xong %@", app.Name));
        if (DateTime.UtcNow - started >= MaxWait)
            return RemoveResult.Failed(label, Win32.ERROR_IO_DEVICE, Loc.T("Hết thời gian chờ trình gỡ cài đặt"));
        return RemoveResult.Failed(label, ERROR_CANCELLED, Loc.F("Trình gỡ đã đóng nhưng %@ vẫn còn trên máy (có thể bạn đã huỷ)", app.Name));
    }

    private static async Task<RemoveResult> RemoveStoreAsync(InstalledApp app, UninstallCommand command, CancellationToken ct)
    {
        var label = app.Name;
        Log.Info(LogCategory.Clean, $"Gỡ gói Store {app.Name}");
        // Gỡ đúng bản đang cài (có thể đã tự cập nhật từ lúc quét).
        var fullName = CurrentStorePackage(app) ?? command.PackageFullName!;
        var (ok, error) = await StoreApps.RemoveAsync(fullName).WaitAsync(ct).ConfigureAwait(false);
        if (ok || IsGone(app)) return RemoveResult.Ok(label, app.Size);
        return RemoveResult.Failed(label, Win32.ERROR_IO_DEVICE, error ?? Loc.T("Không gỡ được gói Microsoft Store"));
    }

    private static HashSet<int> ProcessIDs()
    {
        var ids = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            using (p) ids.Add(p.Id);
        }
        return ids;
    }

    /// <summary>Còn tiến trình mới (xuất hiện sau khi chạy trình gỡ) trông như trình gỡ: trong %TEMP%, thư mục cài, hoặc tên gợi ý.</summary>
    private static bool HasActiveUninstaller(InstalledApp app, string exe, HashSet<int> before)
    {
        var exeName = Path.GetFileName(exe);
        string[] temps = [KnownPaths.Temp, Path.Combine(KnownPaths.Windows, "Temp"), Path.Combine(KnownPaths.Windows, "SystemTemp")];
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (before.Contains(p.Id) || p.Id <= 4) continue;
                var path = Win32.ProcessImagePath((uint)p.Id);
                if (path is null) continue;
                if (temps.Any(t => PathPolicy.IsSameOrInside(path, t))) return true;
                // Tiến trình của Windows (svchost, conhost, TrustedInstaller...) không phải trình gỡ của hãng; msiexec /x tự chờ tới khi xong.
                if (PathPolicy.IsSameOrInside(path, KnownPaths.Windows)) continue;
                var name = Path.GetFileName(path);
                if (name.Equals(exeName, StringComparison.OrdinalIgnoreCase)) return true;
                if (app.InstallLocation is { Length: > 3 } dir && PathPolicy.IsSameOrInside(path, dir)) return true;
                if (Path.GetDirectoryName(exe) is { } exeDir && !PathPolicy.IsSameOrInside(exeDir, KnownPaths.Windows) && PathPolicy.IsSameOrInside(path, exeDir)) return true;
                var lower = name.ToLowerInvariant();
                if (s_uninstallerHints.Any(h => lower.Contains(h, StringComparison.Ordinal))) return true;
            }
        }
        return false;
    }
}

/// <summary>Remover <c>app</c>: chạy trình gỡ của từng app theo thứ tự (CleanEngine gọi, mục 7.3).</summary>
public sealed class AppUninstallRemover : IRemover
{
    public string Id => RemoverIDs.App;

    public async Task<IReadOnlyList<RemoveResult>> RemoveAsync(IReadOnlyList<CleanPlan.Item> items, RemoveContext context)
    {
        var results = new List<RemoveResult>();
        foreach (var item in items)
        {
            if (item.Virtual is not InstalledAppItem appItem)
            {
                results.Add(RemoveResult.Skipped(item.Label, SkipReason.Unsupported));
                continue;
            }
            var app = appItem.App;
            if (context.Cancellation.IsCancellationRequested)
            {
                results.Add(RemoveResult.Skipped(app.Name, SkipReason.UserDeclined));
                continue;
            }
            if (context.DryRun)
            {
                results.Add(RemoveResult.Skipped(app.Name, SkipReason.DryRun, item.ExpectedSize.Bytes));
                continue;
            }
            if (UninstallPolicy.ReasonCannotUninstall(app) is { } reason)
            {
                results.Add(RemoveResult.Failed(app.Name, Win32.ERROR_ACCESS_DENIED, reason));
                continue;
            }
            if (UninstallCommand.For(app, out var problem) is not { } command)
            {
                results.Add(RemoveResult.Failed(app.Name, Win32.ERROR_FILE_NOT_FOUND, problem));
                continue;
            }
            // Báo app đang gỡ (CleanEngine chuyển thành "mục hiện tại" của thanh tiến độ).
            context.Progress(app.Name, 0);
            RemoveResult r;
            try
            {
                r = await AppUninstallRunner.RunAsync(app, command, context.Cancellation).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(LogCategory.Clean, "Gỡ app lỗi", ex);
                r = RemoveResult.Failed(app.Name, Win32.ERROR_IO_DEVICE, ex.Message);
            }
            if (r.Outcome == RemoveOutcomeKind.Ok) context.Progress(app.Name, r.FreedBytes);
            Log.Info(LogCategory.Clean, $"Gỡ {app.Name}: {r.LogValue}");
            results.Add(r);
        }
        return results;
    }
}

/// <summary>
/// Remover <c>registryKey</c>: chỉ khoá trong <c>HKCU\Software</c>. Xuất bản sao <c>.reg</c> bằng <c>reg.exe export</c> vào
/// <c>%LOCALAPPDATA%\CleanBoost\RegistryBackups</c> trước, xuất lỗi thì không xoá.
/// </summary>
public sealed class RegistryKeyRemover : IRemover
{
    public string Id => RemoverIDs.RegistryKey;

    public static string BackupDirectory => Path.Combine(KnownPaths.AppData, "RegistryBackups");

    /// <summary>Khoá con trực tiếp của HKCU\Software không bao giờ xoá nguyên khoá (của Windows hoặc nhiều app dùng chung).</summary>
    private static readonly HashSet<string> s_protectedVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Classes", "Policies", "Wow6432Node", "Clients", "RegisteredApplications", "AppDataLow", "ODBC", "Windows",
        "Intel", "Khronos", "Google", "Mozilla", "Apple Inc.", "Apple Computer, Inc.", "Adobe", "JavaSoft", "Oracle", "CleanBoost",
    };

    /// <summary>Không xoá bất kỳ khoá nào bên dưới các nhánh này.</summary>
    private static readonly HashSet<string> s_neverUnder = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Classes", "Policies", "Wow6432Node", "Windows", "RegisteredApplications", "Clients", "CleanBoost",
    };

    /// <summary>Đường dẫn khoá hợp lệ để xoá: <c>Software\&lt;Hãng&gt;[\...]</c>, không thành phần rỗng, không phải khoá của Windows.</summary>
    public static bool IsAllowed(RegistryKeyItem item, out string reason)
    {
        reason = "";
        if (!item.Hive.Equals("HKCU", StringComparison.OrdinalIgnoreCase))
        {
            reason = Loc.T("Chỉ xoá được khoá registry của người dùng hiện tại (HKCU)");
            return false;
        }
        var parts = item.KeyPath.Split('\\');
        if (parts.Length < 2 || !parts[0].Equals("Software", StringComparison.OrdinalIgnoreCase) || parts.Any(p => p.Length == 0 || p.Trim() is "." or ".."))
        {
            reason = Loc.T("Khoá registry không hợp lệ");
            return false;
        }
        if ((parts.Length == 2 && s_protectedVendors.Contains(parts[1])) || s_neverUnder.Contains(parts[1]))
        {
            reason = Loc.T("Khoá registry được bảo vệ");
            return false;
        }
        return true;
    }

    public async Task<IReadOnlyList<RemoveResult>> RemoveAsync(IReadOnlyList<CleanPlan.Item> items, RemoveContext context)
    {
        var results = new List<RemoveResult>();
        foreach (var item in items)
        {
            if (item.Virtual is not RegistryKeyItem key)
            {
                results.Add(RemoveResult.Skipped(item.Label, SkipReason.Unsupported));
                continue;
            }
            var label = key.DisplayPath;
            if (!IsAllowed(key, out var reason))
            {
                results.Add(RemoveResult.Failed(label, Win32.ERROR_ACCESS_DENIED, reason));
                continue;
            }
            if (context.Cancellation.IsCancellationRequested) break;
            using (var existing = Registry.CurrentUser.OpenSubKey(key.KeyPath))
            {
                if (existing is null)
                {
                    results.Add(RemoveResult.Skipped(label, SkipReason.NotFound));
                    continue;
                }
            }
            if (context.DryRun)
            {
                results.Add(RemoveResult.Skipped(label, SkipReason.DryRun, item.ExpectedSize.Bytes));
                continue;
            }
            results.Add(await RemoveOneAsync(key, item.ExpectedSize.Bytes, context).ConfigureAwait(false));
        }
        return results;
    }

    private static async Task<RemoveResult> RemoveOneAsync(RegistryKeyItem key, long size, RemoveContext context)
    {
        var label = key.DisplayPath;
        string backup;
        try
        {
            Directory.CreateDirectory(BackupDirectory);
            var safeName = string.Concat(key.KeyPath.Split('\\').Last().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            backup = Path.Combine(BackupDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safeName}.reg");
            var output = await ProcessRunner.RunAsync(ProcessRunner.System32("reg.exe"), ["export", "HKCU\\" + key.KeyPath, backup, "/y"],
                TimeSpan.FromSeconds(60), context.Cancellation).ConfigureAwait(false);
            if (!output.Succeeded || !File.Exists(backup))
                return RemoveResult.Failed(label, Win32.ERROR_IO_DEVICE, Loc.T("Không sao lưu được khoá registry nên chưa xoá") + ": " + output.StdErr.Trim());
        }
        catch (OperationCanceledException)
        {
            return RemoveResult.Skipped(label, SkipReason.UserDeclined);
        }
        catch (Exception ex)
        {
            return RemoveResult.Failed(label, Win32.ERROR_IO_DEVICE, Loc.T("Không sao lưu được khoá registry nên chưa xoá") + ": " + ex.Message);
        }
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(key.KeyPath, throwOnMissingSubKey: false);
            Log.Info(LogCategory.Clean, "Đã xoá khoá registry, bản sao lưu: " + Path.GetFileName(backup));
            context.Progress(label, size);
            return RemoveResult.Ok(label, size);
        }
        catch (UnauthorizedAccessException ex)
        {
            return RemoveResult.Failed(label, Win32.ERROR_ACCESS_DENIED, ex.Message);
        }
        catch (Exception ex)
        {
            return RemoveResult.Failed(label, Win32.ERROR_IO_DEVICE, ex.Message);
        }
    }
}

/// <summary>
/// Tiến trình đang chạy từ thư mục cài đặt của app (AppTerminator của bản Mac): chỉ trong phiên đăng nhập hiện tại,
/// bỏ tiến trình hệ thống được bảo vệ và chính Clean Boost.
/// </summary>
public static class AppProcesses
{
    /// <summary>Thư mục dùng để nhận diện tiến trình của app (bỏ qua nếu là thư mục được bảo vệ/quá rộng).</summary>
    public static string? ProcessRoot(InstalledApp app)
    {
        if (app.InstallLocation is not { Length: > 3 } dir) return null;
        if (!app.IsStore && PathPolicy.CurrentUser.IsForbidden(dir) is not null) return null;
        return dir;
    }

    public static List<(int Pid, string Path)> Find(InstalledApp app, Knowledge? knowledge = null)
    {
        if (ProcessRoot(app) is not { } dir) return [];
        var session = Process.GetCurrentProcess().SessionId;
        var self = Environment.ProcessId;
        var protectedNames = knowledge?.ProtectedProcesses ?? [];
        var result = new List<(int, string)>();
        foreach (var (pid, path) in RunningApps.ProcessesUnder(dir))
        {
            if (pid == self) continue;
            var name = Path.GetFileNameWithoutExtension(path);
            if (protectedNames.Any(p => p.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.SessionId != session) continue;
            }
            catch
            {
                continue;
            }
            result.Add((pid, path));
        }
        return result;
    }

    public static bool IsRunning(InstalledApp app, Knowledge? knowledge = null) => Find(app, knowledge).Count > 0;

    /// <summary>Yêu cầu đóng cửa sổ chính (WM_CLOSE), chờ tối đa <paramref name="timeout"/>. Trả về true nếu đã tắt hết.</summary>
    public static async Task<bool> CloseAsync(InstalledApp app, Knowledge? knowledge, TimeSpan timeout, CancellationToken ct = default)
    {
        foreach (var (pid, _) in Find(app, knowledge))
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.CloseMainWindow();
            }
            catch
            {
                // đã thoát
            }
        }
        return await WaitUntilQuitAsync(app, knowledge, timeout, ct).ConfigureAwait(false);
    }

    /// <summary>Buộc thoát (kể cả tiến trình con), chờ tối đa 5 giây.</summary>
    public static async Task<bool> KillAsync(InstalledApp app, Knowledge? knowledge, CancellationToken ct = default)
    {
        foreach (var (pid, _) in Find(app, knowledge))
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                Log.Warning(LogCategory.Clean, "Không buộc thoát được tiến trình: " + ex.Message);
            }
        }
        return await WaitUntilQuitAsync(app, knowledge, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
    }

    public static async Task<bool> WaitUntilQuitAsync(InstalledApp app, Knowledge? knowledge, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Find(app, knowledge).Count == 0) return true;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        return Find(app, knowledge).Count == 0;
    }
}
