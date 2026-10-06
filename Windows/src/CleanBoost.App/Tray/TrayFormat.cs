using System.Globalization;
using CleanBoost.Core;

namespace CleanBoost.App.Tray;

/// <summary>Số liệu hiện trên biểu tượng khay (StatusItemStyle của bản Mac), lưu ở <c>menuBar.statusStyle</c>.</summary>
[Flags]
public enum TrayStatusParts
{
    None = 0,
    Cpu = 1,
    Memory = 2,
    Network = 4,
}

public static class TrayStatusStyle
{
    /// <summary>Đọc <c>"cpu,memory,network"</c>; rỗng hoặc <c>"iconOnly"</c> là chỉ biểu tượng; vẫn đọc giá trị cũ <c>cpuAndMemory</c>.</summary>
    public static TrayStatusParts Parse(string? raw)
    {
        var parts = TrayStatusParts.None;
        foreach (var token in (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            parts |= token switch
            {
                "cpu" => TrayStatusParts.Cpu,
                "memory" => TrayStatusParts.Memory,
                "network" => TrayStatusParts.Network,
                "cpuAndMemory" => TrayStatusParts.Cpu | TrayStatusParts.Memory,
                _ => TrayStatusParts.None,
            };
        }
        return parts;
    }

    public static string Serialize(TrayStatusParts parts)
    {
        var tokens = new List<string>(3);
        if (parts.HasFlag(TrayStatusParts.Cpu)) tokens.Add("cpu");
        if (parts.HasFlag(TrayStatusParts.Memory)) tokens.Add("memory");
        if (parts.HasFlag(TrayStatusParts.Network)) tokens.Add("network");
        return string.Join(',', tokens);
    }
}

/// <summary>Định dạng chữ của popup và biểu tượng khay (giống MenuBarPopoverView/MenuBarModel của bản Mac).</summary>
public static class TrayFormat
{
    public static int PercentValue(double fraction) => (int)Math.Round(fraction * 100, MidpointRounding.AwayFromZero);

    public static string Percent(double fraction) => PercentValue(fraction).ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>Tốc độ mạng trong popup: định dạng dung lượng + "/s".</summary>
    public static string Rate(double bytesPerSecond) => ByteCount.Format((long)Math.Max(0, bytesPerSecond)) + "/s";

    /// <summary>Tốc độ gọn cho chú thích biểu tượng khay: "0 KB/s", "850 KB/s", "1,2 MB/s" (luôn dấu phẩy, như bản Mac).</summary>
    public static string CompactRate(double bytesPerSecond)
    {
        var kb = Math.Max(0, bytesPerSecond) / 1000;
        if (kb < 1000) return ((long)Math.Round(kb, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture) + " KB/s";
        var mb = kb / 1000;
        return mb < 100
            ? mb.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + " MB/s"
            : ((long)Math.Round(mb, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture) + " MB/s";
    }

    public static string Minutes(int minutes) =>
        minutes >= 60 ? Loc.F("%lld giờ %lld phút", minutes / 60, minutes % 60) : Loc.F("%lld phút", minutes);

    public static string Duration(TimeSpan span)
    {
        var total = (long)Math.Max(0, span.TotalSeconds);
        var days = total / 86_400;
        var hours = total % 86_400 / 3_600;
        var mins = total % 3_600 / 60;
        if (days > 0) return Loc.F("%lld ngày %lld giờ", days, hours);
        if (hours > 0) return Loc.F("%lld giờ %lld phút", hours, mins);
        return Loc.F("%lld phút", mins);
    }
}
