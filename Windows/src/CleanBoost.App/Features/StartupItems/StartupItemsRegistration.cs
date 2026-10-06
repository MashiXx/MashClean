using CleanBoost.Features.StartupItems;

namespace CleanBoost.App.Services;

/// <summary>Đăng ký tính năng Mục khởi động: provider cho Smart Scan (mục hỏng) và remover <c>startupItem</c>.</summary>
public sealed partial class FeatureRegistry
{
    partial void RegisterStartupItems()
    {
        Add(new StartupItemsFeature(Env.AppScanner));
        Add(new StartupItemRemover());
    }
}
