using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CleanBoost.Core.Ipc;

/// <summary>
/// Giao thức app ↔ helper quản trị (SYSTEM_DESIGN mục 9.2), bản Windows: named pipe, mỗi thông điệp là
/// 4 byte độ dài (little-endian) + JSON UTF-8. Tham số dùng kiểu cơ bản; helper không nhận lệnh shell tự do.
/// </summary>
public static class HelperCommands
{
    public const string Hello = "hello";
    public const string RemoveItems = "removeItems";
    public const string RunMaintenance = "runMaintenance";
    public const string SetStartupApproved = "setStartupApproved";
    public const string DeleteRunValue = "deleteRunValue";
    public const string SetScheduledTaskEnabled = "setScheduledTaskEnabled";
    public const string Quit = "quit";

    /// <summary>Giới hạn số đường dẫn mỗi lần gọi (mục 9.4).</summary>
    public const int MaxPathsPerCall = 10_000;
}

public sealed record HelperRequest
{
    public required string Command { get; init; }
    public int Id { get; init; }
    public string[]? Paths { get; init; }
    /// <summary>
    /// Vùng rule cho phép của từng đường dẫn (song song với <see cref="Paths"/>); helper kiểm tra lại sau khi chuẩn hoá,
    /// để thư mục bị tráo giữa lúc xác nhận và lúc xoá không đưa việc xoá ra ngoài vùng đó.
    /// </summary>
    public string[]?[]? ItemRoots { get; init; }
    public RemoveMode Mode { get; init; }
    public string? Task { get; init; }
    /// <summary>Phạm vi registry cho lệnh khởi động: <c>machine</c> hoặc <c>machine32</c>.</summary>
    public string? Scope { get; init; }
    /// <summary>Nhóm khởi động: <c>Run</c>, <c>RunOnce</c>, <c>StartupFolder</c>.</summary>
    public string? Location { get; init; }
    public string? Name { get; init; }
    public bool Enabled { get; init; }
}

public sealed record HelperResponse
{
    public int Id { get; init; }
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public int Version { get; init; }
    public RemoveResult[]? Results { get; init; }
    public CommandResult? Command { get; init; }
}

public static class PipeMessage
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Giới hạn kích thước một thông điệp (16 MB) để không cấp phát tuỳ ý theo dữ liệu từ đầu kia.</summary>
    public const int MaxBytes = 16 * 1024 * 1024;

    public static async Task WriteAsync<T>(PipeStream pipe, T value, CancellationToken ct = default)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, json.Length);
        await pipe.WriteAsync(header, ct).ConfigureAwait(false);
        await pipe.WriteAsync(json, ct).ConfigureAwait(false);
        await pipe.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<T?> ReadAsync<T>(PipeStream pipe, CancellationToken ct = default)
    {
        var header = new byte[4];
        if (!await ReadExactlyAsync(pipe, header, ct).ConfigureAwait(false)) return default;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxBytes) throw new InvalidDataException("Bad message length " + length);
        var body = new byte[length];
        if (!await ReadExactlyAsync(pipe, body, ct).ConfigureAwait(false)) return default;
        return JsonSerializer.Deserialize<T>(body, Options);
    }

    private static async Task<bool> ReadExactlyAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await s.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }
}
