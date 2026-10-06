using CleanBoost.Core;
using CleanBoost.Engine.Rules;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace CleanBoost.Engine.Apps;

/// <summary>App Microsoft Store / MSIX của người dùng hiện tại (Windows.Management.Deployment.PackageManager).</summary>
public static class StoreApps
{
    public static List<InstalledApp> Read(Knowledge know, CancellationToken ct, UserAssist.UsageIndex? usage = null)
    {
        var result = new List<InstalledApp>();
        try
        {
            var manager = new PackageManager();
            foreach (var p in manager.FindPackagesForUser(string.Empty))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (p.IsFramework || p.IsResourcePackage || p.IsBundle) continue;
                    if (p.SignatureKind == PackageSignatureKind.System) continue;
                    var name = SafeDisplayName(p);
                    if (string.IsNullOrWhiteSpace(name) || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) continue;
                    string? location = null;
                    try { location = p.InstalledLocation?.Path; } catch { }
                    var publisher = SafePublisher(p);
                    var version = $"{p.Id.Version.Major}.{p.Id.Version.Minor}.{p.Id.Version.Build}.{p.Id.Version.Revision}";
                    DateTime? installed = null;
                    try { installed = p.InstalledDate.LocalDateTime; } catch { }
                    result.Add(new InstalledApp
                    {
                        Id = "STORE:" + p.Id.FullName,
                        AppID = p.Id.FamilyName,
                        Name = name,
                        Version = version,
                        Publisher = publisher,
                        InstallLocation = location,
                        Source = AppSource.Store,
                        Scope = RegistryScope.None,
                        PackageFullName = p.Id.FullName,
                        PackageFamilyName = p.Id.FamilyName,
                        InstallDate = installed,
                        LastUsed = usage?.ByPackageFamily.TryGetValue(p.Id.FamilyName, out var used) == true ? used : null,
                        IsSystemPublisher = know.IsSystemPublisher(publisher) && p.SignatureKind != PackageSignatureKind.Store,
                        IsNonRemovable = p.IsDevelopmentMode ? false : p.SignatureKind == PackageSignatureKind.System,
                    });
                }
                catch (Exception ex)
                {
                    Log.Warning(LogCategory.Scan, "Bỏ qua gói Store không đọc được: " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory.Scan, "Không liệt kê được app Store: " + ex.Message);
        }
        return result;
    }

    private static string SafeDisplayName(Package p)
    {
        try { return p.DisplayName; } catch { return p.Id.Name; }
    }

    private static string? SafePublisher(Package p)
    {
        try { return p.PublisherDisplayName; } catch { return null; }
    }

    /// <summary>Gỡ một gói Store của người dùng hiện tại.</summary>
    public static async Task<(bool Ok, string? Error)> RemoveAsync(string packageFullName)
    {
        try
        {
            var manager = new PackageManager();
            var result = await manager.RemovePackageAsync(packageFullName).AsTask().ConfigureAwait(false);
            return result.ExtendedErrorCode is null ? (true, null) : (false, result.ErrorText);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
