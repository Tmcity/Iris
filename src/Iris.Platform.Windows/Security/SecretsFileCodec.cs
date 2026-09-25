using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Iris.Platform.Windows.Security;

internal static class SecretsFileCodec
{
    public const string Format = "iris-secrets";
    public const int Version = 1;

    /// <summary>解析 secrets.dat 的字节。不抛异常：格式问题通过返回值表达。</summary>
    public static SecretsFileParseResult Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.IsEmpty)
        {
            return new SecretsFileParseResult(new Dictionary<string, byte[]>(), true, Array.Empty<string>());
        }

        try
        {
            using var doc = JsonDocument.Parse((ReadOnlyMemory<byte>)utf8Json.ToArray(), new JsonDocumentOptions { AllowDuplicateProperties = false });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new SecretsFileParseResult(new Dictionary<string, byte[]>(), true, Array.Empty<string>());
            }

            if (!root.TryGetProperty("format", out var formatProp) ||
                formatProp.ValueKind != JsonValueKind.String ||
                !string.Equals(formatProp.GetString(), Format, StringComparison.Ordinal))
            {
                return new SecretsFileParseResult(new Dictionary<string, byte[]>(), true, Array.Empty<string>());
            }

            if (!root.TryGetProperty("version", out var versionProp) ||
                versionProp.ValueKind != JsonValueKind.Number ||
                !versionProp.TryGetInt32(out var version) ||
                version != Version)
            {
                return new SecretsFileParseResult(new Dictionary<string, byte[]>(), true, Array.Empty<string>());
            }

            if (!root.TryGetProperty("entries", out var entriesProp) ||
                entriesProp.ValueKind != JsonValueKind.Object)
            {
                return new SecretsFileParseResult(new Dictionary<string, byte[]>(), true, Array.Empty<string>());
            }

            var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var droppedKeys = new List<string>();

            foreach (var prop in entriesProp.EnumerateObject())
            {
                var key = prop.Name;
                if (string.IsNullOrWhiteSpace(key))
                {
                    droppedKeys.Add("(空键名)");
                    continue;
                }

                if (prop.Value.ValueKind != JsonValueKind.String)
                {
                    droppedKeys.Add(key);
                    continue;
                }

                var b64 = prop.Value.GetString();
                if (b64 == null)
                {
                    droppedKeys.Add(key);
                    continue;
                }

                try
                {
                    var cipherBytes = Convert.FromBase64String(b64);
                    entries[key] = cipherBytes;
                }
                catch (FormatException)
                {
                    droppedKeys.Add(key);
                }
            }

            return new SecretsFileParseResult(entries, false, droppedKeys);
        }
        catch (JsonException)
        {
            return new SecretsFileParseResult(new Dictionary<string, byte[]>(), true, Array.Empty<string>());
        }
    }

    /// <summary>按键名序数排序写出；UTF-8 无 BOM、缩进、末尾换行。</summary>
    public static byte[] Serialize(IReadOnlyDictionary<string, byte[]> encryptedEntries)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", Format);
            writer.WriteNumber("version", Version);
            writer.WriteStartObject("entries");

            foreach (var key in encryptedEntries.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var b64 = Convert.ToBase64String(encryptedEntries[key]);
                writer.WriteString(key, b64);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }
}

internal sealed record SecretsFileParseResult(
    IReadOnlyDictionary<string, byte[]> Entries,   // 键 → DPAPI 密文（已从 Base64 解码）
    bool IsCorrupted,                              // true：整个文件不可用，需要做损坏备份
    IReadOnlyList<string> DroppedKeys);            // 被单独丢弃的条目（只有键名）
