import CryptoKit
import Foundation
import RuleEngine
import SweepCore

// rulepack: lint → test → build → sign → verify bộ rule (mục 8.5, 23.8).

let usage = """
Cách dùng: rulepack <lệnh> [tham số]

  lint <dir>                                     Kiểm tra schema, id, glob, biến, root, provider, ngôn ngữ, vùng cấm
  test <dir> --fixtures <dir>                    Chạy rule trên cây thư mục giả lập, so với kỳ vọng
  build <dir> --out <file> --min-app <x.y.z> [--version N]
                                                 Gộp → JSON → LZFSE (chưa ký); tự chạy lint trước
  sign <unsigned> --key <file-base64> --out <file>
                                                 Ký Ed25519, nối chữ ký 64 byte vào cuối
  verify <bundle> [--pubkey <base64>]            Kiểm tra chữ ký và giải nén (mặc định khoá nhúng trong app)
  keygen --out <file> [--force]                  Tạo khoá ký mới, in public key
  manifest <bundle> --url <url> --min-app <x.y> [--disabled id1,id2] [--pubkey <base64>]
                                                 In manifest JSON cho CDN (mục 14.2)
"""

func runLint(_ args: Arguments) throws -> Int32 {
    let dir = absoluteURL(try args.positional(0, "thư mục rule"), isDirectory: true)
    let source = try RuleSource(directory: dir)
    let issues = RuleLinter().lint(source)
    for i in issues { Console.err(i.description) }
    let errors = issues.filter { $0.severity == .error }.count
    let warnings = issues.count - errors
    Console.out("lint: \(source.rules.count) rule, \(errors) lỗi, \(warnings) cảnh báo")
    return errors == 0 ? 0 : 1
}

func runTest(_ args: Arguments) throws -> Int32 {
    let dir = absoluteURL(try args.positional(0, "thư mục rule"), isDirectory: true)
    let fixturesDir = absoluteURL(try args.required("fixtures"), isDirectory: true)
    let payload: RulePayload
    do { payload = try RuleSourceLoader.load(directory: dir) } catch {
        throw CLIError("Không nạp được rule: \(error)")
    }
    let files = RuleSource.jsonFiles(in: fixturesDir)
    guard !files.isEmpty else { throw CLIError("Không có fixture nào trong \(fixturesDir.path)") }
    let runner = FixtureRunner(payload: payload)
    var results: [FixtureResult] = []
    var covered = Set<String>()
    for url in files {
        let rel = RuleSource.relative(url, to: fixturesDir)
        for c in try FixtureCase.load(url) {
            covered.formUnion(c.rules)
            let r = try runner.run(c, file: rel)
            results.append(r)
            if r.passed {
                Console.out("  ✓ \(rel): \(c.name)")
            } else {
                Console.err("  ✗ \(rel): \(c.name)")
                for e in r.errors { Console.err("      lỗi: \(e)") }
                for m in r.missing { Console.err("      thiếu: \(m)") }
                for u in r.unexpected { Console.err("      thừa:  \(u)") }
            }
        }
    }
    let untested = payload.rules.filter { $0.match.provider == nil && !covered.contains($0.id.rawValue) }.map(\.id.rawValue)
    if !untested.isEmpty { Console.err("cảnh báo: \(untested.count) rule chưa có fixture: \(untested.joined(separator: ", "))") }
    let failed = results.filter { !$0.passed }.count
    Console.out("test: \(results.count) case, \(results.count - failed) đạt, \(failed) lỗi; \(covered.count) rule có fixture")
    return failed == 0 ? 0 : 1
}

func parseMinApp(_ s: String) throws -> OSVersion {
    guard let v = OSVersion(s), v.minor < 100, v.patch < 100 else { throw CLIError.usage("--min-app không hợp lệ: \(s)") }
    return v
}

func runBuild(_ args: Arguments) throws -> Int32 {
    let dir = absoluteURL(try args.positional(0, "thư mục rule"), isDirectory: true)
    let out = try args.required("out")
    let minApp = try parseMinApp(try args.required("min-app"))
    var version: UInt64?
    if let v = args.options["version"] {
        guard let n = UInt64(v), RuleSource.isValidRulesVersion(n) else { throw CLIError.usage("--version phải dạng yyyymmddNN") }
        version = n
    }
    // Không đóng gói bộ rule chưa qua lint.
    let source = try RuleSource(directory: dir)
    let errors = RuleLinter().lint(source).filter { $0.severity == .error }
    guard errors.isEmpty else {
        for e in errors { Console.err(e.description) }
        throw CLIError("Lint có \(errors.count) lỗi, không build")
    }
    var payload: RulePayload
    do { payload = try RuleSourceLoader.load(directory: dir, version: version) } catch {
        throw CLIError("Không nạp được rule: \(error)")
    }
    guard payload.version > 0 else { throw CLIError("Thiếu phiên bản rule (version.txt hoặc --version)") }
    payload.generatedAt = Date()
    let data = try RuleBundleBuilder.pack(payload: payload, minAppVersion: minApp)
    try writeFile(data, to: out)
    Console.out("build: \(payload.rules.count) rule, phiên bản \(payload.version), app tối thiểu \(minApp), \(data.count) byte → \(out)")
    return 0
}

func loadPrivateKey(_ path: String) throws -> Curve25519.Signing.PrivateKey {
    let text = String(decoding: try readFile(path), as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
    guard let raw = Data(base64Encoded: text) else { throw CLIError("File khoá không phải base64: \(path)") }
    do { return try Curve25519.Signing.PrivateKey(rawRepresentation: raw) } catch {
        throw CLIError("Khoá riêng Ed25519 không hợp lệ (cần 32 byte): \(path)")
    }
}

func runSign(_ args: Arguments) throws -> Int32 {
    let input = try args.positional(0, "file chưa ký")
    let keyPath = try args.required("key")
    let out = try args.required("out")
    let unsigned = try readFile(input)
    let header: RuleBundleHeader
    do { header = try RuleBundleHeader(parsing: unsigned) } catch { throw CLIError("\(input) không phải bundle chưa ký: \(error)") }
    guard unsigned.count == RuleBundleHeader.size + Int(header.payloadLength) else {
        throw CLIError("\(input): độ dài không khớp header (đã ký rồi?)")
    }
    let key = try loadPrivateKey(keyPath)
    let signed = try RuleBundleBuilder.sign(unsigned, privateKey: key)
    let pub = key.publicKey.rawRepresentation.base64EncodedString()
    // Tự kiểm tra lại bằng chính public key của khoá vừa ký.
    _ = try RuleBundleVerifier(publicKey: key.publicKey).verify(signed)
    try writeFile(signed, to: out)
    Console.out("sign: phiên bản \(header.rulesVersion), \(signed.count) byte → \(out)")
    Console.out("public key: \(pub)")
    if pub != RulesPublicKey.base64 {
        Console.err("cảnh báo: public key khác khoá nhúng trong app (RulesPublicKey.base64); app sẽ từ chối bundle này")
    }
    return 0
}

func runVerify(_ args: Arguments) throws -> Int32 {
    let input = try args.positional(0, "bundle")
    let pub = args.options["pubkey"] ?? RulesPublicKey.base64
    let data = try readFile(input)
    let verifier: RuleBundleVerifier
    do { verifier = try RuleBundleVerifier(publicKeyBase64: pub) } catch { throw CLIError.usage("Public key không hợp lệ") }
    let bundle: RuleBundle
    do { bundle = try verifier.verify(data) } catch {
        throw CLIError("verify thất bại: \(error)")
    }
    var byCategory: [String: Int] = [:]
    for r in bundle.payload.rules { byCategory[r.category, default: 0] += 1 }
    Console.out("verify: OK — phiên bản \(bundle.version), app tối thiểu \(OSVersion(packed: bundle.header.minAppVersion)), \(bundle.payload.rules.count) rule")
    for (c, n) in byCategory.sorted(by: { $0.key < $1.key }) { Console.out("  \(c): \(n)") }
    return 0
}

func runKeygen(_ args: Arguments) throws -> Int32 {
    let out = try args.required("out")
    if FileManager.default.fileExists(atPath: absoluteURL(out).path) && !args.flags.contains("force") {
        throw CLIError("\(out) đã tồn tại; dùng --force để ghi đè")
    }
    let key = Curve25519.Signing.PrivateKey()
    try writeFile(Data(key.rawRepresentation.base64EncodedString().utf8), to: out, permissions: 0o600)
    Console.out("keygen: đã ghi khoá riêng vào \(out) (quyền 600, không commit)")
    Console.out("public key: \(key.publicKey.rawRepresentation.base64EncodedString())")
    return 0
}

func runManifest(_ args: Arguments) throws -> Int32 {
    let input = try args.positional(0, "bundle")
    let url = try args.required("url")
    let minApp = try args.required("min-app")
    guard OSVersion(minApp) != nil else { throw CLIError.usage("--min-app không hợp lệ: \(minApp)") }
    let data = try readFile(input)
    // Không phát hành manifest cho bundle chưa ký hoặc ký sai.
    let bundle: RuleBundle
    do { bundle = try RuleBundleVerifier(publicKeyBase64: args.options["pubkey"] ?? RulesPublicKey.base64).verify(data) } catch {
        throw CLIError("Bundle không qua verify, không tạo manifest: \(error)")
    }
    let disabled = args.options["disabled"].map { $0.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty } }
    let sha = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    let manifest = RuleManifest(latest: bundle.version, minApp: minApp, url: url, sha256: sha, disabledRules: disabled ?? [])
    let encoder = JSONEncoder()
    encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
    Console.out(String(decoding: try encoder.encode(manifest), as: UTF8.self))
    return 0
}

let argv = Array(CommandLine.arguments.dropFirst())
guard let command = argv.first else {
    Console.err(usage)
    exit(2)
}

do {
    let args = try Arguments(Array(argv.dropFirst()), flags: ["force"])
    let code: Int32
    switch command {
    case "lint": code = try runLint(args)
    case "test": code = try runTest(args)
    case "build": code = try runBuild(args)
    case "sign": code = try runSign(args)
    case "verify": code = try runVerify(args)
    case "keygen": code = try runKeygen(args)
    case "manifest": code = try runManifest(args)
    case "help", "-h", "--help":
        Console.out(usage)
        code = 0
    default:
        Console.err("Lệnh không biết: \(command)\n\n\(usage)")
        code = 2
    }
    exit(code)
} catch let e as CLIError {
    Console.err("lỗi: \(e.message)")
    if e.code == 2 { Console.err("\n\(usage)") }
    exit(e.code)
} catch {
    Console.err("lỗi: \(error)")
    exit(1)
}
