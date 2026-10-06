using CleanBoost.Core;

namespace CleanBoost.Tests.Core;

/// <summary>Glob cho đường dẫn Windows (mục 8.3, 8.6): wildcard trong thành phần, <c>**</c>, không phân biệt hoa thường.</summary>
public class GlobTests
{
    private const string Home = @"C:\Users\tester";

    [Theory]
    [InlineData("*.log", "app.log", true)]
    [InlineData("*.log", "app.LOG", true)]
    [InlineData("*.log", "app.log.txt", false)]
    [InlineData("*.log*", "app.log.1", true)]
    [InlineData("*", "", true)]
    [InlineData("file?.txt", "file1.txt", true)]
    [InlineData("file?.txt", "file12.txt", false)]
    [InlineData("[abc]x", "bx", true)]
    [InlineData("[abc]x", "dx", false)]
    [InlineData("[!a]x", "ax", false)]
    [InlineData("[!a]x", "bx", true)]
    [InlineData("[^a]x", "bx", true)]
    [InlineData("[a-c]1", "B1", true)]
    [InlineData("[a-c]1", "d1", false)]
    [InlineData("[abc", "[abc", true)] // lớp không đóng: '[' là ký tự thường
    [InlineData("Dropbox*", "Dropbox (Personal)", true)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "aXXbYY", false)]
    public void ComponentMatching(string pattern, string name, bool expected) =>
        Assert.Equal(expected, Glob.ComponentMatches(pattern, name));

    [Fact]
    public void LiteralAndWildcardPathsAreCaseInsensitive()
    {
        var g = new Glob(@"C:\Users\*\AppData", Home);
        Assert.True(g.Matches(@"C:\Users\bob\AppData"));
        Assert.True(g.Matches(@"c:\users\BOB\appdata"));
        Assert.False(g.Matches(@"C:\Users\bob\AppData\Local"));
        Assert.False(g.Matches(@"C:\Users\AppData"));
        Assert.True(g.MatchesSelfOrAncestor(@"C:\Users\bob\AppData\Local\Temp"));
        Assert.False(g.MatchesSelfOrAncestor(@"C:\Users\bob\Documents"));
    }

    [Fact]
    public void GlobstarMatchesZeroOrMoreComponents()
    {
        var g = new Glob(@"C:\a\**\x.log", Home);
        Assert.True(g.Matches(@"C:\a\x.log"));
        Assert.True(g.Matches(@"C:\a\b\c\d\x.log"));
        Assert.False(g.Matches(@"C:\b\x.log"));
        Assert.False(g.Matches(@"C:\a\b\y.log"));

        var any = new Glob(@"**\*.pst", Home);
        Assert.True(any.Matches(@"D:\Mail\Archive\old.pst"));
        Assert.True(any.MatchesSelfOrAncestor(@"C:\x\mail.PST"));
        Assert.False(any.Matches(@"C:\x\mail.pst.bak"));
    }

    [Fact]
    public void DriveWildcard()
    {
        var g = new Glob(@"*:\pagefile.sys", Home);
        Assert.True(g.Matches(@"D:\pagefile.sys"));
        Assert.True(g.Matches(@"c:\PAGEFILE.SYS"));
        Assert.False(g.Matches(@"C:\x\pagefile.sys"));
    }

    [Fact]
    public void HomeExpansionAndSlashes()
    {
        Assert.Equal(Home + @"\AppData", new Glob(@"~\AppData", Home).Pattern);
        Assert.Equal(Home + @"\AppData", new Glob("~/AppData", Home).Pattern);
        Assert.Equal(Home, new Glob("~", Home).Pattern);
        Assert.Equal(Home + @"\.npm", new Glob(@"${userHome}\.npm", Home).Pattern);
        Assert.Equal(@"C:\a\b", new Glob("C:/a/b/", Home).Pattern);
        Assert.Equal(@"C:\", new Glob(@"C:\", Home).Pattern);
    }

    [Fact]
    public void LiteralPrefixAndIsLiteral()
    {
        Assert.Equal(Home + @"\AppData\Local\Temp", new Glob(@"~\AppData\Local\Temp\*", Home).LiteralPrefix);
        Assert.Equal(@"C:\", new Glob(@"C:\*", Home).LiteralPrefix);
        Assert.Equal("", new Glob(@"*:\x", Home).LiteralPrefix);
        Assert.Equal(@"C:\a\b", new Glob(@"C:\a\b\**\c", Home).LiteralPrefix);
        Assert.True(new Glob(@"C:\a\b", Home).IsLiteral);
        Assert.False(new Glob(@"C:\a\b*", Home).IsLiteral);
        Assert.False(new Glob(@"C:\a\**", Home).IsLiteral);
    }

    [Fact]
    public void CouldMatchDescendantPrunesWalk()
    {
        var g = new Glob(@"C:\a\*\c", Home);
        Assert.True(g.CouldMatchDescendant(@"C:\a"));
        Assert.True(g.CouldMatchDescendant(@"C:\a\b"));
        Assert.True(g.CouldMatchDescendant(@"C:\a\b\c"));
        Assert.False(g.CouldMatchDescendant(@"C:\x"));
        Assert.False(g.CouldMatchDescendant(@"C:\a\b\c\d"));
        Assert.True(new Glob(@"C:\a\**", Home).CouldMatchDescendant(@"C:\a\b\c\d\e"));
    }

    [Fact]
    public void LongPathPrefixIsIgnored()
    {
        var g = new Glob(@"C:\Windows\Temp", Home);
        Assert.True(g.Matches(@"\\?\C:\Windows\Temp"));
        Assert.Equal(new[] { "C:", "a", "b" }, Glob.SplitPath(@"\\?\C:\a\\b\"));
    }

    [Fact]
    public void EqualityIsCaseInsensitive()
    {
        var a = new Glob(@"C:\Foo\*", Home);
        var b = new Glob(@"c:\foo\*", Home);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, new Glob(@"C:\Bar\*", Home));
    }

    [Fact]
    public void HasMagic()
    {
        Assert.True(Glob.HasMagic("a*"));
        Assert.True(Glob.HasMagic("a?"));
        Assert.True(Glob.HasMagic("[ab]"));
        Assert.False(Glob.HasMagic("plain.txt"));
    }

    [Fact]
    public void JoinPath()
    {
        Assert.Equal(@"C:\", Glob.JoinPath(["C:"]));
        Assert.Equal(@"C:\a\b", Glob.JoinPath(["C:", "a", "b"]));
        Assert.Equal("", Glob.JoinPath([]));
    }

    [Fact]
    public void GlobSetExcludes()
    {
        var set = new GlobSet([@"~\AppData\Local\Microsoft", @"~\AppData\Local\Keep*"], Home);
        Assert.False(set.IsEmpty);
        Assert.True(set.Matches(Home + @"\AppData\Local\KeepMe"));
        Assert.False(set.Matches(Home + @"\AppData\Local\Microsoft\Edge"));
        Assert.True(set.MatchesSelfOrAncestor(Home + @"\AppData\Local\Microsoft\Edge"));
        Assert.False(set.MatchesSelfOrAncestor(Home + @"\AppData\Local\Vendor"));
        Assert.True(GlobSet.Empty.IsEmpty);
        Assert.False(GlobSet.Empty.MatchesSelfOrAncestor(@"C:\x"));
    }
}
