using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using CleanBoost.Core;
using CleanBoost.Engine.Rules;

namespace CleanBoost.Tests.Engine;

/// <summary>Đóng gói, ký Ed25519 và kiểm tra bộ rule (mục 8.5): mọi thay đổi một bit đều bị từ chối.</summary>
public class RuleBundleTests
{
    private static readonly (byte[] Private, byte[] Public) Keys = RuleBundleBuilder.GenerateKeyPair();
    private static readonly OSVersion MinApp = new(1, 0, 0);

    internal static RulePayload Payload(ulong version = 2026100501, string platform = "windows") => new()
    {
        Version = version,
        Platform = platform,
        GeneratedAt = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero),
        Rules =
        [
            TestRules.Make("win.temp.user", RuleCategory.UserTemp, [@"%TEMP%\*"], removal: RemovalStrategy.Delete, minAgeDays: 1,
                exclude: [@"%TEMP%\CleanBoost*"], title: new LocalizedText("Tệp tạm", "Temporary files")),
            TestRules.Make("win.app.leftovers", RuleCategory.AppLeftovers, [@"%APPDATA%\${publisher}\${appName}"], SafetyLevel.Review,
                RemovalStrategy.MoveToTrash, forEachApp: true, appNotRunning: ["${appID}"], minOS: "10.0.17763"),
        ],
        Knowledge = new Knowledge { ProtectedCacheNames = ["Steam"], SystemPublishers = ["Microsoft Corporation"] },
    };

    private static RuleBundleVerifier Verifier => new(Convert.ToBase64String(Keys.Public));

    internal static byte[] Signed(RulePayload payload, byte[] privateKey, OSVersion? minApp = null) =>
        RuleBundleBuilder.Sign(RuleBundleBuilder.Pack(payload, minApp ?? MinApp), privateKey);

    private static RuleException.Kind KindOf(Action a) => Assert.Throws<RuleException>(a).ErrorKind;

    /// <summary>Ký một bundle tự dựng (header/payload tuỳ ý) để kiểm tra các nhánh lỗi sau bước chữ ký.</summary>
    private static byte[] SignRaw(ushort format, ulong version, uint minApp, byte[] payload, uint? declaredLength = null)
    {
        var header = new RuleBundleHeader(format, version, minApp, declaredLength ?? (uint)payload.Length).Serialize();
        return RuleBundleBuilder.Sign([.. header, .. payload], Keys.Private);
    }

    private static byte[] Brotli(byte[] data)
    {
        using var output = new MemoryStream();
        using (var b = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true)) b.Write(data);
        return output.ToArray();
    }

    [Fact]
    public void KeyPairShapes()
    {
        Assert.Equal(32, Keys.Private.Length);
        Assert.Equal(32, Keys.Public.Length);
        Assert.Equal(Keys.Public, RuleBundleBuilder.PublicKeyFor(Keys.Private));
        var other = RuleBundleBuilder.GenerateKeyPair();
        Assert.NotEqual(Keys.Public, other.PublicKey);
    }

    [Fact]
    public void PackSignVerifyRoundTrip()
    {
        var data = Signed(Payload(), Keys.Private, new OSVersion(1, 2, 3));
        Assert.Equal("MSRW", Encoding.ASCII.GetString(data, 0, 4));

        var bundle = Verifier.Verify(data);

        Assert.Equal(2026100501ul, bundle.Version);
        Assert.Equal(RuleBundleHeader.CurrentFormatVersion, bundle.Header.FormatVersion);
        Assert.Equal(new OSVersion(1, 2, 3), OSVersion.FromPacked(bundle.Header.MinAppVersion));
        Assert.Equal((uint)(data.Length - RuleBundleHeader.Size - RuleBundleHeader.SignatureSize), bundle.Header.PayloadLength);
        Assert.Equal(2, bundle.Payload.Rules.Count);
        var temp = bundle.Payload.Rules[0];
        Assert.Equal("win.temp.user", temp.Id);
        Assert.Equal(RemovalStrategy.Delete, temp.Removal);
        Assert.Equal(SafetyLevel.Safe, temp.Safety);
        Assert.Equal(1, temp.Match.MinAgeDays);
        Assert.Equal(new[] { @"%TEMP%\CleanBoost*" }, temp.Exclude);
        Assert.Equal("Temporary files", temp.Title?.Values["en"]);
        var app = bundle.Payload.Rules[1];
        Assert.Equal(SafetyLevel.Review, app.Safety);
        Assert.Equal(RemovalStrategy.MoveToTrash, app.Removal);
        Assert.True(app.UsesAppVariables);
        Assert.Equal("10.0.17763", app.Conditions?.MinOS);
        Assert.Equal(new[] { "Steam" }, bundle.Payload.Knowledge.ProtectedCacheNames);
    }

    [Fact]
    public void EverySingleBitFlipIsRejected()
    {
        var data = Signed(Payload(), Keys.Private);
        // Lật một bit ở header, payload và chữ ký: luôn bị từ chối (BadMagic nếu rơi vào magic, còn lại BadSignature).
        int[] offsets = [0, 3, 4, 6, 14, 18, 22, 23, data.Length / 2, data.Length - 65, data.Length - 64, data.Length - 1];
        foreach (var offset in offsets)
        {
            foreach (var bit in new[] { 0, 7 })
            {
                var tampered = (byte[])data.Clone();
                tampered[offset] ^= (byte)(1 << bit);
                var kind = KindOf(() => Verifier.Verify(tampered));
                Assert.True(offset < 4 ? kind == RuleException.Kind.BadMagic : kind == RuleException.Kind.BadSignature,
                    $"offset {offset} bit {bit}: {kind}");
            }
        }
    }

    [Fact]
    public void WrongKeyIsRejected()
    {
        var other = RuleBundleBuilder.GenerateKeyPair();
        Assert.Equal(RuleException.Kind.BadSignature, KindOf(() => Verifier.Verify(Signed(Payload(), other.PrivateKey))));
    }

    [Fact]
    public void WrongPlatformIsRejected()
    {
        Assert.Equal(RuleException.Kind.WrongPlatform, KindOf(() => Verifier.Verify(Signed(Payload(platform: "macos"), Keys.Private))));
        Assert.Equal(RuleException.Kind.WrongPlatform, KindOf(() => Verifier.Verify(Signed(Payload(platform: ""), Keys.Private))));
        Assert.NotNull(Verifier.Verify(Signed(Payload(platform: "Windows"), Keys.Private)));
    }

    [Fact]
    public void WrongMagicIsRejected()
    {
        var data = Signed(Payload(), Keys.Private);
        Encoding.ASCII.GetBytes("MSRM").CopyTo(data, 0); // magic của bộ rule Mac
        Assert.Equal(RuleException.Kind.BadMagic, KindOf(() => Verifier.Verify(data)));
        Assert.Equal(RuleException.Kind.BadMagic, KindOf(() => RuleBundleHeader.Parse(data)));
    }

    [Fact]
    public void TruncatedBundlesAreRejected()
    {
        var data = Signed(Payload(), Keys.Private);
        Assert.Equal(RuleException.Kind.BadFormat, KindOf(() => Verifier.Verify([])));
        Assert.Equal(RuleException.Kind.BadFormat, KindOf(() => Verifier.Verify(data[..10])));
        Assert.Equal(RuleException.Kind.BadFormat, KindOf(() => Verifier.Verify(data[..(RuleBundleHeader.Size + RuleBundleHeader.SignatureSize)])));
        Assert.Equal(RuleException.Kind.BadSignature, KindOf(() => Verifier.Verify(data[..^1])));
        Assert.Equal(RuleException.Kind.BadSignature, KindOf(() => Verifier.Verify(data[..^64])));
        Assert.Equal(RuleException.Kind.BadSignature, KindOf(() => Verifier.Verify([.. data, 0])));
        Assert.Equal(RuleException.Kind.BadFormat, KindOf(() => RuleBundleHeader.Parse(data.AsSpan(0, 21))));
    }

    [Fact]
    public void SignedButInconsistentHeaderIsRejected()
    {
        var json = RuleBundleBuilder.EncodePayload(Payload());
        var compressed = Brotli(json);
        Assert.Equal(RuleException.Kind.PayloadLengthMismatch,
            KindOf(() => Verifier.Verify(SignRaw(1, 1, MinApp.Packed, compressed, declaredLength: (uint)compressed.Length + 1))));
        Assert.Equal(RuleException.Kind.UnsupportedFormatVersion, KindOf(() => Verifier.Verify(SignRaw(2, 1, MinApp.Packed, compressed))));
        Assert.Equal(RuleException.Kind.DecompressionFailed, KindOf(() => Verifier.Verify(SignRaw(1, 1, MinApp.Packed, json))));
        Assert.Equal(RuleException.Kind.InvalidJSON, KindOf(() => Verifier.Verify(SignRaw(1, 1, MinApp.Packed, Brotli("{\"rules\": ["u8.ToArray())))));
        Assert.Equal(RuleException.Kind.InvalidJSON,
            KindOf(() => Verifier.Verify(SignRaw(1, 1, MinApp.Packed, Brotli("""{"rules":[{"id":"x","category":"c","safety":"yolo","match":{}}]}"""u8.ToArray())))));
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    [InlineData("")]
    public void VerifierRejectsBadPublicKeys(string key) =>
        Assert.Equal(RuleException.Kind.BadFormat, KindOf(() => _ = new RuleBundleVerifier(key)));

    [Fact]
    public void EmbeddedPublicKeyIsWellFormed() => Assert.NotNull(new RuleBundleVerifier(RulesPublicKey.Base64));

    [Fact]
    public void HeaderSerializeParseRoundTrip()
    {
        var h = new RuleBundleHeader(1, 2026100599, new OSVersion(2, 5, 1).Packed, 12345);
        var bytes = h.Serialize();
        Assert.Equal(RuleBundleHeader.Size, bytes.Length);
        Assert.Equal(h, RuleBundleHeader.Parse(bytes));
    }

    [Fact]
    public void SourceLoaderReadsRuleFilesAndKnowledge()
    {
        using var tmp = new TempDir("rulesrc");
        File.WriteAllText(tmp.P("a.json"), """
            [
              // chú thích và dấu phẩy thừa được chấp nhận
              {"id":"r1","category":"userTemp","safety":"safe","removal":"delete","match":{"paths":["%TEMP%\\*"]}},
            ]
            """);
        File.WriteAllText(tmp.P("b.json"), """{"id":"r2","category":"userLogs","safety":"review","removal":"deleteContents","match":{"paths":["%LOCALAPPDATA%\\*\\Logs"]}}""");
        File.WriteAllText(tmp.P("knowledge.json"), """{"protectedCacheNames":["Steam"]}""");
        File.WriteAllText(tmp.P("manifest.json"), "not json at all");
        File.WriteAllText(tmp.P("version.txt"), "2026100507\n");

        var payload = RuleSourceLoader.Load(tmp.Root);

        Assert.Equal(2026100507ul, payload.Version);
        Assert.Equal(new[] { "r1", "r2" }, payload.Rules.Select(r => r.Id));
        Assert.Equal(new[] { "Steam" }, payload.Knowledge.ProtectedCacheNames);
        Assert.Equal(RuleException.Kind.BadFormat, KindOf(() => RuleSourceLoader.Load(tmp.P("missing"))));

        File.WriteAllText(tmp.P("c.json"), """{"id":"r3","category":"x","safety":"safe","removal":"delete","match":{"paths":[]},"surprise":1}""");
        Assert.Equal(3, RuleSourceLoader.Load(tmp.Root).Rules.Count);
        Assert.Equal(RuleException.Kind.InvalidJSON, KindOf(() => RuleSourceLoader.Load(tmp.Root, strict: true)));
    }
}

/// <summary>Nạp và cài bộ rule (mục 8.6, 14.2): chọn bản mới nhất hợp lệ, chống hạ cấp, kiểm tra SHA-256 và phiên bản app.</summary>
public sealed class RuleStoreTests : IDisposable
{
    private readonly TempDir _tmp = new("rulestore");
    private static readonly (byte[] Private, byte[] Public) Keys = RuleBundleBuilder.GenerateKeyPair();
    private static readonly string PublicKey = Convert.ToBase64String(Keys.Public);
    private static readonly OSVersion App = new(1, 5, 0);

    public RuleStoreTests() => Environment.SetEnvironmentVariable("MASHCLEAN_RULES_DIR", null); // không để rule phát triển chen vào

    public void Dispose() => _tmp.Dispose();

    private string Cache => _tmp.P("cache");

    private static byte[] Bundle(ulong version, OSVersion? minApp = null, byte[]? key = null) =>
        RuleBundleTests.Signed(RuleBundleTests.Payload(version), key ?? Keys.Private, minApp ?? new OSVersion(1));

    private string WriteBundled(ulong version)
    {
        var path = _tmp.P("bundled.bundle");
        File.WriteAllBytes(path, Bundle(version));
        return path;
    }

    private RuleStore Store(string? bundled) => new(bundled, Cache, PublicKey, PathResolver.Fixture(_tmp.Root), App);

    [Fact]
    public void LoadsBundledRules()
    {
        var store = Store(WriteBundled(100));
        Assert.Equal(100ul, store.Snapshot.Version);
        Assert.Equal(RuleSnapshot.SourceKind.Bundled, store.Snapshot.Source);
        Assert.Equal(2, store.Snapshot.Rules.Count);
    }

    [Fact]
    public void MissingOrInvalidBundleGivesEmptySnapshot()
    {
        Assert.Equal(RuleSnapshot.SourceKind.Empty, Store(null).Snapshot.Source);
        var bad = _tmp.P("bad.bundle");
        File.WriteAllBytes(bad, Bundle(100, key: RuleBundleBuilder.GenerateKeyPair().PrivateKey));
        var store = Store(bad);
        Assert.Equal(0ul, store.Snapshot.Version);
        Assert.Empty(store.Snapshot.Rules);
    }

    [Fact]
    public void InstallNewerVersionAndRejectDowngrade()
    {
        var store = Store(WriteBundled(100));
        RuleSnapshot? notified = null;
        store.SnapshotChanged += s => notified = s;

        var snap = store.Install(Bundle(101), expectedSha256: null);

        Assert.Equal(101ul, snap.Version);
        Assert.Equal(RuleSnapshot.SourceKind.Downloaded, snap.Source);
        Assert.Same(snap, store.Snapshot);
        Assert.Same(snap, notified);
        Assert.True(File.Exists(Path.Combine(Cache, "rules-101.bundle")));
        Assert.Empty(Directory.GetFiles(Cache, "*.tmp"));

        Assert.Equal(RuleException.Kind.Downgrade, Assert.Throws<RuleException>(() => store.Install(Bundle(101), null)).ErrorKind);
        Assert.Equal(RuleException.Kind.Downgrade, Assert.Throws<RuleException>(() => store.Install(Bundle(99), null)).ErrorKind);
        Assert.Equal(101ul, store.Snapshot.Version);
    }

    [Fact]
    public void InstallVerifiesChecksumSignatureAndAppVersion()
    {
        var store = Store(WriteBundled(100));
        var data = Bundle(102);
        var sha = Convert.ToHexString(SHA256.HashData(data));

        Assert.Equal(RuleException.Kind.ChecksumMismatch, Assert.Throws<RuleException>(() => store.Install(data, new string('0', 64))).ErrorKind);
        var tampered = (byte[])data.Clone();
        tampered[30] ^= 1;
        Assert.Equal(RuleException.Kind.BadSignature, Assert.Throws<RuleException>(() => store.Install(tampered, null)).ErrorKind);
        Assert.Equal(RuleException.Kind.AppTooOld,
            Assert.Throws<RuleException>(() => store.Install(Bundle(103, minApp: new OSVersion(2)), null)).ErrorKind);
        Assert.Equal(100ul, store.Snapshot.Version);
        Assert.False(Directory.Exists(Cache) && Directory.GetFiles(Cache).Length > 0);

        Assert.Equal(102ul, store.Install(data, sha.ToLowerInvariant()).Version); // so SHA không phân biệt hoa thường
    }

    [Fact]
    public void CacheKeepsTwoNewestBundles()
    {
        var store = Store(WriteBundled(100));
        foreach (var v in new ulong[] { 101, 102, 103 }) store.Install(Bundle(v), null);
        var names = Directory.GetFiles(Cache).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "rules-102.bundle", "rules-103.bundle" }, names);
    }

    [Fact]
    public void ReloadPrefersNewestValidCachedBundle()
    {
        Directory.CreateDirectory(Cache);
        File.WriteAllBytes(Path.Combine(Cache, "rules-150.bundle"), Bundle(150));
        File.WriteAllBytes(Path.Combine(Cache, "rules-300.bundle"), Bundle(300, minApp: new OSVersion(9)));                          // cần app mới hơn
        File.WriteAllBytes(Path.Combine(Cache, "rules-400.bundle"), Bundle(400, key: RuleBundleBuilder.GenerateKeyPair().PrivateKey)); // sai chữ ký
        File.WriteAllBytes(Path.Combine(Cache, "rules-500.bundle"), [1, 2, 3]);

        var store = Store(WriteBundled(100));

        Assert.Equal(150ul, store.Snapshot.Version);
        Assert.Equal(RuleSnapshot.SourceKind.Downloaded, store.Snapshot.Source);
    }

    [Fact]
    public void OlderCachedBundleDoesNotOverrideBundled()
    {
        Directory.CreateDirectory(Cache);
        File.WriteAllBytes(Path.Combine(Cache, "rules-90.bundle"), Bundle(90));
        var store = Store(WriteBundled(100));
        Assert.Equal(100ul, store.Snapshot.Version);
        Assert.Equal(RuleSnapshot.SourceKind.Bundled, store.Snapshot.Source);
    }

    [Fact]
    public void KillSwitchDisablesRules()
    {
        var store = Store(WriteBundled(100));
        store.ApplyDisabledRules(["win.temp.user"]);
        Assert.Contains("win.temp.user", store.Snapshot.Disabled);
        Assert.Empty(store.Snapshot.RulesIn(RuleCategory.UserTemp));
        Assert.Single(store.Snapshot.RulesIn(RuleCategory.AppLeftovers));
        Assert.Single(store.Snapshot.ActiveRules);
    }
}
