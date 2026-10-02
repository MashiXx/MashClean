import FileSystemKit
import Foundation
import Testing
import RuleEngine
import SweepCore

/// Mở rộng biến, chống path traversal và đánh giá rule trên thư mục tạm (mục 8.6, 18).
@Suite struct RuleSubstitutionTests {
    let app = RuleAppInfo(bundleID: "com.example.app", teamID: "ABCDE12345", appName: "Example", url: nil)

    @Test func substitutesAllVariables() {
        #expect(RuleEvaluator.substitute("~/Library/Caches/${bundleID}", app: app) == "~/Library/Caches/com.example.app")
        #expect(RuleEvaluator.substitute("~/Library/Group Containers/${teamID}.${bundleID}", app: app)
            == "~/Library/Group Containers/ABCDE12345.com.example.app")
        #expect(RuleEvaluator.substitute("~/Library/Application Support/${appName}", app: app) == "~/Library/Application Support/Example")
        #expect(RuleEvaluator.substitute("~/Library/Logs/plain", app: app) == "~/Library/Logs/plain")
    }

    @Test(arguments: ["../../..", "..", ".", "com.x/../../etc", "a/b", "*", "com.*", "x?", "[ab]", "", "bad\0id"])
    func rejectsTraversalAndGlobInjection(_ evil: String) {
        let bad = RuleAppInfo(bundleID: evil, teamID: nil, appName: "X", url: nil)
        #expect(RuleEvaluator.substitute("~/Library/Caches/${bundleID}", app: bad) == nil)
        #expect(!RuleEvaluator.isSafeComponent(evil))
    }

    @Test func missingTeamIDDropsPath() {
        let noTeam = RuleAppInfo(bundleID: "com.example.app", teamID: nil, appName: "X", url: nil)
        #expect(RuleEvaluator.substitute("~/Library/Group Containers/${teamID}.x", app: noTeam) == nil)
        #expect(RuleEvaluator.substitute("~/Library/Caches/${bundleID}", app: noTeam) != nil)
    }

    @Test func resolverRewritesRoots() {
        let r = PathResolver(home: "/tmp/h", rootPrefix: "/tmp/r")
        #expect(r.resolve("~/Library/Caches/x") == "/tmp/h/Library/Caches/x")
        #expect(r.resolve("${userHome}/.npm") == "/tmp/h/.npm")
        #expect(r.resolve("/Library/Caches/x") == "/tmp/r/Library/Caches/x")
        #expect(r.resolve("~") == "/tmp/h")
    }
}

@Suite struct RuleEvaluatorTests {
    struct Env {
        let dir: TemporaryDirectory
        let home: String
        let root: String

        init() throws {
            dir = try TemporaryDirectory("rules")
            home = dir.path("home")
            root = dir.path("root")
            try dir.dir("home")
            try dir.dir("root")
        }

        func snapshot(_ rules: [Rule]) -> RuleSnapshot {
            RuleSnapshot(version: 1, rules: rules, knowledge: Knowledge(), resolver: PathResolver(home: home, rootPrefix: root), source: .development)
        }

        func context(apps: [RuleAppInfo] = [], running: Set<String> = [], ignore: IgnoreList = .empty, os: OSVersion = .current) -> RuleEvaluationContext {
            RuleEvaluationContext(fileSystem: FileSystemService(home: URL(fileURLWithPath: home)), policy: .user(home: URL(fileURLWithPath: home)),
                                  apps: apps, runningBundleIDs: running, ignore: ignore, osVersion: os)
        }

        func evaluate(_ rule: Rule, apps: [RuleAppInfo] = [], running: Set<String> = [], ignore: IgnoreList = .empty,
                      os: OSVersion = .current, snapshot: RuleSnapshot? = nil) throws -> [String] {
            let snap = snapshot ?? self.snapshot([rule])
            let compiled = try #require(snap.rule(rule.id))
            return try RuleEvaluator.evaluate(compiled, snapshot: snap, context: context(apps: apps, running: running, ignore: ignore, os: os))
                .map { $0.url.path.replacingOccurrences(of: home, with: "~").replacingOccurrences(of: root, with: "") }
                .sorted()
        }
    }

    @Test func minAgeDays() throws {
        let env = try Env()
        try env.dir.file("home/Downloads/old.dmg", ageDays: 45)
        try env.dir.file("home/Downloads/new.dmg", ageDays: 2)
        let rule = makeRule("t.age", category: RuleCategory.oldDownloads, paths: ["~/Downloads/*.dmg"], removal: .moveToTrash, minAgeDays: 30)
        #expect(try env.evaluate(rule) == ["~/Downloads/old.dmg"])
    }

    @Test func minAgeUsesNewestFileInsideDirectory() throws {
        let env = try Env()
        try env.dir.file("home/Library/Caches/a/old", ageDays: 60)
        try env.dir.file("home/Library/Caches/b/old", ageDays: 60)
        try env.dir.file("home/Library/Caches/b/fresh", ageDays: 1)
        env.dir.setAge(env.dir.path("home/Library/Caches/a"), days: 60)
        env.dir.setAge(env.dir.path("home/Library/Caches/b"), days: 60)
        let rule = makeRule("t.age.dir", paths: ["~/Library/Caches/*"], minAgeDays: 30)
        #expect(try env.evaluate(rule) == ["~/Library/Caches/a"])
    }

    @Test func minSizeBytes() throws {
        let env = try Env()
        try env.dir.file("home/Library/Caches/big/blob", size: 256 * 1024)
        try env.dir.file("home/Library/Caches/small/blob", size: 10)
        let rule = makeRule("t.size", paths: ["~/Library/Caches/*"], minSizeBytes: 100 * 1024)
        #expect(try env.evaluate(rule) == ["~/Library/Caches/big"])
    }

    @Test func excludeAlwaysWins() throws {
        let env = try Env()
        try env.dir.file("home/Library/Caches/com.apple.Safari/x")
        try env.dir.file("home/Library/Caches/CloudKit/x")
        try env.dir.file("home/Library/Caches/com.vendor.app/x")
        let rule = makeRule("t.exclude", paths: ["~/Library/Caches/*"], removal: .deleteContents,
                            exclude: ["~/Library/Caches/com.apple.*", "~/Library/Caches/CloudKit"])
        #expect(try env.evaluate(rule) == ["~/Library/Caches/com.vendor.app"])
    }

    @Test func excludeInsideMatchIsNotMeasured() throws {
        let env = try Env()
        try env.dir.file("home/Library/Caches/app/keep/big", size: 512 * 1024)
        try env.dir.file("home/Library/Caches/app/drop", size: 4096)
        let rule = makeRule("t.exclude.inner", paths: ["~/Library/Caches/app"], removal: .deleteContents,
                            exclude: ["~/Library/Caches/app/keep"])
        let snap = env.snapshot([rule])
        let compiled = try #require(snap.rule(rule.id))
        let m = try #require(try RuleEvaluator.evaluate(compiled, snapshot: snap, context: env.context()).first)
        #expect(m.measurement.allocatedSize < 512 * 1024)
    }

    @Test func emptyDirectoryIsSkippedUnlessDelete() throws {
        let env = try Env()
        try env.dir.dir("home/Library/Caches/empty")
        #expect(try env.evaluate(makeRule("t.empty.contents", paths: ["~/Library/Caches/*"], removal: .deleteContents)).isEmpty)
        #expect(try env.evaluate(makeRule("t.empty.delete", paths: ["~/Library/Caches/*"], removal: .delete)) == ["~/Library/Caches/empty"])
    }

    @Test func appNotRunningStaticRule() throws {
        let env = try Env()
        try env.dir.file("home/Library/Developer/Xcode/DerivedData/App-1/x")
        let rule = makeRule("t.running", category: RuleCategory.xcodeJunk, paths: ["~/Library/Developer/Xcode/DerivedData/*"],
                            appNotRunning: ["com.apple.dt.Xcode"])
        #expect(try env.evaluate(rule).count == 1)
        #expect(try env.evaluate(rule, running: ["com.apple.dt.Xcode"]).isEmpty)
    }

    @Test func forEachInstalledAppWithAppNotRunning() throws {
        let env = try Env()
        try env.dir.file("home/Library/Caches/com.a/x")
        try env.dir.file("home/Library/Caches/com.b/x")
        try env.dir.file("home/Library/Containers/com.a/Data/Library/Caches/y")
        try env.dir.file("home/Library/Caches/com.orphan/x")
        let rule = makeRule("t.apps", paths: ["~/Library/Caches/${bundleID}", "~/Library/Containers/${bundleID}/Data/Library/Caches"],
                            removal: .deleteContents, forEachApp: true, appNotRunning: ["${bundleID}"])
        let apps = ["com.a", "com.b"].map { RuleAppInfo(bundleID: $0, teamID: nil, appName: $0, url: nil) }
        #expect(try env.evaluate(rule, apps: apps) == ["~/Library/Caches/com.a", "~/Library/Caches/com.b", "~/Library/Containers/com.a/Data/Library/Caches"])
        #expect(try env.evaluate(rule, apps: apps, running: ["com.a"]) == ["~/Library/Caches/com.b"])
        // Bỏ qua app trong ignore list theo bundle ID.
        #expect(try env.evaluate(rule, apps: apps, ignore: IgnoreList(bundleIDs: ["com.b"])).count == 2)
    }

    @Test func bundleIDFilterUsesGlob() throws {
        let env = try Env()
        try env.dir.file("home/Library/Application Support/Battle.net/x")
        let rule = makeRule("app.net.battle.leftovers", category: RuleCategory.appLeftovers,
                            paths: ["~/Library/Application Support/Battle.net"], bundleIDs: ["net.battle.*"])
        #expect(try env.evaluate(rule, apps: [RuleAppInfo(bundleID: "net.battle.app", teamID: nil, appName: "B", url: nil)]).count == 1)
        #expect(try env.evaluate(rule, apps: [RuleAppInfo(bundleID: "com.other", teamID: nil, appName: "O", url: nil)]).isEmpty)
        #expect(try env.evaluate(rule).isEmpty)
    }

    @Test func maliciousBundleIDCannotTraverse() throws {
        let env = try Env()
        try env.dir.file("home/secret.txt")
        try env.dir.file("home/Library/Caches/x/y")
        let rule = makeRule("t.evil", paths: ["~/Library/Caches/${bundleID}"], forEachApp: true)
        let evil = ["../..", "../../secret.txt", "*", "x/../.."].map { RuleAppInfo(bundleID: $0, teamID: nil, appName: "E", url: nil) }
        #expect(try env.evaluate(rule, apps: evil).isEmpty)
    }

    @Test func symlinkIntoForbiddenZoneIsDropped() throws {
        let env = try Env()
        try env.dir.symlink("home/Library/Caches/evil", to: "/System")
        try env.dir.file("home/Library/Caches/good/Library/x")
        let rule = makeRule("t.symlink", paths: ["~/Library/Caches/*/Library"])
        // `evil/Library` sau chuẩn hoá là /System/Library: bị PathPolicy loại (mục 8.6 bước 4).
        #expect(try env.evaluate(rule) == ["~/Library/Caches/good/Library"])
    }

    @Test func ignoreListAndDisabledRules() throws {
        let env = try Env()
        try env.dir.file("home/Library/Logs/a/x")
        try env.dir.file("home/Library/Logs/b/x")
        let rule = makeRule("t.ignore", category: RuleCategory.userLogs, paths: ["~/Library/Logs/*"])
        #expect(try env.evaluate(rule, ignore: IgnoreList(paths: [env.dir.path("home/Library/Logs/a")])) == ["~/Library/Logs/b"])
        #expect(try env.evaluate(rule, ignore: IgnoreList(rules: ["t.ignore"])).isEmpty)
        let disabled = env.snapshot([rule]).withDisabled([RuleID("t.ignore")])
        #expect(try env.evaluate(rule, snapshot: disabled).isEmpty)
        #expect(disabled.rules(in: RuleCategory.userLogs).isEmpty)
    }

    @Test func minOSCondition() throws {
        let env = try Env()
        try env.dir.file("home/Library/Logs/a/x")
        let rule = makeRule("t.os", category: RuleCategory.userLogs, paths: ["~/Library/Logs/*"], minOS: "14.0")
        #expect(try env.evaluate(rule, os: OSVersion(major: 13)).isEmpty)
        #expect(try env.evaluate(rule, os: OSVersion(major: 15)).count == 1)
    }

    @Test func providerRulesAreLeftToProviders() throws {
        let env = try Env()
        let rule = makeRule("t.provider", category: RuleCategory.trash, paths: [], provider: "trash")
        #expect(try env.evaluate(rule).isEmpty)
    }

    @Test func systemPathsUseRootPrefixAndAllowedRoot() throws {
        let env = try Env()
        try env.dir.file("root/Library/Caches/com.vendor/x")
        let rule = makeRule("t.root", category: RuleCategory.systemCaches, paths: ["/Library/Caches/*"], requiresRoot: true)
        let snap = env.snapshot([rule])
        let compiled = try #require(snap.rule(rule.id))
        let m = try #require(try RuleEvaluator.evaluate(compiled, snapshot: snap, context: env.context()).first)
        #expect(m.url.path == env.root + "/Library/Caches/com.vendor")
        #expect(m.allowedRoot == env.root + "/Library/Caches")
        #expect(m.measurement.itemCount == 1)
    }

    @Test func compiledRuleKnowsWhenItIsPerApp() {
        let resolver = PathResolver(home: "/Users/t")
        #expect(CompiledRule(makeRule("a.b", paths: ["~/x"]), resolver: resolver).staticGlobs != nil)
        #expect(CompiledRule(makeRule("a.c", paths: ["~/x/${bundleID}"], forEachApp: true), resolver: resolver).staticGlobs == nil)
    }
}
