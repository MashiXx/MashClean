using System.Globalization;
using System.Xml;
using CleanBoost.Core;
using CleanBoost.Engine.Cleaning;
using CleanBoost.Engine.Nodes;
using CleanBoost.Engine.Rules;
using CleanBoost.Engine.Scanning;

namespace CleanBoost.Features.SystemJunk;

/// <summary>Thùng rác của từng ổ (SHQueryRecycleBin), đổ bằng SHEmptyRecycleBin — không đụng trực tiếp <c>$Recycle.Bin</c>.</summary>
public sealed class RecycleBinProvider : IJunkProvider
{
    public string Name => "recycleBin";

    public Task<(List<Node> Nodes, List<ScanWarning> Warnings)> NodesAsync(Rule rule, ScanContext context)
    {
        var nodes = new List<Node>();
        foreach (var drive in RecycleBin.Drives())
        {
            if (RecycleBin.Query(drive) is not { Count: > 0 } info) continue;
            var label = drive.TrimEnd('\\');
            var title = label.Equals(KnownPaths.SystemDrive, StringComparison.OrdinalIgnoreCase)
                ? Loc.T("Thùng rác")
                : Loc.F("Thùng rác trên ổ %@", label);
            var node = rule.MakeVirtualNode(new RecycleBinItem(drive), title, new ByteCount(info.Size), (int)Math.Min(int.MaxValue, info.Count));
            node.Removal = RemovalStrategy.Custom(RemoverIDs.RecycleBin);
            node.Icon = "trash";
            nodes.Add(node);
        }
        return Task.FromResult((nodes, new List<ScanWarning>()));
    }
}

/// <summary>Docker: dung lượng giải phóng được theo <c>docker system df</c>; dọn bằng <c>docker system prune -f</c>.</summary>
public sealed class DockerProvider : IJunkProvider
{
    public string Name => "docker.prune";

    public async Task<(List<Node> Nodes, List<ScanWarning> Warnings)> NodesAsync(Rule rule, ScanContext context)
    {
        if (DockerRemover.DockerPath is not { } docker) return ([], []);
        try
        {
            var output = await ProcessRunner.RunAsync(docker, ["system", "df", "--format", "{{.Reclaimable}}"], TimeSpan.FromSeconds(15), context.Cancellation)
                .ConfigureAwait(false);
            if (!output.Succeeded) return ([], []);
            long total = 0;
            foreach (var line in output.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                total += ParseSize(line);
            }
            if (total <= 0) return ([], []);
            var node = rule.MakeVirtualNode(new DockerPruneItem(), Loc.T("Docker: container đã dừng, image và cache không dùng"), new ByteCount(total));
            node.Removal = RemovalStrategy.Custom(RemoverIDs.Docker);
            return ([node], []);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return ([], []);
        }
    }

    /// <summary>"1.2GB (40%)" → byte, đơn vị thập phân.</summary>
    public static long ParseSize(string text)
    {
        var value = text.Split(' ')[0].Trim();
        (string Unit, double Mult)[] units = [("TB", 1e12), ("GB", 1e9), ("MB", 1e6), ("kB", 1e3), ("KB", 1e3), ("B", 1)];
        foreach (var (unit, mult) in units)
        {
            if (value.EndsWith(unit, StringComparison.Ordinal) &&
                double.TryParse(value[..^unit.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                return (long)(n * mult);
        }
        return 0;
    }
}

/// <summary>
/// Bản sao lưu iPhone/iPad của iTunes/Apple Devices: <c>%APPDATA%\Apple Computer\MobileSync\Backup</c> (bản iTunes cài từ
/// web) và <c>%USERPROFILE%\Apple\MobileSync\Backup</c> (bản Microsoft Store). Đọc <c>Info.plist</c> để lấy tên thiết bị.
/// </summary>
public sealed class IosBackupsProvider : IJunkProvider
{
    public string Name => "iosBackups";

    public Task<(List<Node> Nodes, List<ScanWarning> Warnings)> NodesAsync(Rule rule, ScanContext context)
    {
        var resolver = context.Rules.Resolver;
        string[] roots =
        [
            resolver.Resolve(@"%APPDATA%\Apple Computer\MobileSync\Backup"),
            resolver.Resolve(@"~\Apple\MobileSync\Backup"),
        ];
        var nodes = new List<Node>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Core.FileSystem.DirectoryReader.List(root).Where(e => e.IsDirectory && !e.IsLink))
            {
                context.Cancellation.ThrowIfCancellationRequested();
                var path = Path.Combine(root, dir.Name);
                var (device, product, date) = ReadInfo(Path.Combine(path, "Info.plist"));
                var when = date ?? dir.LastWriteUtc.ToLocalTime();
                var title = $"{device ?? dir.Name} — {when.ToString("g", Loc.Culture)}" + (product is null ? "" : $" ({product})");
                var m = context.FileSystem.MeasureConcurrently(path, context.Progress.FileCounter, ct: context.Cancellation);
                var node = rule.MakeFileNode(path, m, title, allowedRoot: root);
                node.Icon = "iphone";
                nodes.Add(node);
            }
        }
        return Task.FromResult((nodes, new List<ScanWarning>()));
    }

    /// <summary>Đọc khoá trong plist XML: Device Name/Display Name, Product Type, Last Backup Date.</summary>
    internal static (string? Device, string? Product, DateTime? Date) ReadInfo(string plist)
    {
        try
        {
            if (!File.Exists(plist)) return (null, null, null);
            var doc = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(plist, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }))
            {
                doc.Load(reader);
            }
            var dict = doc.SelectSingleNode("/plist/dict");
            if (dict is null) return (null, null, null);
            string? Value(string key)
            {
                for (var n = dict.FirstChild; n is not null; n = n.NextSibling)
                {
                    if (n.Name == "key" && n.InnerText == key) return n.NextSibling?.InnerText;
                }
                return null;
            }
            var device = Value("Device Name") ?? Value("Display Name");
            DateTime? date = DateTime.TryParse(Value("Last Backup Date"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d.ToLocalTime() : null;
            return (device, Value("Product Type"), date);
        }
        catch
        {
            return (null, null, null);
        }
    }
}
