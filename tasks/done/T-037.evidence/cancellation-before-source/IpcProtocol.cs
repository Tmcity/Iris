using System.Text;
using System.Text.Json;
using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Serialization;

namespace Iris.Platform.Windows.SystemIntegration;

internal enum IpcReply { Accepted, Invalid, Busy, Stopping }
internal enum IpcSendResult { Accepted, Rejected, Unavailable, Unconfirmed }

internal static class IpcProtocol
{
    internal const int MaxFrameBytes = 256 * 1024;
    internal const int MaxReplyBytes = 64;
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Options = new(IrisJson.Options) { WriteIndented = false };

    internal static bool TryAction(string? name, out IrisAction action)
    {
        action = default;
        return name != null && Enum.GetNames<IrisAction>().Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase))
            && Enum.TryParse(name, true, out action);
    }
    internal static bool IsValid(IpcCommand? command) => command != null && Enum.IsDefined(command.Action)
        && (command.Action == IrisAction.TranslateText ? !string.IsNullOrWhiteSpace(command.Payload) : string.IsNullOrEmpty(command.Payload));

    internal static byte[] Encode(IpcCommand command)
    {
        if (!IsValid(command) || command.Payload?.Length > MaxFrameBytes) throw Invalid();
        try
        {
            if (command.Payload != null) _ = Utf8.GetByteCount(command.Payload);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(command, Options);
            if (bytes.Length > MaxFrameBytes) throw Invalid();
            var frame = new byte[bytes.Length + 1]; bytes.CopyTo(frame, 0); frame[^1] = (byte)'\n';
            return frame;
        }
        catch (EncoderFallbackException) { throw Invalid(); }
    }
    internal static IpcCommand Decode(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxFrameBytes || bytes.Span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) throw Invalid();
        try
        {
            _ = Utf8.GetCharCount(bytes.Span);
            using var document = JsonDocument.Parse(bytes, new() { CommentHandling = Options.ReadCommentHandling, AllowTrailingCommas = Options.AllowTrailingCommas });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IrisAction action = default; string? payload = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid();
                if (property.Name.Equals("action", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind != JsonValueKind.String || !TryAction(property.Value.GetString(), out action)) throw Invalid();
                }
                else if (property.Name.Equals("payload", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind is not JsonValueKind.String and not JsonValueKind.Null) throw Invalid();
                    payload = property.Value.GetString();
                }
                else throw Invalid();
            }
            var command = new IpcCommand(action, payload);
            if (!names.Contains("action") || !IsValid(command)) throw Invalid();
            // Also reject unpaired escaped surrogates rather than changing the caller's text.
            if (payload != null) _ = Utf8.GetByteCount(payload);
            return command;
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or EncoderFallbackException or InvalidOperationException)
        { throw Invalid(); }
    }
    internal static async Task<byte[]> ReadLineAsync(Stream stream, int budget, CancellationToken cancellationToken)
    {
        // Fixed capacity includes optional CR + LF. A second command in the same read is discarded.
        var buffer = new byte[budget + 2]; int count = 0;
        while (count < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(count, Math.Min(4096, buffer.Length - count)), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw Invalid();
            int end = Array.IndexOf(buffer, (byte)'\n', count, read);
            count += read;
            if (end < 0) continue;
            if (end > 0 && buffer[end - 1] == (byte)'\r') end--;
            if (end > budget) throw Invalid();
            return buffer.AsSpan(0, end).ToArray();
        }
        throw Invalid();
    }
    internal static byte[] ReplyBytes(IpcReply reply) => Encoding.ASCII.GetBytes(reply switch
    { IpcReply.Accepted => "accepted\n", IpcReply.Invalid => "invalid\n", IpcReply.Busy => "busy\n", _ => "stopping\n" });
    internal static IpcSendResult DecodeReply(ReadOnlySpan<byte> bytes) => bytes.SequenceEqual("accepted"u8) ? IpcSendResult.Accepted
        : bytes.SequenceEqual("invalid"u8) || bytes.SequenceEqual("busy"u8) || bytes.SequenceEqual("stopping"u8) ? IpcSendResult.Rejected : IpcSendResult.Unconfirmed;
    internal static IrisException Invalid() => new(ErrorCode.InvalidArgument, ErrorMessages.GetDefault(ErrorCode.InvalidArgument));
    internal static IrisException Unavailable() => new(ErrorCode.NetworkUnavailable, ErrorMessages.GetDefault(ErrorCode.NetworkUnavailable));
}
