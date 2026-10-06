using System.Globalization;
using System.Text;
using CleanBoost.Core;
using CleanBoost.Engine.FileSystem;
using CleanBoost.Engine.Persistence;
using CleanBoost.Engine.Rules;
using CleanBoost.Engine.Scanning;
using Microsoft.Win32;

namespace CleanBoost.Engine.Apps;

/// <summary>
/// Tìm app đã cài (SYSTEM_DESIGN mục 11.3) trên Windows:
/// <list type="number">
/// <item>khoá Uninstall trong HKLM (64 và 32-bit) và HKCU, bỏ thành phần hệ thống và bản cập nhật;</item>
/// <item>app Microsoft Store của người dùng hiện tại (PackageManager), bỏ framework và gói hệ thống;</item>
/// <item>lần dùng gần nhất từ UserAssist (Explorer ghi lại mỗi lần mở chương trình).</item>
/// </list>
/// </summary>
public sealed class AppScanner(FileSystemService fileSystem, Persistence.Storage? storage, Func<Knowledge> knowledge)
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public async Task<List<InstalledApp>> ScanAsync(Action<double>? progress = null, CancellationToken ct = default)
    {
        var know = knowledge();
        var usage = UserAssist.Read();
        progress?.Invoke(0.05);
        var desktop = await Task.Run(() => ReadRegistryApps(know, usage, ct), ct).ConfigureAwait(false);
        progress?.Invoke(0.3);
        var store = await Task.Run(() => StoreApps.Read(know, ct, usage), ct).ConfigureAwait(false);
        progress?.Invoke(0.4);

        // Dung lượng: EstimatedSize trong registry; thiếu thì đo thư mục cài đặt (có cache 7 ngày).
        var cache = TryCachedApps();
        var all = desktop.Concat(store).ToList();
        var measured = new List<InstalledApp>(all.Count);
        var done = 0;
        var updates = new List<AppUsageCache>();
        foreach (var app in all)
        {
            ct.ThrowIfCancellationRequested();
            var a = app;
            if (a.Size <= 0 && a.InstallLocation is { Length: > 3 } dir && Directory.Exists(dir))
            {
                if (cache.TryGetValue(a.AppID, out var c) && c.Size is { } size && DateTimeOffset.Now - c.UpdatedAt < TimeSpan.FromDays(7)
                    && string.Equals(c.Path, dir, StringComparison.OrdinalIgnoreCase))
                {
                    a = a with { Size = size };
                }
                else
                {
                    var m = fileSystem.MeasureConcurrently(dir, ct: ct);
                    a = a with { Size = m.AllocatedSize };
                    updates.Add(new AppUsageCache { AppId = a.AppID, Path = dir, Publisher = a.Publisher, Version = a.Version, Size = m.AllocatedSize, Name = a.Name });
                }
            }
            measured.Add(a);
            if (++done % 10 == 0) progress?.Invoke(0.4 + 0.6 * done / Math.Max(1, all.Count));
        }
        try
        {
            if (updates.Count > 0) storage?.UpsertApps(updates);
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory.Storage, "Không cập nhật được cache app: " + ex.Message);
        }
        progress?.Invoke(1);
        var culture = Loc.Culture;
        return measured.OrderBy(a => a.Name, StringComparer.Create(culture, CompareOptions.IgnoreCase)).ToList();
    }

    private Dictionary<string, AppUsageCache> TryCachedApps()
    {
        try { return storage?.CachedApps() ?? []; } catch { return []; }
    }

    // MARK: Registry

    private static List<InstalledApp> ReadRegistryApps(Knowledge know, UserAssist.UsageIndex usage, CancellationToken ct)
    {
        var result = new List<InstalledApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        (RegistryHive Hive, RegistryView View, RegistryScope Scope)[] sources =
        [
            (RegistryHive.LocalMachine, RegistryView.Registry64, RegistryScope.Machine),
            (RegistryHive.LocalMachine, RegistryView.Registry32, RegistryScope.Machine32),
            (RegistryHive.CurrentUser, RegistryView.Registry64, RegistryScope.User),
        ];
        foreach (var (hive, view, scope) in sources)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(UninstallKey);
                if (uninstall is null) continue;
                foreach (var keyName in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(keyName);
                    if (key is null) continue;
                    if (ReadApp(key, keyName, scope, know, usage) is not { } app) continue;
                    var dedupe = $"{app.Name}|{app.Publisher}|{app.InstallLocation}|{app.Version}";
                    if (!seen.Add(dedupe)) continue;
                    result.Add(app);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(LogCategory.Scan, $"Không đọc được khoá Uninstall {hive}/{view}: {ex.Message}");
            }
        }
        return result;
    }

    internal static InstalledApp? ReadApp(RegistryKey key, string keyName, RegistryScope scope, Knowledge know, UserAssist.UsageIndex usage)
    {
        string? S(string name) => key.GetValue(name) is { } v ? Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim().Trim('\0') : null;
        int I(string name) => key.GetValue(name) is int i ? i : int.TryParse(S(name), out var p) ? p : 0;

        var name = S("DisplayName");
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (I("SystemComponent") == 1) return null;
        if (!string.IsNullOrEmpty(S("ParentKeyName")) || !string.IsNullOrEmpty(S("ParentDisplayName"))) return null;
        var releaseType = S("ReleaseType") ?? "";
        if (releaseType is "Security Update" or "Update Rollup" or "Hotfix" or "Update") return null;
        var uninstall = S("UninstallString");
        var quiet = S("QuietUninstallString");
        if (string.IsNullOrEmpty(uninstall) && string.IsNullOrEmpty(quiet)) return null;

        var publisher = S("Publisher");
        var icon = S("DisplayIcon");
        var location = NormalizeDirectory(S("InstallLocation"))
            ?? DirectoryOfExecutable(icon)
            ?? DirectoryOfExecutable(uninstall, skipSystem: true);
        var exes = new List<string>();
        if (ExecutableFrom(icon) is { } iconExe && !IsSystemBinary(iconExe)) exes.Add(Path.GetFileNameWithoutExtension(iconExe));

        DateTime? installDate = null;
        if (S("InstallDate") is { Length: 8 } d && DateTime.TryParseExact(d, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            installDate = parsed;
        long size = 0;
        if (key.GetValue("EstimatedSize") is int kb && kb > 0) size = kb * 1024L;

        var lastUsed = usage.LastUsed(location, name);

        var hivePrefix = scope == RegistryScope.User ? "HKCU" : "HKLM";
        return new InstalledApp
        {
            Id = $"{hivePrefix}{(scope == RegistryScope.Machine32 ? "32" : "")}:{keyName}",
            AppID = keyName,
            Name = name,
            Version = S("DisplayVersion"),
            Publisher = publisher,
            InstallLocation = location,
            UninstallString = uninstall,
            QuietUninstallString = quiet,
            ModifyPath = S("ModifyPath"),
            DisplayIcon = icon,
            IsWindowsInstaller = I("WindowsInstaller") == 1,
            Size = size,
            InstallDate = installDate,
            LastUsed = lastUsed,
            Source = AppSource.Desktop,
            Scope = scope,
            RegistryKeyPath = (scope == RegistryScope.Machine32 ? @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\" : UninstallKey + "\\") + keyName,
            IsSystemPublisher = know.IsSystemPublisher(publisher),
            IsNonRemovable = I("NoRemove") == 1,
            ExecutableNames = exes,
        };
    }

    private static string? NormalizeDirectory(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        var d = Environment.ExpandEnvironmentVariables(dir.Trim().Trim('"')).TrimEnd('\\');
        if (d.Length < 3 || PathPolicy.NormalizeSyntax(d) is not { } n) return null;
        // Thư mục cài đặt trỏ vào gốc ổ hoặc thư mục hệ thống thì không dùng được để tìm file sót.
        if (n.Length <= 3 || n.Equals(KnownPaths.Windows, StringComparison.OrdinalIgnoreCase) || n.Equals(KnownPaths.ProgramFiles, StringComparison.OrdinalIgnoreCase)
            || n.Equals(KnownPaths.ProgramFilesX86, StringComparison.OrdinalIgnoreCase) || PathPolicy.IsSameOrInside(n, KnownPaths.Windows))
            return null;
        return n;
    }

    /// <summary>Đường dẫn exe trong chuỗi lệnh/biểu tượng: <c>"C:\a b\x.exe",0</c> hoặc <c>C:\a\x.exe /uninstall</c>.</summary>
    public static string? ExecutableFrom(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var c = Environment.ExpandEnvironmentVariables(command.Trim());
        string candidate;
        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            if (end < 0) return null;
            candidate = c[1..end];
        }
        else
        {
            var exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            candidate = exe > 0 ? c[..(exe + 4)] : c.Split(',')[0];
        }
        candidate = candidate.Split(',')[0].Trim();
        return candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Path.IsPathFullyQualified(candidate) ? candidate : null;
    }

    private static bool IsSystemBinary(string exe) =>
        PathPolicy.IsSameOrInside(exe, KnownPaths.Windows) || Path.GetFileName(exe).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase);

    private static string? DirectoryOfExecutable(string? command, bool skipSystem = false)
    {
        if (ExecutableFrom(command) is not { } exe) return null;
        if (IsSystemBinary(exe)) return null;
        var dir = Path.GetDirectoryName(exe);
        // Exe thường nằm trong thư mục con (bin, uninstall, x64…) của thư mục cài đặt: lấy thư mục cha khi đó.
        string[] subfolders = ["uninstall", "_uninstall", "uninst", "bin", "bin64", "x64", "x86", "amd64"];
        if (dir is not null && subfolders.Contains(Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase))
            dir = Path.GetDirectoryName(dir);
        if (dir is not null && (PathPolicy.IsSameOrInside(dir, Path.Combine(KnownPaths.ProgramData, "Package Cache"))
                                || PathPolicy.IsSameOrInside(dir, KnownPaths.Temp)))
            return null;
        return NormalizeDirectory(dir);
    }
}

/// <summary>
/// Đọc UserAssist (<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist</c>): tên giá trị mã hoá ROT13,
/// dữ liệu 72 byte có FILETIME lần chạy cuối ở offset 60. Dùng ước lượng "lần dùng gần nhất" của app.
/// </summary>
public static class UserAssist
{
    /// <summary>
    /// Lần chạy gần nhất theo exe (đường dẫn đầy đủ), theo lối tắt (tên file .lnk không đuôi) và theo app Store
    /// (PackageFamilyName lấy từ AppUserModelID <c>Family!App</c>).
    /// </summary>
    public sealed record UsageIndex(
        IReadOnlyDictionary<string, DateTime> ByExe,
        IReadOnlyDictionary<string, DateTime> ByShortcut,
        IReadOnlyDictionary<string, DateTime> ByPackageFamily)
    {
        public static readonly UsageIndex Empty = new(new Dictionary<string, DateTime>(), new Dictionary<string, DateTime>(), new Dictionary<string, DateTime>());

        /// <summary>Lần dùng gần nhất của app: exe trong thư mục cài, hoặc lối tắt trùng tên app.</summary>
        public DateTime? LastUsed(string? installLocation, string appName)
        {
            DateTime? best = null;
            void Take(DateTime when)
            {
                if (best is null || when > best) best = when;
            }
            if (installLocation is { Length: > 3 })
            {
                foreach (var (exe, when) in ByExe)
                {
                    if (PathPolicy.IsSameOrInside(exe, installLocation)) Take(when);
                }
            }
            foreach (var (shortcut, when) in ByShortcut)
            {
                if (shortcut.Equals(appName, StringComparison.OrdinalIgnoreCase)
                    || (shortcut.Length >= 4 && appName.StartsWith(shortcut + " ", StringComparison.OrdinalIgnoreCase)))
                    Take(when);
            }
            return best;
        }
    }

    public static IReadOnlyDictionary<string, DateTime> LastRunByExe() => Read().ByExe;

    public static UsageIndex Read()
    {
        var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var shortcuts = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var packages = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        static void Keep(Dictionary<string, DateTime> d, string key, DateTime when)
        {
            if (!d.TryGetValue(key, out var existing) || when > existing) d[key] = when;
        }
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist");
            if (root is null) return UsageIndex.Empty;
            foreach (var guid in root.GetSubKeyNames())
            {
                using var count = root.OpenSubKey(guid + @"\Count");
                if (count is null) continue;
                foreach (var valueName in count.GetValueNames())
                {
                    if (count.GetValue(valueName) is not byte[] data || data.Length < 68) continue;
                    var ft = BitConverter.ToInt64(data, 60);
                    if (ft <= 0) continue;
                    DateTime when;
                    try { when = DateTime.FromFileTimeUtc(ft).ToLocalTime(); } catch { continue; }
                    var decoded = Rot13(valueName);
                    if (!decoded.Contains('\\') && decoded.IndexOf('!') is var bang and > 0)
                    {
                        Keep(packages, decoded[..bang], when);
                        continue;
                    }
                    var path = ResolveKnownFolder(decoded);
                    if (path is null) continue;
                    if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) Keep(result, path, when);
                    else if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) Keep(shortcuts, Path.GetFileNameWithoutExtension(path), when);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory.Scan, "Không đọc được UserAssist: " + ex.Message);
        }
        return new UsageIndex(result, shortcuts, packages);
    }

    public static string Rot13(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                >= 'a' and <= 'z' => (char)('a' + (c - 'a' + 13) % 26),
                >= 'A' and <= 'Z' => (char)('A' + (c - 'A' + 13) % 26),
                _ => c,
            });
        }
        return sb.ToString();
    }

    /// <summary>Đổi tiền tố <c>{GUID}\...</c> (KNOWNFOLDERID) thành đường dẫn thật.</summary>
    private static string? ResolveKnownFolder(string value)
    {
        if (value.StartsWith('{') && value.IndexOf('}') is var end and > 0 && Guid.TryParse(value[..(end + 1)], out var id))
        {
            var folder = KnownPaths.KnownFolder(id);
            return folder is null ? null : folder + value[(end + 1)..];
        }
        return Path.IsPathFullyQualified(value) ? value : null;
    }
}

/// <summary>Task <c>installedApps</c>: chạy đầu tiên vì nhiều task cần biết app nào đang được cài (mục 5.5).</summary>
public sealed class InstalledAppsTask(AppScanner scanner) : IScanTask
{
    public const string TaskID = "installedApps";
    public const string ArtifactKey = "installedApps";

    public string Id => TaskID;
    public ScanPriority Priority => ScanPriority.High;
    public double EstimatedWeight => 2;
    public string Title => Loc.T("Tìm ứng dụng đã cài");

    public async Task<ScanOutput> RunAsync(ScanContext context)
    {
        var apps = await scanner.ScanAsync(p => context.Progress.Report(p), context.Cancellation).ConfigureAwait(false);
        return new ScanOutput { Artifacts = { [ArtifactKey] = apps } };
    }
}

public static class InstalledAppsContext
{
    /// <summary>Danh sách app từ task <c>installedApps</c> (rỗng nếu task không phải phụ thuộc).</summary>
    public static IReadOnlyList<InstalledApp> InstalledApps(this ScanContext context) =>
        context.UpstreamArtifact<List<InstalledApp>>(InstalledAppsTask.ArtifactKey) ?? [];
}
