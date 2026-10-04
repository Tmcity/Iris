using Iris.Platform.Windows.Clipboard;

namespace Iris.Platform.Windows.Tests.Clipboard.TestSupport;

internal sealed class FakeClipboardSnapshotNative : IClipboardSnapshotNative
{
    internal sealed class Block(byte[] bytes, bool borrowed)
    {
        public byte[] Bytes { get; } = bytes;
        public bool Borrowed { get; } = borrowed;
        public bool Transferred { get; set; }
        public bool Freed { get; set; }
        public int Locks { get; set; }
        public nuint? Size { get; set; }
    }
    private int _error;
    private nint _next = 100;
    public readonly Dictionary<nint, Block> Memory = [];
    public readonly List<(uint Format, nint Handle)> Formats = [];
    public readonly Dictionary<uint, string> Names = [];
    public readonly Dictionary<string, int> Failures = [];
    public readonly System.Collections.Concurrent.ConcurrentQueue<(string Api, int Thread, ApartmentState Apartment)> Calls = [];
    public readonly System.Collections.Concurrent.ConcurrentQueue<string> FailedCalls = [];
    public Action<string>? Before { get; set; }
    public Action<string>? After { get; set; }
    public ClipboardVersion Version { get; set; } = new(0, 0, 0);
    public bool IsOpen { get; private set; }
    public bool WindowAlive { get; private set; }
    public int OwnershipViolations { get; private set; }
    public int Count(string api) => Calls.Count(c => c.Api == api);

    public nint Add(uint format, byte[] bytes, nuint? size = null)
    {
        nint handle = ++_next;
        Memory.Add(handle, new Block((byte[])bytes.Clone(), true) { Size = size });
        Formats.Add((format, handle));
        return handle;
    }

    private bool Call(string api)
    {
        Calls.Enqueue((api, Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()));
        Before?.Invoke(api);
        if (Failures.TryGetValue(api, out int remaining) && remaining != 0)
        {
            if (remaining > 0) Failures[api] = remaining - 1;
            _error = 5;
            FailedCalls.Enqueue(api);
            return false;
        }
        _error = 0;
        return true;
    }
    public nint CreateOwnerWindow() { if (!Call("Create")) return 0; WindowAlive = true; return 900; }
    public bool DestroyWindow(nint hwnd) { if (!Call("Destroy")) return false; WindowAlive = false; return true; }
    public bool OpenClipboard(nint hwnd)
    {
        if (!Call("Open")) return false;
        if (IsOpen || !WindowAlive || hwnd != 900) throw new InvalidOperationException("fake frame invariant");
        IsOpen = true;
        After?.Invoke("Open");
        return true;
    }
    public bool CloseClipboard() { if (!Call("Close")) return false; IsOpen = false; return true; }
    public bool EmptyClipboard()
    {
        if (!Call("Empty")) return false;
        if (!IsOpen) throw new InvalidOperationException("fake frame invariant");
        Formats.Clear();
        Version = new(unchecked(Version.Sequence + 1), 900, 42);
        After?.Invoke("Empty");
        return true;
    }
    public nint SetClipboardData(uint format, nint hMem)
    {
        if (!Call("Set")) return 0;
        var block = Memory[hMem];
        if (!IsOpen || block.Locks != 0 || block.Freed || block.Transferred) throw new InvalidOperationException("fake Set invariant");
        block.Transferred = true;
        Formats.Add((format, hMem));
        Version = Version with { Sequence = unchecked(Version.Sequence + 1) };
        After?.Invoke("Set");
        return hMem;
    }
    public nint GetClipboardData(uint format) => Call("Get") ? Formats.First(f => f.Format == format).Handle : 0;
    public bool IsClipboardFormatAvailable(uint format) => Formats.Any(f => f.Format == format);
    public uint GetClipboardSequenceNumber() { Call("Sequence"); RequireOpen(); return Version.Sequence; }
    public nint GetOwner() { Call("Owner"); RequireOpen(); return Version.Owner; }
    public uint GetOwnerProcessId(nint owner) { bool ok = Call("OwnerPid"); RequireOpen(); return ok ? Version.OwnerProcessId : 0; }
    private void RequireOpen() { if (!IsOpen) throw new InvalidOperationException("version outside Open"); }
    public uint EnumFormats(uint previous)
    {
        if (!Call("Enum")) return 0;
        int index = previous == 0 ? 0 : Formats.FindIndex(f => f.Format == previous) + 1;
        return index < Formats.Count ? Formats[index].Format : 0;
    }
    public string GetFormatName(uint format) { if (!Call("Name")) throw new IOException("private path text"); return Names.GetValueOrDefault(format, "Unknown"); }
    public nint GlobalAlloc(uint uFlags, nuint dwBytes)
    {
        if (!Call("Alloc")) return 0;
        if (uFlags != 2) throw new InvalidOperationException("not movable");
        nint handle = ++_next;
        Memory.Add(handle, new Block(new byte[checked((int)dwBytes)], false));
        return handle;
    }
    public nint GlobalFree(nint hMem)
    {
        if (!Call("Free")) return hMem;
        var block = Memory[hMem];
        if (block.Borrowed || block.Transferred || block.Freed || block.Locks != 0)
        {
            OwnershipViolations++;
            throw new InvalidOperationException("fake ownership invariant");
        }
        block.Freed = true;
        return 0;
    }
    public nint GlobalLock(nint hMem) { if (!Call("Lock")) return 0; Memory[hMem].Locks++; return hMem; }
    public bool GlobalUnlock(nint hMem) { if (!Call("Unlock")) return false; Memory[hMem].Locks--; return false; }
    public nuint GlobalSize(nint hMem) { if (!Call("Size")) return 0; return Memory[hMem].Size ?? (nuint)Memory[hMem].Bytes.Length; }
    public int GetLastError() => _error;
    public void CopyFrom(nint pointer, Span<byte> destination)
    {
        if (!Call("CopyFrom")) throw new IOException("private payload path");
        Memory[pointer].Bytes.AsSpan(0, Math.Min(Memory[pointer].Bytes.Length, destination.Length)).CopyTo(destination);
        After?.Invoke("CopyFrom");
    }
    public void CopyTo(ReadOnlySpan<byte> source, nint pointer)
    {
        if (!Call("CopyTo")) throw new IOException("private payload path");
        source.CopyTo(Memory[pointer].Bytes);
    }
    public void PumpMessages() { if (!Call("Pump")) throw new IOException("private pump error"); }
    public void WaitForWork(WaitHandle work)
    {
        if (!Call("Wait")) throw new IOException("private wake error");
        work.WaitOne();
    }
}
