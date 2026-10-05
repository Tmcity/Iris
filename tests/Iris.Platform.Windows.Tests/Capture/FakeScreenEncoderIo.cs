using Iris.Core.Imaging;
using Iris.Platform.Windows.Capture;

namespace Iris.Platform.Windows.Tests.Capture;

internal sealed class FakeScreenEncoderFactory : IScreenEncoderFactory
{
    internal readonly List<FakeScreenEncoderIo> Sessions = [];
    internal Action<FakeScreenEncoderIo>? Configure { get; set; }
    public IScreenEncoderIo Create() { var io = new FakeScreenEncoderIo(); Configure?.Invoke(io); Sessions.Add(io); return io; }
}

internal sealed class FakeScreenEncoderIo : IScreenEncoderIo
{
    internal readonly List<string> Calls = [];
    internal readonly Dictionary<string, int> Failures = [];
    internal Func<string, Task>? Before { get; set; }
    internal ulong OutputSize { get; set; } = 4;
    internal uint? ReadLength { get; set; }
    internal bool StreamOwned, ReaderOwned;
    internal int InFlight;
    internal byte[]? Pixels;
    internal float Quality;
    internal ImageFormat Format;
    internal int Width, Height;
    internal bool ThrowOnCopy { get; set; }
    private async Task Call(string stage)
    {
        Calls.Add(stage); InFlight++;
        try
        {
            if (Before != null) await Before(stage);
            if (Failures.TryGetValue(stage, out int count) && count != 0)
            { if (count > 0) Failures[stage] = count - 1; throw new IOException("private codec payload"); }
        }
        finally { InFlight--; }
    }
    public Task CreateAsync(ImageFormat format, float jpegQuality)
    { StreamOwned = true; Format = format; Quality = jpegQuality; return Call("Create"); }
    public void SetPixels(byte[] pixels, int width, int height) { Calls.Add("Pixels"); Pixels = pixels; Width = width; Height = height; }
    public Task FlushAsync() => Call("Flush");
    public ulong Size { get { Calls.Add("Size"); return OutputSize; } }
    public async Task<uint> ReadAsync(uint count) { await Call("Read"); return ReadLength ?? count; }
    public void CopyBytes(byte[] target) { Calls.Add("CopyBytes"); ReaderOwned = true; if (ThrowOnCopy) throw new IOException("private copied content"); target.AsSpan().Fill(47); }
    public async ValueTask DisposeReaderAsync()
    {
        if (InFlight != 0) throw new InvalidOperationException("disposing active codec");
        await Call("DisposeReader"); ReaderOwned = false;
    }
    public async ValueTask DisposeStreamAsync()
    {
        if (InFlight != 0) throw new InvalidOperationException("disposing active codec");
        await Call("DisposeStream"); StreamOwned = false;
    }
}
