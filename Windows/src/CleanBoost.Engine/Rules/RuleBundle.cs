using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CleanBoost.Core;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace CleanBoost.Engine.Rules;

public sealed class RuleException(RuleException.Kind kind, string? detail = null) : Exception(Describe(kind, detail))
{
    public enum Kind
    {
        BadFormat,
        BadMagic,
        UnsupportedFormatVersion,
        BadSignature,
        PayloadLengthMismatch,
        DecompressionFailed,
        InvalidJSON,
        AppTooOld,
        Downgrade,
        ChecksumMismatch,
        WrongPlatform,
    }

    public Kind ErrorKind { get; } = kind;

    private static string Describe(Kind kind, string? detail) => kind switch
    {
        Kind.BadFormat => Loc.T("Rule bundle sai định dạng"),
        Kind.BadMagic => "Rule bundle sai magic",
        Kind.UnsupportedFormatVersion => Loc.F("Phiên bản định dạng rule không hỗ trợ: %@", detail ?? ""),
        Kind.BadSignature => Loc.T("Chữ ký rule bundle không hợp lệ"),
        Kind.PayloadLengthMismatch => Loc.T("Độ dài payload không khớp"),
        Kind.DecompressionFailed => Loc.T("Không giải nén được payload"),
        Kind.InvalidJSON => Loc.F("JSON rule không hợp lệ: %@", detail ?? ""),
        Kind.AppTooOld => Loc.F("Bộ rule cần app phiên bản %@ trở lên", detail ?? ""),
        Kind.Downgrade => Loc.F("Từ chối hạ cấp rule: %@", detail ?? ""),
        Kind.ChecksumMismatch => Loc.T("SHA-256 không khớp manifest"),
        _ => Loc.T("Bộ rule không dành cho Windows"),
    };
}

/// <summary>
/// Header 22 byte của <c>rules.bundle</c> (SYSTEM_DESIGN mục 8.5). Số nguyên little-endian. Bản Windows dùng magic
/// <c>MSRW</c> và nén payload bằng Brotli (LZFSE chỉ có trên Apple), để bộ rule Mac và Windows không lẫn nhau.
/// <list type="table">
/// <item>0 · 4 · magic <c>MSRW</c></item>
/// <item>4 · 2 · phiên bản định dạng (1)</item>
/// <item>6 · 8 · phiên bản rule (yyyymmddNN)</item>
/// <item>14 · 4 · phiên bản app tối thiểu</item>
/// <item>18 · 4 · độ dài payload</item>
/// <item>22 · n · payload JSON nén Brotli</item>
/// <item>22+n · 64 · chữ ký Ed25519 trên các byte [0, 22+n)</item>
/// </list>
/// </summary>
public readonly record struct RuleBundleHeader(ushort FormatVersion, ulong RulesVersion, uint MinAppVersion, uint PayloadLength)
{
    public static readonly byte[] Magic = "MSRW"u8.ToArray();
    public const int Size = 22;
    public const ushort CurrentFormatVersion = 1;
    public const int SignatureSize = 64;

    public static RuleBundleHeader Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size) throw new RuleException(RuleException.Kind.BadFormat);
        if (!data[..4].SequenceEqual(Magic)) throw new RuleException(RuleException.Kind.BadMagic);
        var h = new RuleBundleHeader(
            BinaryPrimitives.ReadUInt16LittleEndian(data[4..]),
            BinaryPrimitives.ReadUInt64LittleEndian(data[6..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[14..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[18..]));
        if (h.FormatVersion != CurrentFormatVersion) throw new RuleException(RuleException.Kind.UnsupportedFormatVersion, h.FormatVersion.ToString());
        return h;
    }

    public byte[] Serialize()
    {
        var d = new byte[Size];
        Magic.CopyTo(d, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(4), FormatVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(6), RulesVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(14), MinAppVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(18), PayloadLength);
        return d;
    }
}

/// <summary>Bộ rule đã kiểm tra chữ ký và giải nén.</summary>
public sealed record RuleBundle(RuleBundleHeader Header, RulePayload Payload)
{
    public ulong Version => Header.RulesVersion;

    public static RulePayload ParsePayload(byte[] json)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<RulePayload>(json, RuleJson.Options) ?? throw new RuleException(RuleException.Kind.InvalidJSON, "null");
            if (!string.Equals(payload.Platform, "windows", StringComparison.OrdinalIgnoreCase)) throw new RuleException(RuleException.Kind.WrongPlatform);
            return payload;
        }
        catch (JsonException ex)
        {
            throw new RuleException(RuleException.Kind.InvalidJSON, ex.Message);
        }
    }
}

/// <summary>Kiểm tra chữ ký Ed25519 và giải nén (mục 8.5).</summary>
public sealed class RuleBundleVerifier
{
    private readonly Ed25519PublicKeyParameters _publicKey;

    public RuleBundleVerifier(string publicKeyBase64)
    {
        byte[] raw;
        try { raw = Convert.FromBase64String(publicKeyBase64); } catch (FormatException) { throw new RuleException(RuleException.Kind.BadFormat); }
        if (raw.Length != 32) throw new RuleException(RuleException.Kind.BadFormat);
        _publicKey = new Ed25519PublicKeyParameters(raw, 0);
    }

    public RuleBundle Verify(byte[] data)
    {
        if (data.Length <= RuleBundleHeader.Size + RuleBundleHeader.SignatureSize) throw new RuleException(RuleException.Kind.BadFormat);
        if (!data.AsSpan(0, 4).SequenceEqual(RuleBundleHeader.Magic)) throw new RuleException(RuleException.Kind.BadMagic);
        var signedLength = data.Length - RuleBundleHeader.SignatureSize;
        var signer = new Ed25519Signer();
        signer.Init(false, _publicKey);
        signer.BlockUpdate(data, 0, signedLength);
        if (!signer.VerifySignature(data.AsSpan(signedLength).ToArray())) throw new RuleException(RuleException.Kind.BadSignature);
        var header = RuleBundleHeader.Parse(data);
        var payloadLength = signedLength - RuleBundleHeader.Size;
        if (payloadLength != header.PayloadLength) throw new RuleException(RuleException.Kind.PayloadLengthMismatch);
        byte[] json;
        try
        {
            using var input = new MemoryStream(data, RuleBundleHeader.Size, payloadLength);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            brotli.CopyTo(output);
            json = output.ToArray();
        }
        catch (Exception)
        {
            throw new RuleException(RuleException.Kind.DecompressionFailed);
        }
        return new RuleBundle(header, RuleBundle.ParsePayload(json));
    }
}

/// <summary>Đóng gói và ký (dùng bởi <c>rulepack build</c> / <c>rulepack sign</c>). Khoá riêng chỉ nằm trong secret của CI.</summary>
public static class RuleBundleBuilder
{
    public static byte[] EncodePayload(RulePayload payload) => JsonSerializer.SerializeToUtf8Bytes(payload, RuleJson.Options);

    /// <summary>Tạo phần chưa ký: header + payload Brotli.</summary>
    public static byte[] Pack(RulePayload payload, OSVersion minAppVersion)
    {
        var json = EncodePayload(payload);
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) brotli.Write(json);
        var compressed = output.ToArray();
        var header = new RuleBundleHeader(RuleBundleHeader.CurrentFormatVersion, payload.Version, minAppVersion.Packed, (uint)compressed.Length);
        return [.. header.Serialize(), .. compressed];
    }

    /// <summary>Ký các byte [0, 22+n) bằng Ed25519 và nối chữ ký vào cuối.</summary>
    public static byte[] Sign(byte[] unsigned, byte[] privateKeySeed)
    {
        var key = new Ed25519PrivateKeyParameters(privateKeySeed, 0);
        var signer = new Ed25519Signer();
        signer.Init(true, key);
        signer.BlockUpdate(unsigned, 0, unsigned.Length);
        var signature = signer.GenerateSignature();
        return [.. unsigned, .. signature];
    }

    /// <summary>Sinh cặp khoá mới: (khoá riêng 32 byte, khoá công khai 32 byte).</summary>
    public static (byte[] PrivateKey, byte[] PublicKey) GenerateKeyPair()
    {
        var key = new Ed25519PrivateKeyParameters(new SecureRandom());
        return (key.GetEncoded(), key.GeneratePublicKey().GetEncoded());
    }

    public static byte[] PublicKeyFor(byte[] privateKeySeed) => new Ed25519PrivateKeyParameters(privateKeySeed, 0).GeneratePublicKey().GetEncoded();
}

/// <summary>Đọc thư mục nguồn <c>Rules\*.json</c> (dùng cho <c>rulepack</c> và chế độ phát triển).</summary>
public static class RuleSourceLoader
{
    /// <summary>Mỗi file JSON là một rule hoặc một mảng rule; <c>knowledge.json</c> chứa tri thức; <c>version.txt</c> (tuỳ chọn) chứa phiên bản.</summary>
    public static RulePayload Load(string directory, ulong? version = null, bool strict = false)
    {
        if (!Directory.Exists(directory)) throw new RuleException(RuleException.Kind.BadFormat, directory);
        var options = strict ? RuleJson.StrictOptions : RuleJson.Options;
        var rules = new List<Rule>();
        var knowledge = new Knowledge();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            var data = File.ReadAllBytes(file);
            if (name == "knowledge.json")
            {
                knowledge = JsonSerializer.Deserialize<Knowledge>(data, options) ?? new Knowledge();
                continue;
            }
            if (name.StartsWith("manifest", StringComparison.Ordinal)) continue;
            try
            {
                var trimmed = Encoding.UTF8.GetString(data).TrimStart('﻿', ' ', '\r', '\n', '\t');
                if (trimmed.StartsWith('[')) rules.AddRange(JsonSerializer.Deserialize<List<Rule>>(trimmed, options) ?? []);
                else rules.Add(JsonSerializer.Deserialize<Rule>(trimmed, options) ?? throw new JsonException("null"));
            }
            catch (JsonException ex)
            {
                throw new RuleException(RuleException.Kind.InvalidJSON, name + ": " + ex.Message);
            }
        }
        var resolved = version;
        if (resolved is null)
        {
            var versionFile = Path.Combine(directory, "version.txt");
            if (File.Exists(versionFile) && ulong.TryParse(File.ReadAllText(versionFile).Trim(), out var v)) resolved = v;
        }
        return new RulePayload { Version = resolved ?? 0, GeneratedAt = DateTimeOffset.UtcNow, Rules = rules, Knowledge = knowledge };
    }
}
