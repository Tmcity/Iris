using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Iris.Maintenance;

internal static class InstallerEnvelope
{
    internal const int FooterSize = 56;
    internal static ReadOnlySpan<byte> Magic => "IRIS-INSTALL-V1!"u8;
    internal static async Task<bool> HasPayloadAsync(string executable, CancellationToken token)
    {
        await using var stream = File.OpenRead(executable);
        if (stream.Length < FooterSize) return false;
        var footer = new byte[FooterSize]; stream.Position = stream.Length - FooterSize;
        await stream.ReadExactlyAsync(footer, token).ConfigureAwait(false);
        return footer.AsSpan(0, 16).SequenceEqual(Magic);
    }
    internal static async Task ExtractAsync(string executable, string destination, CancellationToken token)
    {
        await using var input = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (input.Length < FooterSize) throw new InvalidDataException("Invalid installer envelope.");
        byte[] footer = new byte[FooterSize]; input.Position = input.Length - FooterSize;
        await input.ReadExactlyAsync(footer, token).ConfigureAwait(false);
        long length = BinaryPrimitives.ReadInt64LittleEndian(footer.AsSpan(16, 8));
        if (!footer.AsSpan(0, 16).SequenceEqual(Magic) || length is <= 0 or > 1024L * 1024 * 1024 || length > input.Length - FooterSize - 4096)
            throw new InvalidDataException("Invalid installer bounds.");
        input.Position = input.Length - FooterSize - length;
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); byte[] buffer = new byte[65536];
        while (length > 0)
        {
            token.ThrowIfCancellationRequested(); int count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(length, buffer.Length)), token).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException();
            hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false); length -= count;
        }
        if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), footer.AsSpan(24, 32))) throw new InvalidDataException("Installer checksum mismatch.");
        await output.FlushAsync(token).ConfigureAwait(false);
    }
}
