using CleanBoost.Features.Duplicates;

namespace CleanBoost.App.Services;

/// <summary>Đăng ký tính năng File trùng lặp (file đi qua remover file chung, chuyển vào Thùng rác).</summary>
public sealed partial class FeatureRegistry
{
    partial void RegisterDuplicates() => Add(new DuplicatesFeature());
}
