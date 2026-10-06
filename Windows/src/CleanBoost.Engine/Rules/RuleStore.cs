using System.Security.Cryptography;
using CleanBoost.Core;

namespace CleanBoost.Engine.Rules;

/// <summary>
/// Khoá công khai Ed25519 nhúng trong app (mục 8.5). Khoá riêng chỉ nằm trong secret của CI
/// (bản dev: <c>Secrets/windows_rules_signing_key.b64</c>, không commit).
/// </summary>
public static class RulesPublicKey
{
    public const string Base64 = "vvfPXIT6HFHV3fLZ9+P4FX80+LnmOVP0xF0XENPkCYg=";
}

/// <summary>
/// Quy đổi glob của rule thành đường dẫn thật. Token thư mục cố định (không đọc biến môi trường tuỳ ý), đổi được gốc
/// khi chạy test trên cây thư mục giả lập (mục 18).
/// </summary>
public sealed class PathResolver
{
    public string Home { get; }
    public IReadOnlyDictionary<string, string> Tokens { get; }

    public PathResolver(string home, IReadOnlyDictionary<string, string> tokens)
    {
        Home = home;
        Tokens = new Dictionary<string, string>(tokens, StringComparer.OrdinalIgnoreCase);
    }

    public static PathResolver Live { get; } = new(KnownPaths.UserHome, new Dictionary<string, string>
    {
        ["USERPROFILE"] = KnownPaths.UserHome,
        ["LOCALAPPDATA"] = KnownPaths.LocalAppData,
        ["APPDATA"] = KnownPaths.RoamingAppData,
        ["LOCALAPPDATALOW"] = KnownPaths.LocalLowAppData,
        ["TEMP"] = KnownPaths.Temp,
        ["PROGRAMDATA"] = KnownPaths.ProgramData,
        ["WINDIR"] = KnownPaths.Windows,
        ["SYSTEMDRIVE"] = KnownPaths.SystemDrive,
        ["PROGRAMFILES"] = KnownPaths.ProgramFiles,
        ["PROGRAMFILES(X86)"] = KnownPaths.ProgramFilesX86,
        ["PUBLIC"] = KnownPaths.PublicProfile,
        ["DOWNLOADS"] = KnownPaths.Downloads,
    });

    /// <summary>Cây giả lập cho test: <c>&lt;root&gt;\Users\tester</c>, <c>&lt;root&gt;\Windows</c>...</summary>
    public static PathResolver Fixture(string root)
    {
        var home = Path.Combine(root, "Users", "tester");
        return new PathResolver(home, new Dictionary<string, string>
        {
            ["USERPROFILE"] = home,
            ["LOCALAPPDATA"] = Path.Combine(home, "AppData", "Local"),
            ["APPDATA"] = Path.Combine(home, "AppData", "Roaming"),
            ["LOCALAPPDATALOW"] = Path.Combine(home, "AppData", "LocalLow"),
            ["TEMP"] = Path.Combine(home, "AppData", "Local", "Temp"),
            ["PROGRAMDATA"] = Path.Combine(root, "ProgramData"),
            ["WINDIR"] = Path.Combine(root, "Windows"),
            ["SYSTEMDRIVE"] = root.TrimEnd('\\'),
            ["PROGRAMFILES"] = Path.Combine(root, "Program Files"),
            ["PROGRAMFILES(X86)"] = Path.Combine(root, "Program Files (x86)"),
            ["PUBLIC"] = Path.Combine(root, "Users", "Public"),
            ["DOWNLOADS"] = Path.Combine(home, "Downloads"),
        });
    }

    /// <summary>Thay token <c>%NAME%</c>, <c>~</c> và <c>${userHome}</c>.</summary>
    public string Resolve(string pattern)
    {
        var p = pattern.Replace('/', '\\').Replace("${userHome}", Home, StringComparison.Ordinal);
        if (p == "~") return Home;
        if (p.StartsWith("~\\", StringComparison.Ordinal)) p = Home + p[1..];
        var start = p.IndexOf('%');
        while (start >= 0)
        {
            var end = p.IndexOf('%', start + 1);
            if (end < 0) break;
            var name = p[(start + 1)..end];
            if (Tokens.TryGetValue(name, out var value))
            {
                p = p[..start] + value.TrimEnd('\\') + p[(end + 1)..];
                start = p.IndexOf('%', start + value.Length);
            }
            else
            {
                start = p.IndexOf('%', end + 1);
            }
        }
        return p;
    }

    /// <summary>Glob có còn token chưa biết không (lỗi soạn rule).</summary>
    public static bool HasUnknownToken(string resolved) => resolved.Contains('%');

    public Glob Glob(string pattern) => new(Resolve(pattern), Home);
}

/// <summary>Rule đã biên dịch: glob tĩnh được biên dịch một lần lúc nạp (mục 8.6 bước 2).</summary>
public sealed class CompiledRule
{
    public Rule Rule { get; }
    /// <summary><c>null</c> khi rule có biến theo app (phải mở rộng theo từng app).</summary>
    public IReadOnlyList<Glob>? StaticGlobs { get; }
    public GlobSet StaticExcludes { get; }
    public OSVersion? MinOS { get; }

    public string Id => Rule.Id;

    public CompiledRule(Rule rule, PathResolver resolver)
    {
        Rule = rule;
        var excludesHaveVars = (rule.Exclude ?? []).Any(e => Rule.AppVariables.Any(v => e.Contains(v, StringComparison.Ordinal)));
        StaticGlobs = rule.UsesAppVariables ? null : rule.Match.Paths.Select(resolver.Glob).ToArray();
        StaticExcludes = excludesHaveVars ? GlobSet.Empty : new GlobSet((rule.Exclude ?? []).Select(resolver.Glob));
        MinOS = OSVersion.Parse(rule.Conditions?.MinOS);
    }

    public bool AppliesTo(RuleAppInfo app)
    {
        static bool Matches(List<string>? filter, string? value) =>
            filter is not { Count: > 0 } || (!string.IsNullOrEmpty(value) && filter.Any(p => Glob.ComponentMatches(p, value)));
        return Matches(Rule.Match.AppIDs, app.AppID) && Matches(Rule.Match.AppNames, app.AppName) && Matches(Rule.Match.Publishers, app.Publisher);
    }
}

/// <summary>Ảnh chụp bất biến của bộ rule đang dùng; truyền vào ScanContext.</summary>
public sealed class RuleSnapshot
{
    public enum SourceKind { Bundled, Downloaded, Development, Empty }

    public ulong Version { get; }
    public IReadOnlyList<CompiledRule> Rules { get; }
    public Knowledge Knowledge { get; }
    public IReadOnlySet<string> Disabled { get; }
    public PathResolver Resolver { get; }
    public SourceKind Source { get; }

    public RuleSnapshot(ulong version, IEnumerable<Rule> rules, Knowledge knowledge, IEnumerable<string>? disabled = null, PathResolver? resolver = null,
        SourceKind source = SourceKind.Bundled)
    {
        Version = version;
        Resolver = resolver ?? PathResolver.Live;
        Rules = rules.Select(r => new CompiledRule(r, Resolver)).ToArray();
        Knowledge = knowledge;
        Disabled = (disabled ?? []).ToHashSet();
        Source = source;
    }

    public static readonly RuleSnapshot Empty = new(0, [], new Knowledge(), source: SourceKind.Empty);

    /// <summary>Rule đang bật thuộc một nhóm.</summary>
    public IReadOnlyList<CompiledRule> RulesIn(string category) => Rules.Where(r => r.Rule.Category == category && !Disabled.Contains(r.Id)).ToArray();

    public CompiledRule? Rule(string id) => Rules.FirstOrDefault(r => r.Id == id);

    public IEnumerable<CompiledRule> ActiveRules => Rules.Where(r => !Disabled.Contains(r.Id));

    public RuleSnapshot WithDisabled(IEnumerable<string> ids) =>
        new(Version, Rules.Select(r => r.Rule), Knowledge, Disabled.Union(ids), Resolver, Source);

    public RuleSnapshot WithResolver(PathResolver resolver) =>
        new(Version, Rules.Select(r => r.Rule), Knowledge, Disabled, resolver, Source);

    public string SourceTitle => Source switch
    {
        SourceKind.Bundled => Loc.T("Đi kèm ứng dụng"),
        SourceKind.Downloaded => Loc.T("Đã tải về"),
        SourceKind.Development => Loc.T("Thư mục phát triển (chưa ký)"),
        _ => Loc.T("Không có"),
    };
}

/// <summary>Nạp bộ rule mới nhất hợp lệ (mục 8.6 bước 1): bộ đã tải về (chữ ký hợp lệ, phiên bản cao hơn) hoặc bộ đi kèm app.</summary>
public sealed class RuleStore
{
    public string? BundledPath { get; }
    public string CacheDirectory { get; }
    private readonly RuleBundleVerifier _verifier;
    private readonly PathResolver _resolver;
    private readonly OSVersion _appVersion;
    private readonly object _lock = new();
    private RuleSnapshot _snapshot = RuleSnapshot.Empty;

    public RuleStore(string? bundled, string cache, string publicKeyBase64 = RulesPublicKey.Base64, PathResolver? resolver = null, OSVersion? appVersion = null)
    {
        BundledPath = bundled;
        CacheDirectory = cache;
        _verifier = new RuleBundleVerifier(publicKeyBase64);
        _resolver = resolver ?? PathResolver.Live;
        _appVersion = appVersion ?? AppVersion.Current;
        _snapshot = Load();
    }

    public RuleSnapshot Snapshot
    {
        get { lock (_lock) return _snapshot; }
    }

    public event Action<RuleSnapshot>? SnapshotChanged;

    /// <summary>Mặc định: <c>Rules\rules.bundle</c> cạnh file exe; cache tại <c>%LOCALAPPDATA%\CleanBoost\Rules</c>.</summary>
    public static string DefaultCacheDirectory => Path.Combine(KnownPaths.AppData, "Rules");
    public static string DefaultBundledPath => Path.Combine(AppContext.BaseDirectory, "Rules", "rules.bundle");

    public void Reload()
    {
        var snap = Load();
        lock (_lock) _snapshot = snap;
        SnapshotChanged?.Invoke(snap);
    }

    private RuleSnapshot Load()
    {
#if DEBUG
        // Rule tác giả đang viết: nạp thẳng thư mục JSON chưa ký (chỉ bản Debug).
        if (Environment.GetEnvironmentVariable("MASHCLEAN_RULES_DIR") is { Length: > 0 } dir)
        {
            try
            {
                var payload = RuleSourceLoader.Load(dir);
                Log.Warning(LogCategory.Rules, "Đang dùng rule chưa ký từ MASHCLEAN_RULES_DIR (chỉ Debug)");
                return new RuleSnapshot(payload.Version, payload.Rules, payload.Knowledge, resolver: _resolver, source: RuleSnapshot.SourceKind.Development);
            }
            catch (Exception ex)
            {
                Log.Error(LogCategory.Rules, "Không nạp được MASHCLEAN_RULES_DIR", ex);
            }
        }
#endif
        (RuleBundle Bundle, RuleSnapshot.SourceKind Source)? best = null;
        if (BundledPath is not null && File.Exists(BundledPath))
        {
            try { best = (_verifier.Verify(File.ReadAllBytes(BundledPath)), RuleSnapshot.SourceKind.Bundled); }
            catch (Exception ex) { Log.Error(LogCategory.Rules, "Bộ rule đi kèm không hợp lệ", ex); }
        }
        foreach (var file in CachedBundles())
        {
            try
            {
                var bundle = _verifier.Verify(File.ReadAllBytes(file));
                if (OSVersion.FromPacked(bundle.Header.MinAppVersion) > _appVersion) continue;
                if (bundle.Version > (best?.Bundle.Version ?? 0)) best = (bundle, RuleSnapshot.SourceKind.Downloaded);
            }
            catch (Exception ex)
            {
                Log.Error(LogCategory.Rules, "Bỏ rule cache " + Path.GetFileName(file), ex);
            }
        }
        if (best is not { } b)
        {
            Log.Error(LogCategory.Rules, "Không có bộ rule hợp lệ nào");
            return RuleSnapshot.Empty;
        }
        Log.Info(LogCategory.Rules, $"Nạp rule phiên bản {b.Bundle.Version} ({b.Source}), {b.Bundle.Payload.Rules.Count} rule");
        return new RuleSnapshot(b.Bundle.Version, b.Bundle.Payload.Rules, b.Bundle.Payload.Knowledge, resolver: _resolver, source: b.Source);
    }

    private IEnumerable<string> CachedBundles()
    {
        try { return Directory.Exists(CacheDirectory) ? Directory.GetFiles(CacheDirectory, "*.bundle") : []; }
        catch { return []; }
    }

    /// <summary>Cài bộ rule tải về (mục 14.2): kiểm tra SHA-256, chữ ký, phiên bản app, chống hạ cấp. Giữ 2 phiên bản gần nhất.</summary>
    public RuleSnapshot Install(byte[] data, string? expectedSha256, IReadOnlyList<string>? disabledRules = null)
    {
        if (expectedSha256 is not null)
        {
            var digest = Convert.ToHexStringLower(SHA256.HashData(data));
            if (!digest.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) throw new RuleException(RuleException.Kind.ChecksumMismatch);
        }
        var bundle = _verifier.Verify(data);
        var current = Snapshot.Version;
        if (bundle.Version <= current) throw new RuleException(RuleException.Kind.Downgrade, $"{current} ≥ {bundle.Version}");
        var minApp = OSVersion.FromPacked(bundle.Header.MinAppVersion);
        if (minApp > _appVersion) throw new RuleException(RuleException.Kind.AppTooOld, minApp.ToString());

        Directory.CreateDirectory(CacheDirectory);
        var file = Path.Combine(CacheDirectory, $"rules-{bundle.Version}.bundle");
        var tmp = file + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, file, overwrite: true);
        PruneCache(keep: 2);

        var snap = new RuleSnapshot(bundle.Version, bundle.Payload.Rules, bundle.Payload.Knowledge, disabledRules, _resolver, RuleSnapshot.SourceKind.Downloaded);
        lock (_lock) _snapshot = snap;
        SnapshotChanged?.Invoke(snap);
        Log.Info(LogCategory.Rules, $"Đã cài rule phiên bản {bundle.Version}, dùng từ lần quét kế tiếp");
        return snap;
    }

    /// <summary>Kill switch: tắt ngay các rule lỗi theo manifest (mục 14.2).</summary>
    public void ApplyDisabledRules(IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0) return;
        RuleSnapshot snap;
        lock (_lock) snap = _snapshot = _snapshot.WithDisabled(ids);
        SnapshotChanged?.Invoke(snap);
        Log.Warning(LogCategory.Rules, $"Tắt {ids.Count} rule theo manifest: {string.Join(", ", ids)}");
    }

    private void PruneCache(int keep)
    {
        static ulong VersionOf(string f) => ulong.TryParse(Path.GetFileNameWithoutExtension(f).Replace("rules-", ""), out var v) ? v : 0;
        foreach (var old in CachedBundles().OrderByDescending(VersionOf).Skip(keep))
        {
            try { File.Delete(old); } catch { }
        }
    }
}
