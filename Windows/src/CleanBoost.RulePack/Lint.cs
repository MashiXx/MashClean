using System.Text.Json;
using System.Text.RegularExpressions;
using CleanBoost.Core;
using CleanBoost.Engine.Rules;

namespace CleanBoost.RulePack;

public enum LintSeverity { Error, Warning }

public sealed record LintIssue(LintSeverity Severity, string File, string? RuleID, string Message)
{
    public override string ToString() =>
        $"{(Severity == LintSeverity.Error ? "lỗi" : "cảnh báo")}: {File}{(RuleID is null ? "" : " [" + RuleID + "]")}: {Message}";
}

/// <summary>
/// Kiểm tra bộ rule nguồn (mục 8.5): schema, id trùng, glob, token thư mục, biến theo app, quyền quản trị, provider,
/// đủ hai ngôn ngữ, không chạm vùng cấm của PathPolicy.
/// </summary>
public static partial class RuleLinter
{
    /// <summary>Provider cài trong code của feature Rác hệ thống.</summary>
    public static readonly string[] KnownProviders = ["recycleBin", "docker.prune", "iosBackups"];

    /// <summary>Remover chuyên biệt có trong app.</summary>
    public static readonly string[] KnownCustomRemovers = [RemoverIDs.RecycleBin, RemoverIDs.Docker];

    /// <summary>Token thư mục hợp lệ; đường dẫn tuyệt đối viết cứng bị cấm để test trên cây giả lập được.</summary>
    public static readonly string[] KnownTokens =
        ["USERPROFILE", "LOCALAPPDATA", "APPDATA", "LOCALAPPDATALOW", "TEMP", "PROGRAMDATA", "WINDIR", "SYSTEMDRIVE", "PROGRAMFILES", "PROGRAMFILES(X86)", "PUBLIC", "DOWNLOADS"];

    /// <summary>Token trỏ vào vùng hệ thống: rule nhắm vào đây phải khai báo requiresRoot.</summary>
    private static readonly string[] SystemTokens = ["WINDIR", "PROGRAMDATA", "PROGRAMFILES", "PROGRAMFILES(X86)", "SYSTEMDRIVE"];

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^\\d{10}$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex("%([^%\\\\]+)%")]
    private static partial Regex TokenPattern();

    [GeneratedRegex("\\$\\{([^}]*)\\}")]
    private static partial Regex VariablePattern();

    /// <summary>Hai thành phần glob có thể khớp cùng một tên không (ước lượng thận trọng).</summary>
    private static bool SegmentsOverlap(Glob.Segment a, Glob.Segment b)
    {
        if (a.Kind == Glob.SegmentKind.Globstar || b.Kind == Glob.SegmentKind.Globstar) return true;
        if (a.Kind == Glob.SegmentKind.Literal && b.Kind == Glob.SegmentKind.Literal) return a.Text.Equals(b.Text, StringComparison.OrdinalIgnoreCase);
        if (a.Kind == Glob.SegmentKind.Literal) return Glob.ComponentMatches(b.Text, a.Text);
        if (b.Kind == Glob.SegmentKind.Literal) return Glob.ComponentMatches(a.Text, b.Text);
        return true;
    }

    public static List<LintIssue> Lint(string directory, out int ruleCount)
    {
        var issues = new List<LintIssue>();
        var rules = new List<(Rule Rule, string File)>();
        Knowledge? knowledge = null;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(directory, file);
            var name = Path.GetFileName(file);
            var text = File.ReadAllText(file);
            try
            {
                if (name == "knowledge.json")
                {
                    knowledge = JsonSerializer.Deserialize<Knowledge>(text, RuleJson.StrictOptions);
                    continue;
                }
                if (name.StartsWith("manifest", StringComparison.Ordinal))
                {
                    JsonSerializer.Deserialize<RuleManifest>(text, RuleJson.StrictOptions);
                    continue;
                }
                if (text.TrimStart().StartsWith('['))
                {
                    foreach (var r in JsonSerializer.Deserialize<List<Rule>>(text, RuleJson.StrictOptions) ?? []) rules.Add((r, rel));
                }
                else if (JsonSerializer.Deserialize<Rule>(text, RuleJson.StrictOptions) is { } single)
                {
                    rules.Add((single, rel));
                }
            }
            catch (JsonException ex)
            {
                issues.Add(new LintIssue(LintSeverity.Error, rel, null, "JSON sai schema: " + ex.Message));
            }
        }
        ruleCount = rules.Count;

        var versionFile = Path.Combine(directory, "version.txt");
        if (!File.Exists(versionFile)) issues.Add(new LintIssue(LintSeverity.Warning, "version.txt", null, "Không có version.txt, cần truyền --version khi build"));
        else if (!VersionPattern().IsMatch(File.ReadAllText(versionFile).Trim()))
            issues.Add(new LintIssue(LintSeverity.Error, "version.txt", null, "Phiên bản phải có dạng yyyymmddNN"));

        if (knowledge is null) issues.Add(new LintIssue(LintSeverity.Error, "knowledge.json", null, "Không có knowledge.json hợp lệ"));
        else
        {
            if (!knowledge.SystemPublishers.Any(p => p.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)))
                issues.Add(new LintIssue(LintSeverity.Error, "knowledge.json", null, "systemPublishers phải chứa Microsoft"));
            if (!knowledge.OrphanWhitelist.Any(p => p.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)))
                issues.Add(new LintIssue(LintSeverity.Error, "knowledge.json", null, "orphanWhitelist phải chứa Microsoft"));
        }

        var seen = new Dictionary<string, string>();
        var policy = PathPolicy.CurrentUser;
        foreach (var (rule, file) in rules)
        {
            void Error(string m) => issues.Add(new LintIssue(LintSeverity.Error, file, rule.Id, m));
            void Warn(string m) => issues.Add(new LintIssue(LintSeverity.Warning, file, rule.Id, m));

            if (!IdPattern().IsMatch(rule.Id)) Error("id chỉ gồm a-z, 0-9, '.', '_', '-'");
            if (seen.TryGetValue(rule.Id, out var other)) Error("id trùng với " + other);
            else seen[rule.Id] = file;
            if (!RuleCategory.All.Contains(rule.Category)) Error("category không biết: " + rule.Category);
            foreach (var (label, text) in new[] { ("title", rule.Title), ("reason", rule.Reason) })
            {
                if (text is null) { Error($"thiếu {label}"); continue; }
                foreach (var lang in new[] { "vi", "en" })
                {
                    if (!text.Values.TryGetValue(lang, out var v) || string.IsNullOrWhiteSpace(v)) Error($"{label} thiếu bản '{lang}'");
                }
            }
            if (rule.Removal.IsCustom && !KnownCustomRemovers.Contains(rule.Removal.CustomID)) Error("remover không biết: " + rule.Removal.CustomID);
            if (rule.Removal.Kind == RemovalKind.MoveToTrash && rule.RequiresRoot == true) Warn("mục cần quyền quản trị không chuyển vào Thùng rác được; sẽ xoá vĩnh viễn");
            if (rule.Safety == SafetyLevel.Safe && rule.Removal.Kind == RemovalKind.MoveToTrash && rule.Category is RuleCategory.OldDownloads)
                Warn("file người dùng không nên safe (tự chọn sẵn)");

            if (rule.Match.Provider is { } provider)
            {
                if (!KnownProviders.Contains(provider)) Error("provider không biết: " + provider);
                continue;
            }
            if (rule.Match.Paths.Count == 0) Error("match.paths rỗng");
            var usesVars = false;
            foreach (var path in rule.Match.Paths.Concat(rule.Exclude ?? []))
            {
                var isExclude = !rule.Match.Paths.Contains(path);
                if (!(path.StartsWith("~\\", StringComparison.Ordinal) || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith('%')))
                    Error($"đường dẫn phải bắt đầu bằng ~\\ hoặc %TOKEN%: {path}");
                foreach (Match m in TokenPattern().Matches(path))
                {
                    if (!KnownTokens.Contains(m.Groups[1].Value, StringComparer.OrdinalIgnoreCase)) Error($"token không biết %{m.Groups[1].Value}%: {path}");
                }
                foreach (Match m in VariablePattern().Matches(path))
                {
                    var v = "${" + m.Groups[1].Value + "}";
                    if (!Rule.AppVariables.Contains(v) && v != "${userHome}") Error($"biến không biết {v}: {path}");
                    else if (v != "${userHome}") usesVars = true;
                }
                if (path.Split('\\', '/').Any(c => c == "..")) Error("đường dẫn không được chứa '..': " + path);
                if (path.Contains("**\\**", StringComparison.Ordinal)) Warn("'**' liên tiếp: " + path);
                if (isExclude) continue;

                var resolved = PathResolver.Live.Resolve(path);
                var prefix = new Glob(resolved).LiteralPrefix;
                if (policy.IsForbidden(prefix) is { Kind: PathPolicy.ViolationKind.ForbiddenZone } v2) Error($"trỏ vào vùng cấm ({v2.Detail}): {path}");
                var token = TokenPattern().Match(path) is { Success: true } tm ? tm.Groups[1].Value.ToUpperInvariant() : "";
                if (SystemTokens.Contains(token) && rule.RequiresRoot != true && !path.Contains("${", StringComparison.Ordinal))
                    Warn("đường dẫn hệ thống nên khai báo requiresRoot: " + path);
            }
            if (usesVars && rule.Match.ForEachInstalledApp != true && rule.Match.AppIDs is null && rule.Match.AppNames is null && rule.Match.Publishers is null)
                Error("dùng biến theo app nhưng không bật forEachInstalledApp hoặc lọc appIDs/appNames/publishers");
            // Exclude chỉ lọc mục khớp (và bỏ qua khi đo), không bảo vệ được thứ nằm BÊN TRONG mục bị xoá: báo lỗi.
            foreach (var ex in rule.Exclude ?? [])
            {
                var exGlob = new Glob(PathResolver.Live.Resolve(ex));
                foreach (var p in rule.Match.Paths)
                {
                    var pGlob = new Glob(PathResolver.Live.Resolve(p));
                    if (exGlob.Segments.Count > pGlob.Segments.Count && !pGlob.Segments.Any(s => s.Kind == Glob.SegmentKind.Globstar)
                        && Enumerable.Range(0, pGlob.Segments.Count).All(i => SegmentsOverlap(pGlob.Segments[i], exGlob.Segments[i])))
                        Error($"exclude nằm bên trong mục bị xoá nên không có tác dụng khi xoá: {ex} (trong {p})");
                }
            }
            if (rule.Match.MinAgeDays is < 0) Error("minAgeDays phải ≥ 0");
            if (rule.Match.MinSizeBytes is < 0) Error("minSizeBytes phải ≥ 0");
            if (rule.Conditions?.MinOS is { } minOS && OSVersion.Parse(minOS) is null) Error("minOS sai định dạng: " + minOS);
        }
        return issues;
    }
}
