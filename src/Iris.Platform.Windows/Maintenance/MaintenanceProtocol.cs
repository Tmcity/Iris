using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using Windows.Win32;
using Iris.Platform.Windows.SystemIntegration;

namespace Iris.Platform.Windows.Maintenance;

internal sealed record MaintenanceMessage(string Kind, string Token, string? File = null, string? Sha256 = null, long Size = 0, string? Version = null)
{
    public override string ToString() => "MaintenanceMessage";
}
internal static class MaintenanceProtocol
{
    internal static async Task WriteAsync(Stream stream, MaintenanceMessage message, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message, ReleaseIdentity.Json);
        if (bytes.Length > 8192) throw ReleaseIdentity.Invalid();
        byte[] prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        await stream.WriteAsync(bytes, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
    }
    internal static async Task<MaintenanceMessage> ReadAsync(Stream stream, CancellationToken token)
    {
        byte[] prefix = new byte[4]; token.ThrowIfCancellationRequested();
        await ReadExactlyAsync(stream, prefix, token).ConfigureAwait(false);
        int size = BinaryPrimitives.ReadInt32LittleEndian(prefix); if (size is <= 0 or > 8192) throw ReleaseIdentity.Invalid();
        byte[] bytes = new byte[size];
        await ReadExactlyAsync(stream, bytes, token).ConfigureAwait(false);
        return ReleaseIdentity.Parse<MaintenanceMessage>(bytes);
    }
    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> bytes, CancellationToken token)
    {
        while (!bytes.IsEmpty)
        {
            token.ThrowIfCancellationRequested();
            int count = await stream.ReadAsync(bytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (count == 0) throw new EndOfStreamException();
            bytes = bytes[count..];
        }
    }
    internal static void VerifyPeer(PipeStream pipe, bool server, int expectedProcessId)
    {
        new IpcPipe(pipe).VerifyPeer(server);
        uint id;
        bool valid = server ? PInvoke.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out id) : PInvoke.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out id);
        if (!valid || id != expectedProcessId) throw ReleaseIdentity.Invalid();
    }
}
