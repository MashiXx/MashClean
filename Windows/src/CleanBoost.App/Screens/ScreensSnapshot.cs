using System.Windows;
using CleanBoost.App.Services;
using CleanBoost.App.Shell;
using CleanBoost.Core;
using CleanBoost.Engine.Ipc;
using CleanBoost.Engine.Persistence;

namespace CleanBoost.App.Screens;

/// <summary>
/// Chụp ảnh kiểm thử giao diện cho các màn trong thư mục này (chỉ khi <c>CLEANBOOST_SNAPSHOT</c> được đặt):
/// <list type="bullet">
/// <item><c>CLEANBOOST_SNAPSHOT_ONBOARDING=1</c>: từng bước onboarding (không lưu gì vào cài đặt).</item>
/// <item><c>CLEANBOOST_SNAPSHOT_SETTINGS=1</c>: từng tab Cài đặt.</item>
/// <item><c>CLEANBOOST_HISTORY_DB=&lt;file&gt;</c>: màn Lịch sử đọc database riêng thay vì database thật;
/// bản Debug thêm <c>CLEANBOOST_HISTORY_DEMO=1</c> để tạo lại database đó với dữ liệu mẫu.</item>
/// </list>
/// Ảnh lưu vào thư mục <c>CLEANBOOST_SNAPSHOT</c>; app tự thoát khi <see cref="DebugSnapshot"/> chụp xong các màn chính.
/// </summary>
internal static class ScreensSnapshot
{
    private static bool Flag(string name) => Environment.GetEnvironmentVariable(name) == "1";

    private static readonly Lazy<Storage?> s_demoStorage = new(OpenDemoStorage);

    /// <summary>Database thay thế cho màn Lịch sử và danh sách bỏ qua khi chụp ảnh (không đụng tới database thật).</summary>
    public static Storage? DemoStorage() => DebugSnapshot.IsEnabled ? s_demoStorage.Value : null;

    private static Storage? OpenDemoStorage()
    {
        if (Environment.GetEnvironmentVariable("CLEANBOOST_HISTORY_DB") is not { Length: > 0 } path) return null;
        try
        {
#if DEBUG
            if (Flag("CLEANBOOST_HISTORY_DEMO"))
            {
                foreach (var f in new[] { path, path + "-wal", path + "-shm" }) File.Delete(f);
                var demo = new Storage(path);
                SeedDemoHistory(demo);
                return demo;
            }
#endif
            return new Storage(path);
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.UI, "Không mở được database chụp ảnh", ex);
            return null;
        }
    }

    public static void Attach(MainWindow window, AppEnvironment env)
    {
        var onboarding = Flag("CLEANBOOST_SNAPSHOT_ONBOARDING");
        var settings = Flag("CLEANBOOST_SNAPSHOT_SETTINGS");
        if (!onboarding && !settings) return;
        var dir = Environment.GetEnvironmentVariable("CLEANBOOST_SNAPSHOT")!;
        Directory.CreateDirectory(dir);
        var started = false;
        window.ContentRendered += async (_, _) =>
        {
            if (started) return;
            started = true;
            try
            {
                if (onboarding) await SnapOnboardingAsync(window, env, dir);
                if (settings) await SnapSettingsAsync(window, env, dir);
                Log.Info(LogCategory.UI, "Đã chụp ảnh onboarding/cài đặt vào " + dir);
            }
            catch (Exception ex)
            {
                Log.Error(LogCategory.UI, "Chụp ảnh onboarding/cài đặt lỗi", ex);
            }
        };
    }

    private static async Task SnapOnboardingAsync(Window owner, AppEnvironment env, string dir)
    {
        var w = new OnboardingWindow(env) { Owner = owner };
        w.Show();
        await Task.Delay(500);
        DebugSnapshot.Save(w, Path.Combine(dir, "Onboarding-1-Welcome.png"));
        w.GoTo(OnboardingWindow.Step.Admin, animate: false);
        await Task.Delay(300);
        DebugSnapshot.Save(w, Path.Combine(dir, "Onboarding-2-Admin.png"));
        w.PreviewHelperStatus(HelperStatus.Declined);
        await Task.Delay(200);
        DebugSnapshot.Save(w, Path.Combine(dir, "Onboarding-2b-Admin-Declined.png"));
        w.PreviewHelperStatus(HelperStatus.Running);
        await Task.Delay(200);
        DebugSnapshot.Save(w, Path.Combine(dir, "Onboarding-2c-Admin-Running.png"));
        w.PreviewHelperStatus(null);
        w.GoTo(OnboardingWindow.Step.Done, animate: false);
        await Task.Delay(300);
        DebugSnapshot.Save(w, Path.Combine(dir, "Onboarding-3-Done.png"));
        w.Close();
    }

    private static async Task SnapSettingsAsync(Window owner, AppEnvironment env, string dir)
    {
        var w = SettingsWindow.ShowFor(owner, env);
        await Task.Delay(500);
        var i = 1;
        foreach (var tab in Enum.GetValues<SettingsWindow.Tab>())
        {
            w.Select(tab);
            await Task.Delay(450);
            DebugSnapshot.Save(w, Path.Combine(dir, $"Settings-{i}-{tab}.png"));
            if (tab == SettingsWindow.Tab.General && FindScroll(w) is { } scroll)
            {
                scroll.ScrollToEnd();
                await Task.Delay(250);
                DebugSnapshot.Save(w, Path.Combine(dir, $"Settings-{i}b-{tab}-End.png"));
                scroll.ScrollToHome();
            }
            i++;
        }
        w.Close();
    }

    private static System.Windows.Controls.ScrollViewer? FindScroll(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.ScrollViewer { IsVisible: true } sv) return sv;
            if (FindScroll(child) is { } found) return found;
        }
        return null;
    }

#if DEBUG
    /// <summary>Dữ liệu mẫu cho ảnh chụp màn Lịch sử (đường dẫn giả, chỉ ghi vào database chụp ảnh).</summary>
    private static void SeedDemoHistory(Storage storage)
    {
        var home = KnownPaths.UserHome;
        var temp = KnownPaths.Temp;
        var rnd = new Random(7);
        var now = DateTimeOffset.Now;
        int[] daysAgo = [19, 17, 17, 14, 11, 9, 8, 6, 4, 2, 1, 0];
        long[] freedMB = [820, 140, 96, 1450, 310, 2210, 75, 540, 1180, 260, 690, 1930];
        for (var n = 0; n < daysAgo.Length; n++)
        {
            var start = now.Date.AddDays(-daysAgo[n]).AddHours(9 + rnd.Next(0, 11)).AddMinutes(rnd.Next(0, 59));
            var startedAt = new DateTimeOffset(start, now.Offset);
            var last = n == daysAgo.Length - 1;
            var items = new List<CleanItemLog>();
            var opId = Guid.NewGuid().ToString();
            void Add(string path, string strategy, long size, string result, string? trashed = null) =>
                items.Add(new CleanItemLog { OperationId = opId, Path = path, Strategy = strategy, Size = size, Result = result, TrashedPath = trashed, RuleId = "demo" });

            var remaining = freedMB[n] * 1_000_000;
            Add(Path.Combine(home, @"AppData\Local\Google\Chrome\User Data\Default\Cache\Cache_Data"), "deleteContents", remaining * 38 / 100, "ok");
            Add(Path.Combine(temp, "chrome_installer_" + n), "delete", remaining * 9 / 100, "ok");
            Add(@"C:\Windows\SoftwareDistribution\Download\4f1c2a9be0d3b7a6c55e1d2f9a8b7c6d", "delete", remaining * 22 / 100, "ok");
            Add(Path.Combine(home, @"AppData\Local\npm-cache\_cacache"), "trash", remaining * 14 / 100, "ok",
                @"C:\$Recycle.Bin\S-1-5-21-0000000000-0000000000-0000000000-1001\$RDEMO" + n);
            Add(Path.Combine(home, @"Downloads\VSCodeUserSetup-x64-1.88.1.exe"), "trash", remaining * 10 / 100, n % 3 == 0 ? "restored" : "ok",
                n % 3 == 0 ? null : @"C:\$Recycle.Bin\S-1-5-21-0000000000-0000000000-0000000000-1001\$RVSC" + n + ".exe");
            Add(Path.Combine(home, @"AppData\Local\CrashDumps\explorer.exe.10424.dmp"), "delete", remaining * 4 / 100, "ok");
            Add(@"C:\ProgramData\Microsoft\Windows\WER\ReportArchive\AppCrash_Teams.exe_3f8e1c", "custom:recycleBin", remaining * 3 / 100, "ok");
            if (n % 2 == 0) Add(Path.Combine(home, @"AppData\Local\Microsoft\Teams\Cache\data_3"), "delete", 18_400_000, "failed:32");
            if (n % 4 == 1) Add(Path.Combine(home, @"AppData\Roaming\Code\logs\20241002T091522"), "delete", 2_100_000, "skipped:inUse");
            if (last)
            {
                for (var k = 0; k < 14; k++)
                    Add(Path.Combine(temp, $"tmp{rnd.Next(1000, 9999)}.tmp"), "delete", rnd.Next(10_000, 9_000_000), "ok");
            }
            var op = new CleanOperation
            {
                Id = opId,
                StartedAt = startedAt,
                FinishedAt = startedAt.AddSeconds(rnd.Next(4, 60)),
                PlannedBytes = remaining,
                FreedBytes = remaining,
                ItemsOk = items.Count(i => i.Result is "ok" or "restored"),
                ItemsFailed = items.Count(i => i.Result.StartsWith("failed", StringComparison.Ordinal)),
            };
            storage.Insert(op);
            storage.LogItems(items);
        }
        storage.AddIgnore(IgnoreEntry.KindPath, Path.Combine(home, @"AppData\Local\JetBrains\Rider2024.2\caches"));
        storage.AddIgnore(IgnoreEntry.KindPath, Path.Combine(home, @"Downloads\Installers"));
        storage.AddIgnore(IgnoreEntry.KindRule, "win.browser.edge.cache");
        storage.AddIgnore(IgnoreEntry.KindApp, "Microsoft.WindowsTerminal_8wekyb3d8bbwe");
        storage.AddIgnore(IgnoreEntry.KindPath, @"D:\Projects\very-long-folder-name-for-a-client\build-artifacts\nightly\2024-09-30\symbols");
    }
#endif
}
