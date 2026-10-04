using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Iris.Platform.Windows.Selection;

internal static class UiaProtocolConstants
{
    public const int CurrentVersion = 1;
    public const int MaxFrameBytes = 1_048_576; // 1 MiB
    public const int MaxRanges = 32;
    public const int MaxTextUnits = 65_536; // UTF-16 code units
    public const int ReadyTimeoutMs = 1500;
    public const int RequestTimeoutMs = 300;
    public const int ExitGraceMs = 1000;
    public const int KillConfirmationMs = 1000;
    public const int MaxStderrDrainBytes = 4096;
}

internal sealed class UiaReadyFrame
{
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = UiaProtocolConstants.CurrentVersion;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "Ready";
}

internal sealed class UiaRequestFrame
{
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = UiaProtocolConstants.CurrentVersion;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "Request";

    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("hwnd")]
    public long Hwnd { get; set; }

    [JsonPropertyName("processId")]
    public uint ProcessId { get; set; }
}

internal sealed class UiaReplyFrame
{
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = UiaProtocolConstants.CurrentVersion;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "Reply";

    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter<UiaSelectionStatus>))]
    public UiaSelectionStatus Status { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }
}

internal static class UiaProtocolCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string SerializeReady() =>
        JsonSerializer.Serialize(new UiaReadyFrame(), JsonOptions);

    public static string SerializeRequest(UiaRequestFrame request) =>
        JsonSerializer.Serialize(request, JsonOptions);

    public static string SerializeReply(UiaReplyFrame reply)
    {
        // Safety: on non-success, text must be null
        if (reply.Status != UiaSelectionStatus.Success)
        {
            reply.Text = null;
        }

        return JsonSerializer.Serialize(reply, JsonOptions);
    }

    public static UiaReadyFrame? DeserializeReady(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<UiaReadyFrame>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static UiaRequestFrame? DeserializeRequest(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<UiaRequestFrame>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static UiaReplyFrame? DeserializeReply(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<UiaReplyFrame>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static async Task WriteFrameAsync(Stream stream, object frame, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(frame, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
}

internal static class UiaProtocolFrameReader
{
    public static async Task<string?> ReadLineWithLimitAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[1];

        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(0, 1), ct).ConfigureAwait(false);
            }
            catch (IOException) when (IsCleanEof(ms))
            {
                return null; // Clean EOF on pipe broken/closed
            }

            if (read == 0)
            {
                if (IsCleanEof(ms))
                {
                    return null; // Clean EOF
                }
                throw new EndOfStreamException("Unexpected end of stream before newline delimiter.");
            }

            byte b = buffer[0];
            if (b == (byte)'\n')
            {
                break;
            }

            if (b == (byte)'\r')
            {
                continue; // Ignore CR
            }

            if (ms.Length >= maxBytes)
            {
                throw new InvalidDataException($"Protocol frame exceeded maximum size of {maxBytes} bytes.");
            }

            ms.WriteByte(b);
        }

        return DecodeFrameBytes(ms);
    }

    public static string? ReadLineWithLimit(Stream stream, int maxBytes)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            int b;
            try
            {
                b = stream.ReadByte();
            }
            catch (IOException) when (IsCleanEof(ms))
            {
                return null; // Clean EOF on pipe broken/closed
            }

            if (b == -1)
            {
                if (IsCleanEof(ms))
                {
                    return null; // Clean EOF
                }
                throw new EndOfStreamException("Unexpected end of stream before newline delimiter.");
            }

            if (b == '\n')
            {
                break;
            }

            if (b == '\r')
            {
                continue; // Ignore CR
            }

            if (ms.Length >= maxBytes)
            {
                throw new InvalidDataException($"Protocol frame exceeded maximum size of {maxBytes} bytes.");
            }

            ms.WriteByte((byte)b);
        }

        return DecodeFrameBytes(ms);
    }

    private static bool IsCleanEof(MemoryStream ms)
    {
        if (ms.Length == 0) return true;
        if (ms.Length == 3)
        {
            var bytes = ms.GetBuffer();
            if (bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return true;
            }
        }
        return false;
    }

    private static string DecodeFrameBytes(MemoryStream ms)
    {
        var array = ms.ToArray();
        int offset = 0;
        int count = array.Length;
        if (count >= 3 && array[0] == 0xEF && array[1] == 0xBB && array[2] == 0xBF)
        {
            offset = 3;
            count -= 3;
        }
        return Encoding.UTF8.GetString(array, offset, count);
    }
}
