using CleanBoost.App.Services;
using CleanBoost.App.Shell;

namespace CleanBoost.App;

public partial class App
{
    /// <summary>
    /// Đăng ký URL <c>cleanboost://</c>, menu chuột phải của File Explorer, khởi động cùng Windows và Jump List
    /// (tương đương FinderSync, AppIntents và login item của bản Mac); cập nhật khi cài đặt đổi.
    /// </summary>
    partial void StartShellIntegration(AppEnvironment env) => ShellIntegration.Start(env.Settings);
}
