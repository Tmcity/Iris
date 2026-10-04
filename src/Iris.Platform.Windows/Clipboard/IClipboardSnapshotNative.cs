namespace Iris.Platform.Windows.Clipboard;

// Only external Win32/transport boundaries; scheduling and ownership stay in the service.
internal interface IClipboardSnapshotNative : INativeClipboardAdapter
{
    uint EnumFormats(uint previous);
    string GetFormatName(uint format);
    nint GetOwner();
    uint GetOwnerProcessId(nint owner);
    void CopyFrom(nint pointer, Span<byte> destination);
    void CopyTo(ReadOnlySpan<byte> source, nint pointer);
    void PumpMessages();
    void WaitForWork(WaitHandle work);
}
