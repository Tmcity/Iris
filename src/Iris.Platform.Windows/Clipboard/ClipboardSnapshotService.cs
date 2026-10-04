using Iris.Core.Errors;

namespace Iris.Platform.Windows.Clipboard;

internal sealed partial class ClipboardSnapshotService : IDisposable, IAsyncDisposable
{
    internal const int MaxFormats = 64;
    internal const int MaxFormatBytes = 33_554_432;
    internal const long MaxSnapshotBytes = 67_108_864;
    private readonly IClipboardSnapshotNative _native;
    private readonly List<MemoryBlock> _blocks = [];
    private nint _window;
    private bool _frameOpen;
    private bool _commitStarted;

    public ClipboardSnapshotService(IClipboardSnapshotNative native) => _native = native;

    public Task<ClipboardSnapshotResult> CaptureSnapshotAsync(CancellationToken cancellationToken = default) =>
        Submit(Capture, cancellationToken);

    public Task<ClipboardVersion> ReadVersionAsync(CancellationToken cancellationToken = default) =>
        Submit(token => { Open(token); var version = ReadVersion(); CheckCancellation(token); return version; }, cancellationToken);

    public Task<ClipboardSnapshotResult> TryClearAsync(ClipboardVersion expectedVersion, CancellationToken cancellationToken = default) =>
        Submit(token => Commit(null, expectedVersion, token), cancellationToken);

    public Task<ClipboardSnapshotResult> TryRestoreAsync(ClipboardSnapshot snapshot, ClipboardVersion expectedVersion, CancellationToken cancellationToken = default)
    {
        if (snapshot == null) throw new IrisException(ErrorCode.InvalidArgument, "SNAPSHOT_INVALID");
        // Borrow before queuing, not when the worker eventually executes.
        var lease = snapshot.Borrow(this);
        return Submit(token => Commit(lease, expectedVersion, token), cancellationToken, lease);
    }

    private ClipboardSnapshotResult Capture(CancellationToken token)
    {
        Open(token);
        var before = ReadVersion();
        var formats = Enumerate();
        if (formats == null) return new(ClipboardSnapshotStatus.TooLarge, before);
        foreach (uint format in formats)
            if (!IsSupported(format)) return new(ClipboardSnapshotStatus.UnsupportedFormat, before);

        var data = new List<(uint Format, byte[] Bytes)>();
        bool transferred = false;
        try
        {
            long total = 0;
            foreach (uint format in formats)
            {
                CheckCancellation(token);
                nint handle = _native.GetClipboardData(format);
                Check(handle != 0, "GET_DATA");
                nuint size = _native.GlobalSize(handle);
                Check(size != 0, "GLOBAL_SIZE");
                if (size > MaxFormatBytes) return new(ClipboardSnapshotStatus.TooLarge, before);
                total = checked(total + (long)size);
                if (total > MaxSnapshotBytes) return new(ClipboardSnapshotStatus.TooLarge, before);
                var bytes = new byte[checked((int)size)];
                data.Add((format, bytes));
                var block = new MemoryBlock(handle, false);
                nint pointer = _native.GlobalLock(handle);
                Check(pointer != 0, "GLOBAL_LOCK");
                block.Locked = true;
                _blocks.Add(block);
                _native.CopyFrom(pointer, bytes);
                Unlock(block);
                _blocks.Remove(block);
            }
            var afterFormats = Enumerate();
            var after = ReadVersion();
            CheckCancellation(token);
            if (before != after || afterFormats == null || !formats.SequenceEqual(afterFormats))
                return new(ClipboardSnapshotStatus.Changed, after);
            var snapshot = new ClipboardSnapshot(this, before, data);
            transferred = true;
            return new(ClipboardSnapshotStatus.Success, before, snapshot);
        }
        finally
        {
            if (!transferred) foreach (var item in data) Array.Clear(item.Bytes);
        }
    }

    private ClipboardSnapshotResult Commit(ClipboardSnapshot.Lease? lease, ClipboardVersion expected, CancellationToken token)
    {
        var prepared = new List<(uint Format, MemoryBlock Block)>();
        if (lease != null)
        {
            for (int i = 0; i < lease.Count; i++)
            {
                CheckCancellation(token);
                var bytes = lease.BytesAt(i);
                nint handle = _native.GlobalAlloc(0x2, checked((nuint)bytes.Length)); // GMEM_MOVEABLE
                Check(handle != 0, "GLOBAL_ALLOC");
                var block = new MemoryBlock(handle, true);
                _blocks.Add(block);
                prepared.Add((lease.FormatAt(i), block));
                nint pointer = _native.GlobalLock(handle);
                Check(pointer != 0, "GLOBAL_LOCK");
                block.Locked = true;
                _native.CopyTo(bytes, pointer);
                Unlock(block);
            }
        }
        Open(token);
        var current = ReadVersion();
        if (current != expected) return new(ClipboardSnapshotStatus.Changed, current);
        CheckCancellation(token);
        Check(_native.EmptyClipboard(), "EMPTY");
        _commitStarted = true;
        // Past the commit point: cancellation cannot abandon remaining Set/cleanup.
        foreach (var (format, block) in prepared)
        {
            Check(_native.SetClipboardData(format, block.Handle) != 0, "SET_DATA");
            _blocks.Remove(block); // Ownership transferred; never touch this handle again.
        }
        return new(ClipboardSnapshotStatus.Applied, ReadVersion());
    }

    private void Open(CancellationToken token)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            CheckCancellation(token);
            if (_native.OpenClipboard(_window)) { _frameOpen = true; return; }
            if (attempt < 9 && token.WaitHandle.WaitOne(30)) CheckCancellation(token);
        }
        CheckCancellation(token);
        throw new NativeFailure(ErrorCode.ClipboardBusy, "OPEN_BUSY", 0);
    }

    private ClipboardVersion ReadVersion()
    {
        uint sequence = _native.GetClipboardSequenceNumber();
        nint owner = _native.GetOwner();
        uint pid = owner == 0 ? 0 : _native.GetOwnerProcessId(owner);
        Check(owner == 0 || pid != 0, "OWNER_ID");
        return new(sequence, owner, pid);
    }

    private List<uint>? Enumerate()
    {
        var formats = new List<uint>();
        uint previous = 0;
        while (true)
        {
            uint next = _native.EnumFormats(previous);
            if (next == 0) { Check(_native.GetLastError() == 0, "ENUM_FORMATS"); return formats; }
            if (formats.Count == MaxFormats) return null;
            if (formats.Contains(next)) throw Safe("ENUM_DUPLICATE", 0);
            formats.Add(next);
            previous = next;
        }
    }

    private bool IsSupported(uint format)
    {
        if (format is 1 or 7 or 8 or 13 or 15 or 16 or 17) return true;
        if (format < 0xC000 || format > 0xFFFF) return false;
        return _native.GetFormatName(format) is "HTML Format" or "Rich Text Format" or "PNG";
    }

    private void Unlock(MemoryBlock block)
    {
        try
        {
            bool remainsLocked = _native.GlobalUnlock(block.Handle);
            Check(remainsLocked || _native.GetLastError() == 0, "GLOBAL_UNLOCK");
            block.Locked = false;
            block.UnlockFailed = false;
        }
        catch { block.UnlockFailed = true; throw; }
    }

    private void CleanupResources(List<string> failures, bool retry = false)
    {
        foreach (var block in _blocks.ToArray())
        {
            if (block.Locked)
            {
                if (block.UnlockFailed && !retry) failures.Add("UNLOCK_PENDING");
                else Attempt(() => Unlock(block), "UNLOCK", failures);
            }
            if (!block.Locked && block.Owned)
                Attempt(() => { Check(_native.GlobalFree(block.Handle) == 0, "GLOBAL_FREE"); _blocks.Remove(block); }, "FREE", failures);
            else if (!block.Locked) _blocks.Remove(block);
        }
        // A borrowed locked handle must remain inside its Open frame until unlocked.
        if (_frameOpen && !_blocks.Any(b => !b.Owned && b.Locked))
            Attempt(() => { Check(_native.CloseClipboard(), "CLOSE"); _frameOpen = false; }, "CLOSE", failures);
    }

    private void Check(bool success, string stage)
    {
        if (!success) throw Safe(stage, _native.GetLastError());
    }
    private static NativeFailure Safe(string stage, int code) => new(ErrorCode.CaptureFailed, stage, code);
    private static string Describe(Exception exception) => exception is NativeFailure known ? known.Message : $"NATIVE:0x{exception.HResult:X8}";
    private static void Attempt(Action action, string stage, List<string> failures)
    {
        try { action(); }
        catch (Exception ex) { failures.Add($"{stage}:{Describe(ex)}"); }
    }
    private sealed class NativeFailure(ErrorCode errorCode, string stage, int nativeCode)
        : IrisException(errorCode, $"CLIPBOARD_{stage} code=0x{nativeCode:X8}");
    private sealed class MemoryBlock(nint handle, bool owned)
    {
        public nint Handle { get; } = handle;
        public bool Owned { get; } = owned;
        public bool Locked { get; set; }
        public bool UnlockFailed { get; set; }
    }
}
