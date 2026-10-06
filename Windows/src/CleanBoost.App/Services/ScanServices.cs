using CleanBoost.Core;
using CleanBoost.Engine.Apps;
using CleanBoost.Engine.Cleaning;
using CleanBoost.Engine.FileSystem;
using CleanBoost.Engine.Ipc;
using CleanBoost.Engine.Nodes;
using CleanBoost.Engine.Persistence;
using CleanBoost.Engine.Rules;
using CleanBoost.Engine.Scanning;

namespace CleanBoost.App.Services;

/// <summary>Gói dịch vụ dùng chung cho các màn quét/dọn (ScanServices của bản Mac).</summary>
public sealed class ScanServices(AppEnvironment env)
{
    public FileSystemService FileSystem => env.FileSystem;
    public ScanEngine ScanEngine => env.ScanEngine;
    public CleanEngine CleanEngine => env.CleanEngine;
    public RuleStore RuleStore => env.Rules;
    public RuleSnapshot Rules => env.Rules.Snapshot;
    public Storage? Storage => env.Storage;
    public AppSettings Settings => env.Settings;
    public AppScanner AppScanner => env.AppScanner;
    public HelperClient Helper => env.Helper;

    /// <summary>Môi trường cho một phiên quét: chính sách người dùng, danh sách bỏ qua, tiến trình đang chạy.</summary>
    public ScanEnvironment MakeEnvironment()
    {
        var running = RunningApps.Capture();
        IgnoreList ignore;
        try { ignore = Storage?.IgnoreList() ?? IgnoreList.Empty; } catch { ignore = IgnoreList.Empty; }
        return new ScanEnvironment
        {
            Policy = PathPolicy.CurrentUser,
            Ignore = ignore,
            RunningProcesses = running.Names,
            RunningProcessPaths = running.Paths,
            IsElevated = HelperClient.IsProcessElevated,
            ShowRiskyItems = Settings.ShowRiskyItems,
            NowUtc = DateTime.UtcNow,
        };
    }

    /// <summary>"Không bao giờ đề xuất mục này" theo đường dẫn.</summary>
    public void Ignore(Node node)
    {
        if (node.Url is not { } path) return;
        try { Storage?.AddIgnore(IgnoreEntry.KindPath, path); } catch (Exception ex) { Log.Error(LogCategory.Storage, "Không lưu được mục bỏ qua", ex); }
    }

    public void IgnoreRule(string ruleID)
    {
        try { Storage?.AddIgnore(IgnoreEntry.KindRule, ruleID); } catch (Exception ex) { Log.Error(LogCategory.Storage, "Không lưu được rule bỏ qua", ex); }
    }
}
