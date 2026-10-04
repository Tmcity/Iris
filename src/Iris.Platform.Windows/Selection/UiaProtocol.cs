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
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (!root.TryGetProperty("protocolVersion", out var pvProp) ||
                pvProp.ValueKind != JsonValueKind.Number ||
                pvProp.GetInt32() != UiaProtocolConstants.CurrentVersion)
            {
                return null;
            }

            if (!root.TryGetProperty("kind", out var kindProp) ||
                kindProp.ValueKind != JsonValueKind.String ||
                kindProp.GetString() != "Ready")
            {
                return null;
            }

            return new UiaReadyFrame
            {
                ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                Kind = "Ready"
            };
        }
        catch
        {
            return null;
        }
    }

    public static UiaRequestFrame? DeserializeRequest(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (!root.TryGetProperty("protocolVersion", out var pvProp) ||
                pvProp.ValueKind != JsonValueKind.Number ||
                pvProp.GetInt32() != UiaProtocolConstants.CurrentVersion)
            {
                return null;
            }

            if (!root.TryGetProperty("kind", out var kindProp) ||
                kindProp.ValueKind != JsonValueKind.String ||
                kindProp.GetString() != "Request")
            {
                return null;
            }

            if (!root.TryGetProperty("requestId", out var reqIdProp) ||
                reqIdProp.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            var reqId = reqIdProp.GetString();
            if (string.IsNullOrWhiteSpace(reqId) || reqId.Length > 64)
            {
                return null;
            }

            if (!root.TryGetProperty("hwnd", out var hwndProp) ||
                hwndProp.ValueKind != JsonValueKind.Number ||
                !hwndProp.TryGetInt64(out var hwnd) || hwnd == 0)
            {
                return null;
            }

            if (!root.TryGetProperty("processId", out var pidProp) ||
                pidProp.ValueKind != JsonValueKind.Number ||
                !pidProp.TryGetUInt32(out var pid) || pid == 0)
            {
                return null;
            }

            return new UiaRequestFrame
            {
                ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                Kind = "Request",
                RequestId = reqId,
                Hwnd = hwnd,
                ProcessId = pid
            };
        }
        catch
        {
            return null;
        }
    }

    public static UiaReplyFrame? DeserializeReply(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (!root.TryGetProperty("protocolVersion", out var pvProp) ||
                pvProp.ValueKind != JsonValueKind.Number ||
                pvProp.GetInt32() != UiaProtocolConstants.CurrentVersion)
            {
                return null;
            }

            if (!root.TryGetProperty("kind", out var kindProp) ||
                kindProp.ValueKind != JsonValueKind.String ||
                kindProp.GetString() != "Reply")
            {
                return null;
            }

            if (!root.TryGetProperty("requestId", out var reqIdProp) ||
                reqIdProp.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            var reqId = reqIdProp.GetString();
            if (reqId == null || reqId.Length > 64)
            {
                return null;
            }

            if (!root.TryGetProperty("status", out var statusProp) ||
                statusProp.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            var statusStr = statusProp.GetString();
            UiaSelectionStatus status = statusStr switch
            {
                "Success" => UiaSelectionStatus.Success,
                "Empty" => UiaSelectionStatus.Empty,
                "Unsupported" => UiaSelectionStatus.Unsupported,
                "Sensitive" => UiaSelectionStatus.Sensitive,
                "TooLong" => UiaSelectionStatus.TooLong,
                "TargetChanged" => UiaSelectionStatus.TargetChanged,
                "Failed" => UiaSelectionStatus.Failed,
                _ => (UiaSelectionStatus)(-1)
            };
            if ((int)status == -1)
            {
                return null;
            }

            string? text = null;
            if (root.TryGetProperty("text", out var textProp))
            {
                if (textProp.ValueKind == JsonValueKind.String)
                {
                    text = textProp.GetString();
                }
                else if (textProp.ValueKind != JsonValueKind.Null)
                {
                    return null;
                }
            }

            string? errorCode = null;
            if (root.TryGetProperty("errorCode", out var errProp))
            {
                if (errProp.ValueKind == JsonValueKind.String)
                {
                    errorCode = errProp.GetString();
                }
                else if (errProp.ValueKind != JsonValueKind.Null)
                {
                    return null;
                }
            }

            if (status == UiaSelectionStatus.Success)
            {
                if (string.IsNullOrWhiteSpace(reqId))
                {
                    return null;
                }
                if (string.IsNullOrWhiteSpace(text) || text.Length > UiaProtocolConstants.MaxTextUnits)
                {
                    return null;
                }
                if (!string.IsNullOrEmpty(errorCode))
                {
                    return null;
                }
            }
            else
            {
                if (text != null)
                {
                    return null;
                }
                if (!string.IsNullOrEmpty(errorCode) && !IsValidErrorCode(errorCode))
                {
                    return null;
                }
            }

            return new UiaReplyFrame
            {
                ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                Kind = "Reply",
                RequestId = reqId,
                Status = status,
                Text = text,
                ErrorCode = errorCode
            };
        }
        catch
        {
            return null;
        }
    }

    public static bool IsValidErrorCode(string code)
    {
        if (string.IsNullOrEmpty(code) || code.Length > 32) return false;

        if (code.StartsWith("COM_0x", StringComparison.Ordinal) && code.Length == 14)
        {
            for (int i = 6; i < 14; i++)
            {
                char c = code[i];
                if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }
            return true;
        }

        if (code.StartsWith("0x", StringComparison.Ordinal) && code.Length == 10)
        {
            for (int i = 2; i < 10; i++)
            {
                char c = code[i];
                if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }
            return true;
        }

        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i];
            if (!((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_'))
            {
                return false;
            }
        }
        return true;
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
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static async Task<string?> ReadLineWithLimitAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[1];
        int totalWireBytes = 0;

        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(0, 1), ct).ConfigureAwait(false);
            }
            catch (IOException ex) when (IsCleanEof(ms) && IsPipeClosedException(ex))
            {
                return null;
            }

            if (read == 0)
            {
                if (IsCleanEof(ms))
                {
                    return null;
                }
                throw new EndOfStreamException("Unexpected end of stream before newline delimiter.");
            }

            totalWireBytes++;
            if (totalWireBytes > maxBytes)
            {
                throw new InvalidDataException($"Protocol frame exceeded maximum size of {maxBytes} bytes.");
            }

            byte b = buffer[0];
            if (b == (byte)'\n')
            {
                break;
            }

            ms.WriteByte(b);
        }

        if (ms.Length > 0 && ms.GetBuffer()[ms.Length - 1] == (byte)'\r')
        {
            ms.SetLength(ms.Length - 1);
        }

        return DecodeFrameBytes(ms);
    }

    public static string? ReadLineWithLimit(Stream stream, int maxBytes)
    {
        using var ms = new MemoryStream();
        int totalWireBytes = 0;

        while (true)
        {
            int b;
            try
            {
                b = stream.ReadByte();
            }
            catch (IOException ex) when (IsCleanEof(ms) && IsPipeClosedException(ex))
            {
                return null;
            }

            if (b == -1)
            {
                if (IsCleanEof(ms))
                {
                    return null;
                }
                throw new EndOfStreamException("Unexpected end of stream before newline delimiter.");
            }

            totalWireBytes++;
            if (totalWireBytes > maxBytes)
            {
                throw new InvalidDataException($"Protocol frame exceeded maximum size of {maxBytes} bytes.");
            }

            if (b == '\n')
            {
                break;
            }

            ms.WriteByte((byte)b);
        }

        if (ms.Length > 0 && ms.GetBuffer()[ms.Length - 1] == (byte)'\r')
        {
            ms.SetLength(ms.Length - 1);
        }

        return DecodeFrameBytes(ms);
    }

    public static bool IsPipeClosedException(IOException ex)
    {
        const int ERROR_BROKEN_PIPE = unchecked((int)0x8007006D);
        const int ERROR_PIPE_NOT_CONNECTED = unchecked((int)0x800700E8);
        return ex.HResult == ERROR_BROKEN_PIPE || ex.HResult == ERROR_PIPE_NOT_CONNECTED;
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

        try
        {
            return StrictUtf8.GetString(array, offset, count);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("Invalid UTF-8 sequence in protocol frame.", ex);
        }
    }
}
