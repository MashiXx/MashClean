using CleanBoost.Features.SpaceLens;

namespace CleanBoost.App.Services;

/// <summary>Đăng ký Space Lens: provider (không chạy trong Smart Scan), xoá dùng remover file chung của Clean Engine.</summary>
public sealed partial class FeatureRegistry
{
    partial void RegisterSpaceLens() => Add(new SpaceLensFeature(Env.FileSystem));
}
