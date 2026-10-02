import CryptoKit
import Foundation
import Testing
import RuleEngine
import SweepCore
import SystemJunkScanning

/// Đóng gói, ký, kiểm tra rule bundle và cài đặt từ CDN (mục 8.5, 14.2).
@Suite struct RuleBundleTests {
    let key = Curve25519.Signing.PrivateKey()
    var pub: String { key.publicKey.rawRepresentation.base64EncodedString() }

    func payload(_ version: UInt64, rules: [Rule]? = nil) -> RulePayload {
        RulePayload(version: version, rules: rules ?? [makeRule("t.cache", paths: ["~/Library/Caches/*"], removal: .deleteContents)],
                    knowledge: Knowledge(orphanWhitelist: ["com.apple.*"]))
    }

    func signed(_ version: UInt64, minApp: String = "1.0.0", key k: Curve25519.Signing.PrivateKey? = nil) throws -> Data {
        let unsigned = try RuleBundleBuilder.pack(payload: payload(version), minAppVersion: OSVersion(minApp)!)
        return try RuleBundleBuilder.sign(unsigned, privateKey: k ?? key)
    }

    /// Ký lại sau khi sửa header để chỉ kiểm tra phần phân tích header, không phải chữ ký.
    func resigned(_ data: Data, mutate: (inout Data) -> Void) throws -> Data {
        var unsigned = Data(data.dropLast(RuleBundleHeader.signatureSize))
        mutate(&unsigned)
        return try RuleBundleBuilder.sign(unsigned, privateKey: key)
    }

    @Test func roundTrip() throws {
        let data = try signed(2026100201, minApp: "1.2.3")
        let bundle = try RuleBundleVerifier(publicKeyBase64: pub).verify(data)
        #expect(bundle.version == 2026100201)
        #expect(OSVersion(packed: bundle.header.minAppVersion) == OSVersion("1.2.3"))
        #expect(bundle.payload.rules.map(\.id) == [RuleID("t.cache")])
        #expect(bundle.payload.rules.first?.removal == .deleteContents)
        #expect(bundle.payload.knowledge.orphanWhitelist == ["com.apple.*"])
    }

    @Test func headerLayoutIsLittleEndian() throws {
        let data = try signed(2026100201)
        #expect(data.prefix(4) == Data("MSRB".utf8))
        #expect(data[4] == 1 && data[5] == 0)
        let header = try RuleBundleHeader(parsing: data)
        #expect(header.rulesVersion == 2026100201)
        #expect(header.payloadLength == UInt32(data.count - RuleBundleHeader.size - RuleBundleHeader.signatureSize))
        #expect(header.serialized == data.prefix(RuleBundleHeader.size))
    }

    @Test func tamperedPayloadFailsSignature() throws {
        var data = try signed(2026100201)
        data[RuleBundleHeader.size + 3] ^= 0xFF
        #expect(throws: RuleError.badSignature) { try RuleBundleVerifier(publicKeyBase64: pub).verify(data) }
    }

    @Test func tamperedSignatureFails() throws {
        var data = try signed(2026100201)
        data[data.count - 1] ^= 0x01
        #expect(throws: RuleError.badSignature) { try RuleBundleVerifier(publicKeyBase64: pub).verify(data) }
    }

    @Test func wrongKeyFails() throws {
        let data = try signed(2026100201, key: Curve25519.Signing.PrivateKey())
        #expect(throws: RuleError.badSignature) { try RuleBundleVerifier(publicKeyBase64: pub).verify(data) }
    }

    @Test func badMagic() throws {
        var data = try signed(2026100201)
        data[0] = UInt8(ascii: "X")
        #expect(throws: RuleError.badMagic) { try RuleBundleVerifier(publicKeyBase64: pub).verify(data) }
    }

    @Test func tooShort() {
        #expect(throws: RuleError.badFormat) { try RuleBundleVerifier(publicKeyBase64: pub).verify(Data("MSRB".utf8)) }
        #expect(throws: RuleError.badFormat) { try RuleBundleHeader(parsing: Data("MSRB".utf8)) }
    }

    @Test func unsupportedFormatVersion() throws {
        let data = try resigned(try signed(2026100201)) { $0[4] = 2 }
        #expect(throws: RuleError.unsupportedFormatVersion(2)) { try RuleBundleVerifier(publicKeyBase64: pub).verify(data) }
    }

    @Test func payloadLengthMismatch() throws {
        let data = try resigned(try signed(2026100201)) { $0[18] &+= 1 }
        #expect(throws: RuleError.payloadLengthMismatch) { try RuleBundleVerifier(publicKeyBase64: pub).verify(data) }
    }

    @Test func corruptedPayloadFailsDecompression() throws {
        let data = try resigned(try signed(2026100201)) { d in
            for i in RuleBundleHeader.size..<d.count { d[i] = 0x5A }
        }
        #expect(throws: RuleError.self) { try RuleBundleVerifier(publicKeyBase64: pub).verify(data) }
    }

    // MARK: RuleStore

    func store(bundled: Data?, cache: URL, appVersion: String = "1.0.0") throws -> RuleStore {
        var bundledURL: URL?
        if let bundled {
            let u = cache.deletingLastPathComponent().appendingPathComponent("bundled-\(UUID().uuidString).bundle")
            try bundled.write(to: u)
            bundledURL = u
        }
        return try RuleStore(bundled: bundledURL, cache: cache, publicKeyBase64: pub, appVersion: OSVersion(appVersion)!)
    }

    @Test func storeLoadsBundledAndRefusesDowngrade() throws {
        let dir = try TemporaryDirectory("store")
        let cache = dir.url("cache")
        let s = try store(bundled: try signed(2026100100), cache: cache)
        #expect(s.snapshot.version == 2026100100)
        #expect(s.snapshot.source == .bundled)

        #expect(throws: RuleError.downgrade(current: 2026100100, offered: 2026090100)) { try s.install(try signed(2026090100), expectedSHA256: nil) }
        #expect(throws: RuleError.downgrade(current: 2026100100, offered: 2026100100)) { try s.install(try signed(2026100100), expectedSHA256: nil) }
        #expect(s.snapshot.version == 2026100100)
    }

    @Test func storeInstallsNewerVerifiedBundle() throws {
        let dir = try TemporaryDirectory("store")
        let cache = dir.url("cache")
        let s = try store(bundled: try signed(2026100100), cache: cache)
        let newer = try signed(2026100201)
        let sha = SHA256.hash(data: newer).map { String(format: "%02x", $0) }.joined()
        let snap = try s.install(newer, expectedSHA256: sha.uppercased(), disabledRules: ["t.cache"])
        #expect(snap.version == 2026100201)
        #expect(snap.source == .downloaded)
        #expect(snap.disabled == [RuleID("t.cache")])
        #expect(s.snapshot.version == 2026100201)
        #expect(FileManager.default.fileExists(atPath: cache.appendingPathComponent("rules-2026100201.bundle").path))

        // Khởi động lại: lấy bộ đã tải về vì mới hơn bộ đi kèm.
        let reloaded = try store(bundled: try signed(2026100100), cache: cache)
        #expect(reloaded.snapshot.version == 2026100201)
        #expect(reloaded.snapshot.source == .downloaded)
    }

    @Test func storeRejectsChecksumSignatureAndAppVersion() throws {
        let dir = try TemporaryDirectory("store")
        let s = try store(bundled: try signed(2026100100), cache: dir.url("cache"))
        #expect(throws: RuleError.checksumMismatch) { try s.install(try signed(2026100201), expectedSHA256: String(repeating: "0", count: 64)) }
        #expect(throws: RuleError.badSignature) { try s.install(try signed(2026100201, key: Curve25519.Signing.PrivateKey()), expectedSHA256: nil) }
        #expect(throws: RuleError.appTooOld(required: "9.0.0")) { try s.install(try signed(2026100201, minApp: "9.0.0"), expectedSHA256: nil) }
        #expect(s.snapshot.version == 2026100100)
        #expect(!FileManager.default.fileExists(atPath: dir.path("cache/rules-2026100201.bundle")))
    }

    @Test func storeIgnoresTamperedCacheAndKeepsTwoVersions() throws {
        let dir = try TemporaryDirectory("store")
        let cache = dir.url("cache")
        let s = try store(bundled: try signed(2026100100), cache: cache)
        try s.install(try signed(2026100101), expectedSHA256: nil)
        try s.install(try signed(2026100102), expectedSHA256: nil)
        try s.install(try signed(2026100103), expectedSHA256: nil)
        let files = try FileManager.default.contentsOfDirectory(atPath: cache.path).sorted()
        #expect(files == ["rules-2026100102.bundle", "rules-2026100103.bundle"])

        // Sửa bộ mới nhất trong cache: bị bỏ, dùng bộ hợp lệ kế tiếp.
        let newest = cache.appendingPathComponent("rules-2026100103.bundle")
        var bytes = try Data(contentsOf: newest)
        bytes[30] ^= 0xFF
        try bytes.write(to: newest)
        let reloaded = try store(bundled: try signed(2026100100), cache: cache)
        #expect(reloaded.snapshot.version == 2026100102)
    }

    @Test func storeWithoutValidBundleIsEmpty() throws {
        let dir = try TemporaryDirectory("store")
        let s = try store(bundled: Data("garbage".utf8), cache: dir.url("cache"))
        #expect(s.snapshot.version == 0)
        #expect(s.snapshot.source == .empty)
    }

    @Test func killSwitchDisablesRules() throws {
        let dir = try TemporaryDirectory("store")
        let s = try store(bundled: try signed(2026100100), cache: dir.url("cache"))
        s.applyDisabledRules(["t.cache"])
        #expect(s.snapshot.activeRules.isEmpty)
        #expect(s.snapshot.rules(in: RuleCategory.userCaches).isEmpty)
    }

    @Test func manifestDecodes() throws {
        let json = #"{"latest":2026100201,"minApp":"1.2","url":"rules-2026100201.bundle","sha256":"ab","disabledRules":["x.y"]}"#
        let m = try JSONDecoder().decode(RuleManifest.self, from: Data(json.utf8))
        #expect(m.latest == 2026100201)
        #expect(m.disabledRules == ["x.y"])
    }
}

/// Bộ rule thật trong `Rules/` và bundle đi kèm app.
@Suite struct ShippedRulesTests {
    func load() throws -> RulePayload { try RuleSourceLoader.load(directory: RepoPaths.rules) }

    @Test func loadsAndIdsAreUnique() throws {
        let payload = try load()
        #expect(payload.rules.count >= 100)
        #expect(payload.version >= 2026100201)
        let ids = payload.rules.map(\.id.rawValue)
        #expect(Set(ids).count == ids.count)
        for r in payload.rules {
            #expect(r.title?.values["en"]?.isEmpty == false && r.title?.values["vi"]?.isEmpty == false, "\(r.id)")
            #expect(r.reason?.values["en"]?.isEmpty == false && r.reason?.values["vi"]?.isEmpty == false, "\(r.id)")
            #expect(RuleCategory.all.contains(r.category), "\(r.id)")
        }
    }

    @Test func everyCategoryOfTable84IsCovered() throws {
        let categories = Set(try load().rules.map(\.category))
        for c in RuleCategory.all { #expect(categories.contains(c), "Thiếu rule cho \(c)") }
        // Mọi nhóm System Junk đều có rule tương ứng.
        for spec in SystemJunkTasks.specs { #expect(categories.contains(spec.category), "\(spec.category)") }
    }

    @Test func providersExistInSystemJunk() throws {
        let known = Set(SystemJunkTasks.providers.map(\.name))
        for r in try load().rules {
            if let p = r.match.provider { #expect(known.contains(p), "Provider \(p) của \(r.id) không có trong SystemJunkTasks") }
        }
    }

    @Test func noShippedPathTouchesForbiddenZones() throws {
        let home = "/Users/rules-test"
        let policy = PathPolicy.user(home: URL(fileURLWithPath: home))
        let app = RuleAppInfo(bundleID: "com.example.test", teamID: "ABCDE12345", appName: "Test", url: nil)
        for r in try load().rules {
            for p in r.match.paths {
                let s = try #require(RuleEvaluator.substitute(p, app: app))
                let glob = Glob(PathResolver(home: home).resolve(s), home: home)
                let sample = "/" + glob.segments.map { seg -> String in
                    switch seg {
                    case let .literal(x): x
                    case let .wildcard(w): w.replacingOccurrences(of: "*", with: "sample").replacingOccurrences(of: "?", with: "x")
                    case .globstar: "sample"
                    }
                }.joined(separator: "/")
                #expect(policy.isForbidden(sample) == nil, "\(r.id): \(p)")
                #expect(PathPolicy.root.isForbidden(sample) == nil, "\(r.id): \(p)")
                #expect(!glob.couldMatchDescendant(of: home + "/Library/Keychains"), "\(r.id): \(p)")
                #expect(!glob.couldMatchDescendant(of: home + "/Documents"), "\(r.id): \(p)")
            }
        }
    }

    @Test func bundledRulesVerifyWithEmbeddedKey() throws {
        let url = RepoPaths.bundledRules
        try #require(FileManager.default.fileExists(atPath: url.path), "Chạy Scripts/build-rules.sh để tạo rules.bundle")
        let bundle = try RuleBundleVerifier(publicKeyBase64: RulesPublicKey.base64).verify(try Data(contentsOf: url))
        let source = try load()
        #expect(bundle.version == source.version)
        #expect(Set(bundle.payload.rules.map(\.id)) == Set(source.rules.map(\.id)))
    }
}

/// `RuleSourceLoader` đọc thư mục JSON (fixture trong `Tests/MashCleanTests/Fixtures`).
@Suite struct RuleSourceLoaderTests {
    func fixture(_ name: String) throws -> URL {
        let root = try #require(Bundle.module.url(forResource: "Fixtures", withExtension: nil))
        return root.appendingPathComponent(name, isDirectory: true)
    }

    @Test func loadsSingleArrayKnowledgeAndVersion() throws {
        let payload = try RuleSourceLoader.load(directory: try fixture("RuleSource"))
        #expect(payload.version == 2026100105)
        #expect(Set(payload.rules.map(\.id.rawValue)) == ["fixture.single", "fixture.sim", "fixture.app"])
        #expect(payload.knowledge.isWhitelistedOrphan("com.apple.Safari"))
        #expect(payload.knowledge.isWhitelistedOrphan("CloudKit"))
        #expect(!payload.knowledge.isWhitelistedOrphan("com.vendor.app"))
        #expect(payload.knowledge.isApple("com.apple.dt.Xcode"))
        let single = try #require(payload.rules.first { $0.id == "fixture.single" })
        #expect(single.version == 2)
        #expect(single.reason?.values["en"] == "Downloaded packages")
        #expect(single.conditions?.minOS == "13.0")
        let sim = try #require(payload.rules.first { $0.id == "fixture.sim" })
        #expect(sim.removal == .custom(.simulator))
        #expect(sim.match.provider == "simctl.unavailableDevices")
        #expect(payload.rules.first { $0.id == "fixture.app" }?.usesAppVariables == true)
    }

    @Test func versionOverride() throws {
        #expect(try RuleSourceLoader.load(directory: try fixture("RuleSource"), version: 2026123199).version == 2026123199)
    }

    @Test func invalidSafetyIsRejected() throws {
        #expect(throws: RuleError.self) { try RuleSourceLoader.load(directory: try fixture("BadRuleSource")) }
    }

    @Test func ruleJSONRoundTrip() throws {
        let payload = try RuleSourceLoader.load(directory: try fixture("RuleSource"))
        let data = try RuleBundleBuilder.encodePayload(payload)
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        let back = try decoder.decode(RulePayload.self, from: data)
        #expect(back.rules == payload.rules)
        #expect(back.knowledge == payload.knowledge)
    }
}
