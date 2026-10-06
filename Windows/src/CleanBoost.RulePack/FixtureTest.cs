using System.Text.Json;
using CleanBoost.Core;
using CleanBoost.Engine.FileSystem;
using CleanBoost.Engine.Rules;

namespace CleanBoost.RulePack;

/// <summary>
/// Fixture cho <c>rulepack test</c> (mục 18, mức "Rule"): mô tả cây thư mục giả lập và danh sách đường dẫn kỳ vọng.
/// <code>
/// {
///   "name": "npm cache",
///   "rules": ["devtools.npm.cache"],
///   "apps": [{ "appID": "Editor", "appName": "Editor", "publisher": "Example", "installLocation": "%PROGRAMFILES%\\Editor" }],
///   "running": ["editor"],
///   "files": [
///     { "path": "%LOCALAPPDATA%\\npm-cache\\_cacache\\index-v5\\aa", "size": 4096, "ageDays": 3 },
///     { "path": "%TEMP%\\empty", "dir": true }
///   ],
///   "expect": ["%LOCALAPPDATA%\\npm-cache\\_cacache"]
/// }
/// </code>
/// Token thư mục được đặt dưới gốc giả (<see cref="PathResolver.Fixture"/>). Một file có thể là một case, một mảng
/// case, hoặc <c>{ "cases": [...] }</c>.
/// </summary>
public static class FixtureTest
{
    public sealed record FileSpec
    {
        public required string Path { get; init; }
        public int? Size { get; init; }
        public double? AgeDays { get; init; }
        public bool? Dir { get; init; }
    }

    public sealed record AppSpec
    {
        public required string AppID { get; init; }
        public string? AppName { get; init; }
        public string? Publisher { get; init; }
        public string? InstallLocation { get; init; }
    }

    public sealed record Case
    {
        public required string Name { get; init; }
        public required List<string> Rules { get; init; }
        public List<AppSpec>? Apps { get; init; }
        public List<string>? Running { get; init; }
        public required List<FileSpec> Files { get; init; }
        public required List<string> Expect { get; init; }
    }

    private sealed record Wrapper(List<Case> Cases);

    public static List<Case> Load(string file)
    {
        var text = File.ReadAllText(file).TrimStart();
        try
        {
            if (text.StartsWith('[')) return JsonSerializer.Deserialize<List<Case>>(text, RuleJson.StrictOptions) ?? [];
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("cases", out _)) return JsonSerializer.Deserialize<Wrapper>(text, RuleJson.StrictOptions)?.Cases ?? [];
            return [JsonSerializer.Deserialize<Case>(text, RuleJson.StrictOptions)!];
        }
        catch (JsonException ex)
        {
            throw new CliException($"{System.IO.Path.GetFileName(file)}: fixture sai định dạng: {ex.Message}");
        }
    }

    public static (int Passed, int Failed) Run(RulePayload payload, string fixturesDir)
    {
        int passed = 0, failed = 0;
        var tested = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(fixturesDir, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            foreach (var c in Load(file))
            {
                var (ok, message) = RunCase(payload, c);
                foreach (var r in c.Rules) tested.Add(r);
                if (ok)
                {
                    passed++;
                    Console.WriteLine($"  ✓ {c.Name}");
                }
                else
                {
                    failed++;
                    Console.Error.WriteLine($"  ✗ {System.IO.Path.GetFileName(file)}: {c.Name}\n{message}");
                }
            }
        }
        var untested = payload.Rules.Where(r => r.Match.Provider is null && !tested.Contains(r.Id)).Select(r => r.Id).ToList();
        if (untested.Count > 0) Console.WriteLine($"  (chưa có fixture: {string.Join(", ", untested)})");
        return (passed, failed);
    }

    public static (bool Ok, string Message) RunCase(RulePayload payload, Case c)
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cb-rulepack-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        try
        {
            var resolver = PathResolver.Fixture(root);
            var now = DateTime.UtcNow;
            foreach (var f in c.Files)
            {
                var path = resolver.Resolve(f.Path);
                if (f.Dir == true)
                {
                    Directory.CreateDirectory(path);
                    continue;
                }
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, new byte[f.Size ?? 16]);
                if (f.AgeDays is { } age)
                {
                    var when = now.AddDays(-age);
                    File.SetLastWriteTimeUtc(path, when);
                    File.SetLastAccessTimeUtc(path, when);
                }
            }
            // Thư mục cha của file cũ cũng phải cũ, nếu không mtime của thư mục làm lệch minAgeDays.
            foreach (var f in c.Files.Where(f => f.AgeDays is not null && f.Dir != true))
            {
                var dir = System.IO.Path.GetDirectoryName(resolver.Resolve(f.Path))!;
                var when = now.AddDays(-f.AgeDays!.Value);
                while (dir.Length > root.Length)
                {
                    Directory.SetLastWriteTimeUtc(dir, when);
                    Directory.SetLastAccessTimeUtc(dir, when);
                    dir = System.IO.Path.GetDirectoryName(dir)!;
                }
            }

            var rules = payload.Rules.Where(r => c.Rules.Contains(r.Id)).ToList();
            var missing = c.Rules.Except(rules.Select(r => r.Id)).ToList();
            if (missing.Count > 0) return (false, "    không có rule: " + string.Join(", ", missing));
            var snapshot = new RuleSnapshot(payload.Version, rules, payload.Knowledge, resolver: resolver, source: RuleSnapshot.SourceKind.Development);
            var apps = (c.Apps ?? []).Select(a => new RuleAppInfo(a.AppID, a.AppName ?? a.AppID, a.Publisher,
                a.InstallLocation is null ? null : resolver.Resolve(a.InstallLocation), [])).ToList();
            var running = (c.Running ?? []).Select(r => r.ToLowerInvariant()).ToHashSet();
            var runningApps = apps.Where(a => running.Contains(a.AppID.ToLowerInvariant())).Select(a => a.AppID).ToHashSet();
            var context = new RuleEvaluationContext
            {
                FileSystem = new FileSystemService(resolver.Home),
                Policy = PathPolicy.ForCurrentUser(resolver.Home),
                Apps = apps,
                RunningProcesses = running,
                RunningAppIDs = runningApps,
            };
            var found = snapshot.Rules.SelectMany(r => RuleEvaluator.Evaluate(r, snapshot, context)).Select(m => m.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            var expected = c.Expect.Select(resolver.Resolve).Select(p => p.TrimEnd('\\')).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            var extra = found.Except(expected, StringComparer.OrdinalIgnoreCase).ToList();
            var lacking = expected.Except(found, StringComparer.OrdinalIgnoreCase).ToList();
            if (extra.Count == 0 && lacking.Count == 0) return (true, "");
            string Rel(string p) => p.Replace(root, "<root>", StringComparison.OrdinalIgnoreCase);
            var msg = "";
            if (lacking.Count > 0) msg += "    thiếu:\n" + string.Join("\n", lacking.Select(p => "      " + Rel(p))) + "\n";
            if (extra.Count > 0) msg += "    thừa:\n" + string.Join("\n", extra.Select(p => "      " + Rel(p))) + "\n";
            return (false, msg);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
