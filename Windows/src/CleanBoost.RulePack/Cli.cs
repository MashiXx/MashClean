namespace CleanBoost.RulePack;

public sealed class CliException(string message) : Exception(message);

/// <summary>Tham số dòng lệnh: vị trí, <c>--tên giá trị</c>, cờ <c>--force</c>.</summary>
public sealed class Arguments
{
    private readonly List<string> _positional = [];
    private readonly Dictionary<string, string?> _named = new(StringComparer.Ordinal);

    public Arguments(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var name = a[2..];
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) _named[name] = args[++i];
                else _named[name] = null;
            }
            else
            {
                _positional.Add(a);
            }
        }
    }

    public string Positional(int index, string what) =>
        index < _positional.Count ? _positional[index] : throw new CliException("Thiếu tham số: " + what);

    public string Required(string name) =>
        _named.TryGetValue(name, out var v) && v is not null ? v : throw new CliException("Thiếu --" + name);

    public string? Optional(string name) => _named.TryGetValue(name, out var v) ? v : null;
    public bool Flag(string name) => _named.ContainsKey(name);
}
