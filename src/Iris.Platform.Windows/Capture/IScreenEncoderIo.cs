using Iris.Core.Imaging;

namespace Iris.Platform.Windows.Capture;

internal interface IScreenEncoderFactory { IScreenEncoderIo Create(); }
internal interface IScreenEncoderIo
{
    Task CreateAsync(ImageFormat format, float jpegQuality);
    void SetPixels(byte[] pixels, int width, int height);
    Task FlushAsync();
    ulong Size { get; }
    Task<uint> ReadAsync(uint count);
    void CopyBytes(byte[] target);
    ValueTask DisposeReaderAsync();
    ValueTask DisposeStreamAsync();
}
