using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CleanBoost.Core;
using CleanBoost.Engine.Rules;
using CleanBoost.RulePack;

// rulepack: lint → test → build → sign → verify bộ rule Windows (mục 8.5, 23.8).
Console.OutputEncoding = Encoding.UTF8;

const string Usage = """
Cách dùng: rulepack <lệnh> [tham số]

  lint <dir>                                     Kiểm tra schema, id, glob, token, biến, quyền quản trị, provider, ngôn ngữ, vùng cấm
  test <dir> --fixtures <dir>                    Chạy rule trên cây thư mục giả lập, so với kỳ vọng
  build <dir> --out <file> --min-app <x.y.z> [--version N]
                                                 Gộp → JSON → Brotli (chưa ký); tự chạy lint trước
  sign <unsigned> --key <file-base64> --out <file>
                                                 Ký Ed25519, nối chữ ký 64 byte vào cuối
  verify <bundle> [--pubkey <base64>]            Kiểm tra chữ ký và giải nén (mặc định khoá nhúng trong app)
  keygen --out <file> [--force]                  Tạo khoá ký mới, in public key
  manifest <bundle> --url <url> --min-app <x.y.z> [--disabled id1,id2] [--pubkey <base64>]
                                                 In manifest JSON cho CDN (mục 14.2)
""";

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(Usage);
    return args.Length == 0 ? 2 : 0;
}

try
{
    var a = new Arguments(args.Skip(1).ToArray());
    return args[0] switch
    {
        "lint" => RunLint(a),
        "test" => RunTest(a),
        "build" => RunBuild(a),
        "sign" => RunSign(a),
        "verify" => RunVerify(a),
        "keygen" => RunKeygen(a),
        "manifest" => RunManifest(a),
        _ => throw new CliException("Lệnh không biết: " + args[0] + "\n\n" + Usage),
    };
}
catch (CliException ex)
{
    Console.Error.WriteLine("rulepack: " + ex.Message);
    return 1;
}
catch (RuleException ex)
{
    Console.Error.WriteLine("rulepack: " + ex.Message);
    return 1;
}

static int RunLint(Arguments a)
{
    var dir = Path.GetFullPath(a.Positional(0, "thư mục rule"));
    var issues = RuleLinter.Lint(dir, out var ruleCount);
    foreach (var i in issues) Console.Error.WriteLine(i);
    var errors = issues.Count(i => i.Severity == LintSeverity.Error);
    Console.WriteLine($"lint: {ruleCount} rule, {errors} lỗi, {issues.Count - errors} cảnh báo");
    return errors == 0 ? 0 : 1;
}

static int RunTest(Arguments a)
{
    var dir = Path.GetFullPath(a.Positional(0, "thư mục rule"));
    var fixtures = Path.GetFullPath(a.Required("fixtures"));
    var payload = RuleSourceLoader.Load(dir);
    var (passed, failed) = FixtureTest.Run(payload, fixtures);
    Console.WriteLine($"test: {passed} đạt, {failed} lỗi");
    return failed == 0 ? 0 : 1;
}

static int RunBuild(Arguments a)
{
    var dir = Path.GetFullPath(a.Positional(0, "thư mục rule"));
    var output = Path.GetFullPath(a.Required("out"));
    var minApp = OSVersion.Parse(a.Required("min-app")) ?? throw new CliException("--min-app sai định dạng");
    var issues = RuleLinter.Lint(dir, out _);
    if (issues.Any(i => i.Severity == LintSeverity.Error))
    {
        foreach (var i in issues) Console.Error.WriteLine(i);
        throw new CliException("Lint lỗi, không build");
    }
    ulong? version = a.Optional("version") is { } v ? ulong.Parse(v) : null;
    var payload = RuleSourceLoader.Load(dir, version);
    if (payload.Version == 0) throw new CliException("Thiếu phiên bản rule (version.txt hoặc --version)");
    var data = RuleBundleBuilder.Pack(payload, minApp);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    File.WriteAllBytes(output, data);
    Console.WriteLine($"build: {payload.Rules.Count} rule, phiên bản {payload.Version}, {data.Length} byte → {output}");
    return 0;
}

static int RunSign(Arguments a)
{
    var input = Path.GetFullPath(a.Positional(0, "file chưa ký"));
    var output = Path.GetFullPath(a.Required("out"));
    var key = LoadPrivateKey(a.Required("key"));
    var signed = RuleBundleBuilder.Sign(File.ReadAllBytes(input), key);
    File.WriteAllBytes(output, signed);
    Console.WriteLine($"sign: {signed.Length} byte → {output}");
    Console.WriteLine("public key: " + Convert.ToBase64String(RuleBundleBuilder.PublicKeyFor(key)));
    return 0;
}

static int RunVerify(Arguments a)
{
    var input = Path.GetFullPath(a.Positional(0, "bundle"));
    var verifier = new RuleBundleVerifier(a.Optional("pubkey") ?? RulesPublicKey.Base64);
    var bundle = verifier.Verify(File.ReadAllBytes(input));
    Console.WriteLine($"verify: OK, phiên bản {bundle.Version}, app tối thiểu {OSVersion.FromPacked(bundle.Header.MinAppVersion)}, {bundle.Payload.Rules.Count} rule");
    return 0;
}

static int RunKeygen(Arguments a)
{
    var output = Path.GetFullPath(a.Required("out"));
    if (File.Exists(output) && !a.Flag("force")) throw new CliException("File khoá đã tồn tại, dùng --force để ghi đè: " + output);
    var (privateKey, publicKey) = RuleBundleBuilder.GenerateKeyPair();
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    File.WriteAllText(output, Convert.ToBase64String(privateKey));
    Console.WriteLine("Đã ghi khoá riêng: " + output + " (không commit file này)");
    Console.WriteLine("public key: " + Convert.ToBase64String(publicKey));
    return 0;
}

static int RunManifest(Arguments a)
{
    var input = Path.GetFullPath(a.Positional(0, "bundle"));
    var url = a.Required("url");
    var minApp = a.Required("min-app");
    var data = File.ReadAllBytes(input);
    RuleBundle bundle;
    // Không phát hành manifest cho bundle chưa ký hoặc ký sai.
    try { bundle = new RuleBundleVerifier(a.Optional("pubkey") ?? RulesPublicKey.Base64).Verify(data); }
    catch (Exception ex) { throw new CliException("Bundle không qua verify, không tạo manifest: " + ex.Message); }
    var manifest = new RuleManifest
    {
        Latest = bundle.Version,
        MinApp = minApp,
        Url = url,
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(data)),
        DisabledRules = a.Optional("disabled")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() ?? [],
    };
    Console.WriteLine(JsonSerializer.Serialize(manifest, new JsonSerializerOptions(RuleJson.Options) { WriteIndented = true }));
    return 0;
}

static byte[] LoadPrivateKey(string path)
{
    var full = Path.GetFullPath(path);
    if (!File.Exists(full)) throw new CliException("Không tìm thấy khoá ký: " + full);
    byte[] raw;
    try { raw = Convert.FromBase64String(File.ReadAllText(full).Trim()); } catch { throw new CliException("Khoá ký phải là base64 của 32 byte"); }
    if (raw.Length != 32) throw new CliException("Khoá ký phải là base64 của 32 byte");
    return raw;
}
