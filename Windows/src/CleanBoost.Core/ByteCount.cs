using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CleanBoost.Core;

/// <summary>Dung lượng tính bằng byte (Int64 để khớp SQLite).</summary>
[JsonConverter(typeof(ByteCountJsonConverter))]
public readonly record struct ByteCount(long Bytes) : IComparable<ByteCount>
{
    public static readonly ByteCount Zero = new(0);

    public static ByteCount Kilobytes(long v) => new(v * 1_000);
    public static ByteCount Megabytes(long v) => new(v * 1_000_000);
    public static ByteCount Gigabytes(long v) => new(v * 1_000_000_000);

    public static ByteCount operator +(ByteCount l, ByteCount r) => new(unchecked(l.Bytes + r.Bytes));
    public static ByteCount operator -(ByteCount l, ByteCount r) => new(unchecked(l.Bytes - r.Bytes));
    public static bool operator <(ByteCount l, ByteCount r) => l.Bytes < r.Bytes;
    public static bool operator >(ByteCount l, ByteCount r) => l.Bytes > r.Bytes;
    public static bool operator <=(ByteCount l, ByteCount r) => l.Bytes <= r.Bytes;
    public static bool operator >=(ByteCount l, ByteCount r) => l.Bytes >= r.Bytes;

    public int CompareTo(ByteCount other) => Bytes.CompareTo(other.Bytes);

    /// <summary>"1,2 GB" theo đơn vị thập phân (giống Finder/bản Mac), dấu thập phân theo ngôn ngữ giao diện.</summary>
    public string Formatted => Format(Bytes);

    public override string ToString() => Formatted;

    public static string Format(long bytes)
    {
        var culture = Loc.Culture;
        if (bytes == 0) return Loc.IsEnglish ? "Zero KB" : "0 KB";
        var negative = bytes < 0;
        double value = Math.Abs((double)bytes);
        string[] units = ["bytes", "KB", "MB", "GB", "TB", "PB"];
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        string number;
        if (unit == 0) number = ((long)value).ToString(culture);
        else if (unit <= 1) number = Math.Round(value).ToString("0", culture);
        else if (unit == 2) number = value.ToString("0.#", culture);
        else number = value.ToString("0.##", culture);
        if (unit == 0 && value == 1) return (negative ? "-" : "") + "1 byte";
        return (negative ? "-" : "") + number + " " + units[unit];
    }
}

public static class ByteCountExtensions
{
    public static ByteCount Sum<T>(this IEnumerable<T> source, Func<T, ByteCount> selector)
    {
        long total = 0;
        foreach (var item in source) total = unchecked(total + selector(item).Bytes);
        return new ByteCount(total);
    }
}

internal sealed class ByteCountJsonConverter : JsonConverter<ByteCount>
{
    public override ByteCount Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetInt64());
    public override void Write(Utf8JsonWriter writer, ByteCount value, JsonSerializerOptions options) => writer.WriteNumberValue(value.Bytes);
}
