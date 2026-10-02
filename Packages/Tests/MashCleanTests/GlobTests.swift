import Foundation
import Testing
import SweepCore

/// Glob của rule (mục 8.3, 8.6).
@Suite struct GlobTests {
    @Test func star() {
        let g = Glob("/a/*/c")
        #expect(g.matches("/a/b/c"))
        #expect(g.matches("/a/xyz/c"))
        #expect(!g.matches("/a/b/d/c"))
        #expect(!g.matches("/a/c"))
        #expect(Glob("/x/*.dmg").matches("/x/Installer.dmg"))
        #expect(!Glob("/x/*.dmg").matches("/x/Installer.dmg.part"))
    }

    @Test func globstar() {
        let g = Glob("/a/**/c")
        #expect(g.matches("/a/c"))
        #expect(g.matches("/a/b/c"))
        #expect(g.matches("/a/b/d/e/c"))
        #expect(!g.matches("/a/b/d"))
        #expect(Glob("**/*.photoslibrary").matches("/Users/x/Pictures/Photos Library.photoslibrary"))
    }

    @Test func questionMarkAndBrackets() {
        #expect(Glob("/a/file?.txt").matches("/a/file1.txt"))
        #expect(!Glob("/a/file?.txt").matches("/a/file10.txt"))
        #expect(Glob("/a/[ab].log").matches("/a/b.log"))
        #expect(!Glob("/a/[ab].log").matches("/a/c.log"))
    }

    @Test func tildeAndUserHome() {
        let g = Glob("~/Library/Caches/*", home: "/Users/test")
        #expect(g.pattern == "/Users/test/Library/Caches/*")
        #expect(g.matches("/Users/test/Library/Caches/com.example"))
        #expect(Glob("${userHome}/.npm", home: "/Users/test").pattern == "/Users/test/.npm")
        #expect(Glob("~", home: "/Users/test").pattern == "/Users/test")
    }

    @Test func literalPrefix() {
        #expect(Glob("/Users/x/Library/Caches/*").literalPrefix == "/Users/x/Library/Caches")
        #expect(Glob("/Users/x/Library/Caches/*/Data").literalPrefix == "/Users/x/Library/Caches")
        #expect(Glob("/a/b").literalPrefix == "/a/b")
        #expect(Glob("/*").literalPrefix == "/")
        #expect(Glob("/a/**/c").literalPrefix == "/a")
        #expect(Glob("/a/b").isLiteral)
        #expect(!Glob("/a/*").isLiteral)
    }

    @Test func matchesSelfOrAncestor() {
        let g = Glob("/a/b")
        #expect(g.matchesSelfOrAncestor("/a/b"))
        #expect(g.matchesSelfOrAncestor("/a/b/c/d"))
        #expect(!g.matchesSelfOrAncestor("/a/bc"))
        #expect(!g.matchesSelfOrAncestor("/a"))
        #expect(Glob("/x/com.apple.*").matchesSelfOrAncestor("/x/com.apple.Safari/Cache.db"))
    }

    @Test func couldMatchDescendant() {
        let g = Glob("/Users/x/Library/*/Caches")
        #expect(g.couldMatchDescendant(of: "/Users/x/Library"))
        #expect(g.couldMatchDescendant(of: "/Users/x/Library/Keychains"))
        #expect(!g.couldMatchDescendant(of: "/Users/y"))
        #expect(!g.couldMatchDescendant(of: "/Users/x/Library/Keychains/Caches/deeper"))
    }

    @Test func globSet() {
        let set = GlobSet(["~/Library/Caches/com.apple.*", "~/Library/Caches/CloudKit"], home: "/Users/test")
        #expect(set.matches("/Users/test/Library/Caches/CloudKit"))
        #expect(set.matchesSelfOrAncestor("/Users/test/Library/Caches/com.apple.akd/db"))
        #expect(!set.matches("/Users/test/Library/Caches/com.spotify.client"))
        #expect(GlobSet([]).isEmpty)
    }

    @Test func hasMagic() {
        #expect(Glob.hasMagic("a*"))
        #expect(Glob.hasMagic("a?"))
        #expect(Glob.hasMagic("[a]"))
        #expect(!Glob.hasMagic("com.example.app"))
    }
}
