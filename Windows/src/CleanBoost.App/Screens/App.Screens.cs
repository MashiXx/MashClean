using System.Windows;
using System.Windows.Threading;
using CleanBoost.App.Screens;
using CleanBoost.App.Services;
using CleanBoost.App.Shell;
using CleanBoost.Core;

namespace CleanBoost.App;

/// <summary>Gắn các màn ứng dụng (Onboarding, Cài đặt) vào vòng đời app.</summary>
public partial class App
{
    /// <summary>Onboarding chưa hoàn tất: hiện khi cửa sổ chính mở (ngay lúc khởi động, hoặc lần đầu mở từ khay nếu chạy nền).</summary>
    private static bool s_offerOnboarding;
    private static bool s_onboardingOpen;

    partial void StartOnboarding(AppEnvironment env, bool background)
    {
        s_offerOnboarding = OnboardingWindow.ShouldShow(env.Settings);
        // Chế độ chụp ảnh: ScreensSnapshot tự mở onboarding không chặn để chụp từng bước.
        if (DebugSnapshot.IsEnabled) s_offerOnboarding = false;
        if (s_offerOnboarding && background) Log.Info(LogCategory.UI, "Onboarding chưa hoàn tất: sẽ hiện khi mở cửa sổ chính");
    }

    partial void AttachScreens(MainWindow window)
    {
        Shell.MainWindow.OpenSettings = owner =>
        {
            if (Env is { } env) SettingsWindow.ShowFor(owner, env);
        };

        if (DebugSnapshot.IsEnabled)
        {
            if (Env is { } snapEnv) ScreensSnapshot.Attach(window, snapEnv);
            return;
        }

        if (!s_offerOnboarding) return;
        var shown = false;
        window.ContentRendered += (_, _) =>
        {
            if (shown) return;
            shown = true;
            // Để cửa sổ chính vẽ xong rồi mới mở hộp thoại onboarding phía trên.
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                if (s_onboardingOpen || Env is not { } env || !OnboardingWindow.ShouldShow(env.Settings)) return;
                s_onboardingOpen = true;
                try
                {
                    OnboardingWindow.ShowModal(window, env);
                }
                finally
                {
                    s_onboardingOpen = false;
                    // Đóng giữa chừng: không hỏi lại trong phiên này, lần mở app sau hiện lại.
                    s_offerOnboarding = false;
                }
            });
        };
    }
}
