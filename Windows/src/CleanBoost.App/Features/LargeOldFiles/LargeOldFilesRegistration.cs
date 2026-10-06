using CleanBoost.Features.LargeOldFiles;

namespace CleanBoost.App.Services;

/// <summary>Đăng ký tính năng File lớn &amp; cũ (không có remover riêng: file đi qua remover file chung, chuyển vào Thùng rác).</summary>
public sealed partial class FeatureRegistry
{
    partial void RegisterLargeOldFiles() => Add(new LargeOldFilesFeature(Env.Settings));
}
