using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using CleanBoost.App.Services;
using CleanBoost.App.Shell;
using CleanBoost.Core;
using CleanBoost.Engine.Persistence;

namespace CleanBoost.App;

/// <summary>
/// Điểm vào của app (MashCleanApp của bản Mac): một phiên bản duy nhất (lần mở sau chuyển tham số sang phiên đang chạy),
/// nạp bản dịch, dựng AppEnvironment, mở cửa sổ chính hoặc chỉ chạy ở khay hệ thống (<c>--background</c>).
/// </summary>
public partial class App : Application
{
    private static Mutex? s_mutex;
    private CancellationTokenSource? _activationCts;

    public static AppEnvironment? Env { get; private set; }
    public static AppRouter Router { get; } = new();
    public static MainWindow? MainView { get; private set; }

    // Các thành phần khởi động thêm, mỗi phần cài partial của mình trong thư mục riêng:
    // khay hệ thống (Tray\), tích hợp Explorer/khởi động cùng Windows (Shell\ShellIntegration), onboarding và cài đặt (Screens\).
    partial void StartTray(AppEnvironment env, bool background);
    partial void StopTray();
    partial void StartShellIntegration(AppEnvironment env);
    partial void StartOnboarding(AppEnvironment env, bool background);
    partial void AttachScreens(MainWindow window);

    /// <summary>Có giữ app chạy khi đóng cửa sổ (biểu tượng khay đang bật) không.</summary>
    public static bool KeepRunningInTray { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Trình gỡ cài đặt: gỡ đăng ký theo người dùng (Explorer, cleanboost://, Run, Jump List) rồi thoát, không hiện gì.
        if (e.Args.Contains(ShellIntegration.UnregisterArgument))
        {
            ShellIntegration.UnregisterAll();
            Shutdown(0);
            return;
        }
        Log.Configure("CleanBoost");
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error(LogCategory.UI, "Lỗi không xử lý: " + ex.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error(LogCategory.UI, "Task lỗi không quan sát", ex.Exception);
            ex.SetObserved();
        };

        // Một phiên bản: lần mở thứ hai gửi tham số cho phiên đang chạy rồi thoát (chế độ chụp ảnh kiểm thử thì bỏ qua).
        var first = true;
        if (!DebugSnapshot.IsEnabled)
        {
            s_mutex = new Mutex(false, Identifiers.SingleInstanceMutex);
            // Mở lại (đổi ngôn ngữ, chạy với quyền quản trị): chờ phiên cũ thoát hẳn.
            var wait = e.Args.Contains("--relaunched") ? TimeSpan.FromSeconds(10) : TimeSpan.Zero;
            try { first = s_mutex.WaitOne(wait); } catch (AbandonedMutexException) { first = true; }
        }
        if (!first)
        {
            ForwardToRunningInstance(e.Args);
            Shutdown();
            return;
        }

        LoadTranslations();
        // CLEANBOOST_LANGUAGE=vi|en ghi đè cài đặt (kiểm thử, chụp ảnh giao diện).
        var languageOverride = Environment.GetEnvironmentVariable("CLEANBOOST_LANGUAGE");
        Loc.Apply(string.IsNullOrEmpty(languageOverride) ? AppSettings.Shared.Language : AppLanguageExtensions.ParseLanguage(languageOverride));
        Log.Info(LogCategory.UI, $"Clean Boost {AppVersion.Display} khởi động, ngôn ngữ {Loc.Active}, Windows {OSVersion.Current}");

        try
        {
            Env = new AppEnvironment();
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.UI, "Không khởi động được", ex);
            Shared.MessageDialog.Show(null, Loc.T("Clean Boost không khởi động được"),
                ex.Message + "\n\n" + Loc.T("Thường do bộ rule đi kèm bị hỏng. Hãy cài lại ứng dụng."), Loc.T("Thoát"));
            Shutdown(1);
            return;
        }

        if (!DebugSnapshot.IsEnabled) StartActivationServer();
        var background = e.Args.Contains("--background");
        StartShellIntegration(Env);
        StartTray(Env, background);
        StartOnboarding(Env, background);
        if (!background) ShowMainWindow();
        Router.Handle(e.Args.Where(a => a != "--background").ToArray());
        _ = Task.Run(StartBackgroundWorkAsync);
    }

    public static void ShowMainWindow()
    {
        if (Env is null) return;
        if (MainView is null)
        {
            MainView = new MainWindow(Env, Router) { ShouldHideOnClose = () => KeepRunningInTray };
            Router.ActivateRequested += () => MainView?.Dispatcher.InvokeAsync(() => MainView?.BringToFront());
            MainView.Closed += (_, _) =>
            {
                MainView = null;
                if (!KeepRunningInTray) Current.Shutdown();
            };
            ((App)Current).AttachScreens(MainView);
            if (DebugSnapshot.IsEnabled) MainView.Loaded += (_, _) => DebugSnapshot.Run(MainView, Router);
        }
        MainView.BringToFront();
    }

    /// <summary>Việc nền lúc khởi động: dọn dữ liệu cũ, kiểm tra rule mới.</summary>
    private static async Task StartBackgroundWorkAsync()
    {
        if (Env is null || Environment.GetEnvironmentVariable("MASHCLEAN_SMOKE_TEST") == "1") return;
        try
        {
            Env.Storage?.PruneOldData();
            var outcome = await Env.RuleUpdater.CheckIfNeededAsync().ConfigureAwait(false);
            Log.Info(LogCategory.Rules, outcome.Description);
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.UI, "Việc nền lỗi", ex);
        }
    }

    private static void LoadTranslations()
    {
        var asm = Assembly.GetExecutingAssembly();
        // en.json (dịch từ bản Mac) trước, các file en.*.json của bản Windows ghi đè sau.
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("CleanBoost.Localization.en", StringComparison.Ordinal))
                     .OrderBy(n => n == "CleanBoost.Localization.en.json" ? 0 : 1).ThenBy(n => n, StringComparer.Ordinal))
        {
            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) continue;
            try { Loc.LoadEnglish(stream); } catch (Exception ex) { Log.Error(LogCategory.UI, "Bản dịch lỗi: " + name, ex); }
        }
    }

    // MARK: Một phiên bản

    private void StartActivationServer()
    {
        _activationCts = new CancellationTokenSource();
        var ct = _activationCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(Identifiers.ActivationPipe, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    using var reader = new StreamReader(server);
                    var text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                    var args = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!args.Contains("--background")) ShowMainWindow();
                        Router.Handle(args);
                    });
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Warning(LogCategory.UI, "Kênh kích hoạt lỗi: " + ex.Message);
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
            }
        }, ct);
    }

    private static void ForwardToRunningInstance(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Identifiers.ActivationPipe, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            using var writer = new StreamWriter(client);
            writer.Write(string.Join('\n', args.Length == 0 ? ["--activate"] : args));
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory.UI, "Không gửi được tới phiên đang chạy: " + ex.Message);
        }
    }

    /// <summary>Khởi động lại app (đổi ngôn ngữ cần khởi động lại, như bản Mac). Phiên mới chờ phiên này thoát.</summary>
    public static void Restart()
    {
        try
        {
            var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, Identifiers.AppExeName);
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, Arguments = "--relaunched" })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.UI, "Không khởi động lại được", ex);
        }
        ReleaseInstanceAndShutdown();
    }

    /// <summary>
    /// Mở lại app với quyền quản trị (để đọc thư mục hệ thống như <c>%WINDIR%\Temp</c>) rồi tiếp tục ở màn đang dùng.
    /// Người dùng từ chối UAC thì app hiện tại chạy tiếp như cũ.
    /// </summary>
    public static void RestartElevated(string? url = null)
    {
        try
        {
            var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, Identifiers.AppExeName);
            var args = "--relaunched" + (url is null ? "" : $" \"{url}\"");
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", Arguments = args })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return; // từ chối UAC
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.UI, "Không chạy được với quyền quản trị", ex);
            return;
        }
        ReleaseInstanceAndShutdown();
    }

    private static void ReleaseInstanceAndShutdown()
    {
        try { s_mutex?.ReleaseMutex(); } catch (ApplicationException) { }
        s_mutex?.Dispose();
        s_mutex = null;
        Current.Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        StopTray();
        _activationCts?.Cancel();
        Env?.Helper.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(LogCategory.UI, "Lỗi giao diện không xử lý", e.Exception);
        e.Handled = true;
        Shared.MessageDialog.Show(MainView, Loc.T("Đã có lỗi"), e.Exception.Message, "OK");
    }
}
