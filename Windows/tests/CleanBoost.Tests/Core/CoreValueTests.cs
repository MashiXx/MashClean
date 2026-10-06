using System.Globalization;
using System.Text.Json;
using CleanBoost.Core;

namespace CleanBoost.Tests.Core;

/// <summary>Định dạng kiểu printf của khoá bản Mac (<c>%@</c>, <c>%lld</c>, <c>%.1f</c>, <c>%1$@</c>).</summary>
public class FormatPrintfTests
{
    private static string F(string format, params object?[] args) => Loc.FormatPrintf(format, args, CultureInfo.InvariantCulture);

    [Fact]
    public void ObjectAndIntegerConversions()
    {
        Assert.Equal("Xoá 3 mục trong Cache", F("Xoá %lld mục trong %@", 3L, "Cache"));
        Assert.Equal("a 7 b", F("a %d b", 7));
        Assert.Equal("n=42", F("n=%ld", 42));
        Assert.Equal("u=5", F("u=%u", 5u));
        Assert.Equal("i=-1", F("i=%i", -1));
        Assert.Equal("s=x", F("s=%s", "x"));
    }

    [Fact]
    public void IntegerConversionRoundsDoubles() => Assert.Equal("3", F("%d", 2.6));

    [Fact]
    public void FloatPrecision()
    {
        Assert.Equal("1.5 GB", F("%.1f GB", 1.4999));
        Assert.Equal("2", F("%.0f", 1.5));
        Assert.Equal("0.333333", F("%f", 1.0 / 3));
        Assert.Equal("1,5", Loc.FormatPrintf("%.1f", [1.5], CultureInfo.GetCultureInfo("vi-VN")));
    }

    [Fact]
    public void PositionalArguments()
    {
        Assert.Equal("B trước A", F("%2$@ trước %1$@", "A", "B"));
        Assert.Equal("A A", F("%1$@ %1$@", "A"));
    }

    [Fact]
    public void EscapesAndMalformedSpecifiers()
    {
        Assert.Equal("100%", F("100%%"));
        Assert.Equal("50% off", F("50% off"));     // "% o" không phải đặc tả hợp lệ: giữ nguyên
        Assert.Equal("end %", F("end %"));
        Assert.Equal("x %l", F("x %l"));
        Assert.Equal("a  b", F("a %@ b"));          // thiếu tham số → rỗng
        Assert.Equal("a  b", F("a %@ b", [null]));
    }

    [Fact]
    public void ByteCountArgumentUsesFormatted()
    {
        var b = ByteCount.Megabytes(5);
        Assert.Equal("Đã dọn " + b.Formatted, F("Đã dọn %@", b));
    }
}

public class ByteCountTests
{
    private static string Sep => Loc.Culture.NumberFormat.NumberDecimalSeparator;

    [Fact]
    public void FormatsWithDecimalUnits()
    {
        Assert.Equal(Loc.IsEnglish ? "Zero KB" : "0 KB", ByteCount.Format(0));
        Assert.Equal("1 byte", ByteCount.Format(1));
        Assert.Equal("999 bytes", ByteCount.Format(999));
        Assert.Equal("2 KB", ByteCount.Format(1_500));
        Assert.Equal($"1{Sep}5 MB", ByteCount.Format(1_500_000));
        Assert.Equal($"1{Sep}23 GB", ByteCount.Format(1_234_000_000));
        Assert.Equal("1 TB", ByteCount.Format(1_000_000_000_000));
        Assert.Equal($"-1{Sep}5 MB", ByteCount.Format(-1_500_000));
    }

    [Fact]
    public void ArithmeticAndComparison()
    {
        var a = ByteCount.Kilobytes(3);
        var b = ByteCount.Megabytes(1);
        Assert.Equal(1_003_000, (a + b).Bytes);
        Assert.Equal(997_000, (b - a).Bytes);
        Assert.True(a < b && b > a && a <= ByteCount.Kilobytes(3) && b >= ByteCount.Megabytes(1));
        Assert.Equal(0, ByteCount.Zero.CompareTo(new ByteCount(0)));
        Assert.Equal(6_000, new[] { a, a }.Sum(x => x).Bytes);
        Assert.Equal(1_000_000_000, ByteCount.Gigabytes(1).Bytes);
    }

    [Fact]
    public void SerializesAsNumber()
    {
        Assert.Equal("1234", JsonSerializer.Serialize(new ByteCount(1234)));
        Assert.Equal(new ByteCount(99), JsonSerializer.Deserialize<ByteCount>("99"));
    }
}

public class OSVersionTests
{
    [Theory]
    [InlineData("10.0.19045", 10, 0, 19045)]
    [InlineData("10", 10, 0, 0)]
    [InlineData("10.1", 10, 1, 0)]
    [InlineData("1.2.x", 1, 2, 0)]
    public void Parses(string s, int major, int minor, int patch) => Assert.Equal(new OSVersion(major, minor, patch), OSVersion.Parse(s));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("abc")]
    public void RejectsGarbage(string? s) => Assert.Null(OSVersion.Parse(s));

    [Fact]
    public void Ordering()
    {
        Assert.True(new OSVersion(10, 0, 22631) > new OSVersion(10, 0, 19045));
        Assert.True(new OSVersion(10, 1) > new OSVersion(10, 0, 99999));
        Assert.True(new OSVersion(11) >= new OSVersion(11, 0, 0));
        Assert.True(OSVersion.Current >= new OSVersion(10));
        Assert.Equal("10.0.19045", new OSVersion(10, 0, 19045).ToString());
    }

    [Fact]
    public void PackedRoundTrip()
    {
        var v = new OSVersion(1, 2, 3);
        Assert.Equal(10_203u, v.Packed);
        Assert.Equal(v, OSVersion.FromPacked(v.Packed));
        Assert.True(new OSVersion(2).Packed > new OSVersion(1, 99, 99).Packed);
    }
}

public class SafetyValueTests
{
    [Theory]
    [InlineData("moveToTrash", RemovalKind.MoveToTrash, "trash")]
    [InlineData("delete", RemovalKind.Delete, "delete")]
    [InlineData("deleteContents", RemovalKind.DeleteContents, "deleteContents")]
    [InlineData("custom:docker", RemovalKind.Custom, "custom:docker")]
    public void RemovalStrategyRoundTrip(string raw, RemovalKind kind, string log)
    {
        var s = RemovalStrategy.Parse(raw);
        Assert.NotNull(s);
        Assert.Equal(kind, s.Value.Kind);
        Assert.Equal(raw, s.Value.RuleValue);
        Assert.Equal(log, s.Value.LogValue);
        Assert.Equal(kind == RemovalKind.MoveToTrash, s.Value.IsRecoverable);
        Assert.Equal("\"" + raw + "\"", JsonSerializer.Serialize(s.Value));
    }

    [Theory]
    [InlineData("custom:")]
    [InlineData("rm -rf")]
    [InlineData("")]
    public void RemovalStrategyRejectsUnknown(string raw)
    {
        Assert.Null(RemovalStrategy.Parse(raw));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RemovalStrategy>("\"" + raw + "\""));
    }

    [Fact]
    public void SafetyLevels()
    {
        Assert.Equal(SafetyLevel.Safe, SafetyLevelExtensions.ParseRuleValue("safe"));
        Assert.Equal(SafetyLevel.Risky, SafetyLevelExtensions.ParseRuleValue("risky"));
        Assert.Null(SafetyLevelExtensions.ParseRuleValue("SAFE"));
        Assert.Equal(SafetyLevel.Risky, SafetyLevel.Safe.Raised(SafetyLevel.Risky));
        Assert.Equal(SafetyLevel.Review, SafetyLevel.Review.Raised(SafetyLevel.Safe));
    }

    [Fact]
    public void RemoveResultLogValues()
    {
        Assert.Equal("ok", RemoveResult.Ok("p", 1).LogValue);
        Assert.Equal("skipped:notFound", RemoveResult.Skipped("p", SkipReason.NotFound).LogValue);
        Assert.Equal("skipped:dryRun", RemoveResult.Skipped("p", SkipReason.DryRun).LogValue);
        Assert.Equal("failed:5", RemoveResult.Failed("p", 5).LogValue);
        Assert.True(RemoveResult.Skipped("p", SkipReason.NotFound).IsSuccess);
        Assert.False(RemoveResult.Skipped("p", SkipReason.InUse).IsSuccess);
        Assert.True(RemoveResult.Failed("p", 5).IsFailure);
    }

    [Fact]
    public void IgnoreListMatching()
    {
        var list = new IgnoreList(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Users\t\AppData\Local\Keep" },
            new HashSet<string> { "rule.a" },
            new HashSet<string> { "App1" });
        Assert.True(list.IgnoresPath(@"C:\Users\t\AppData\Local\Keep"));
        Assert.True(list.IgnoresPath(@"c:\users\t\appdata\local\keep\sub\x"));
        Assert.False(list.IgnoresPath(@"C:\Users\t\AppData\Local\KeepMe"));
        Assert.True(list.IgnoresRule("rule.a"));
        Assert.False(list.IgnoresRule(null));
        Assert.True(list.IgnoresApp("App1"));
        Assert.False(list.IgnoresApp(null));
        Assert.False(IgnoreList.Empty.IgnoresPath(@"C:\x"));
    }

    [Fact]
    public void LocalizedTextResolution()
    {
        var t = new LocalizedText("Bộ nhớ đệm", "Cache");
        Assert.Contains(t.Resolved, new[] { "Bộ nhớ đệm", "Cache" });
        var json = JsonSerializer.Serialize(t);
        Assert.StartsWith("{\"en\":\"Cache\",\"vi\":", json);
        Assert.Equal(t, JsonSerializer.Deserialize<LocalizedText>(json));
        var single = JsonSerializer.Deserialize<LocalizedText>("\"Only English\"");
        Assert.Equal("Only English", single!.Resolved);
        Assert.True(LocalizedText.Empty.IsEmpty);
    }
}
