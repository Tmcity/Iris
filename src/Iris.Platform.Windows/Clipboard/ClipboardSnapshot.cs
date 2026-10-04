using Iris.Core.Errors;

namespace Iris.Platform.Windows.Clipboard;

internal readonly record struct ClipboardVersion(uint Sequence, nint Owner, uint OwnerProcessId)
{
    public override string ToString() => "ClipboardVersion";
}

internal enum ClipboardSnapshotStatus { Success, UnsupportedFormat, TooLarge, Changed, Applied }

internal sealed record ClipboardSnapshotResult(ClipboardSnapshotStatus Status, ClipboardVersion Version, ClipboardSnapshot? Snapshot = null)
{
    public override string ToString() => $"ClipboardSnapshotResult Status={Status}";
}

internal sealed class ClipboardSnapshot : IDisposable
{
    private readonly object _gate = new();
    private readonly object _owner;
    private readonly List<(uint Format, byte[] Bytes)> _data;
    private bool _disposed;
    private int _borrowers;
    public ClipboardVersion Version { get; }
    public int FormatCount { get; }

    internal ClipboardSnapshot(object owner, ClipboardVersion version, List<(uint Format, byte[] Bytes)> data)
    {
        _owner = owner;
        Version = version;
        _data = data;
        FormatCount = data.Count;
    }

    internal Lease Borrow(object owner)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(owner, _owner))
                throw new IrisException(ErrorCode.InvalidArgument, "SNAPSHOT_INVALID");
            _borrowers++;
            return new Lease(this);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            ClearIfUnused();
        }
    }

    private void ClearIfUnused()
    {
        if (!_disposed || _borrowers != 0) return;
        foreach (var item in _data) Array.Clear(item.Bytes);
        _data.Clear();
    }

    public override string ToString() => $"ClipboardSnapshot Formats={FormatCount}";

    internal sealed class Lease(ClipboardSnapshot snapshot) : IDisposable
    {
        private ClipboardSnapshot? _snapshot = snapshot;
        public int Count => _snapshot!._data.Count;
        public uint FormatAt(int index) => _snapshot!._data[index].Format;
        public ReadOnlySpan<byte> BytesAt(int index) => _snapshot!._data[index].Bytes;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _snapshot, null);
            if (current == null) return;
            lock (current._gate)
            {
                current._borrowers--;
                current.ClearIfUnused();
            }
        }
        public override string ToString() => "ClipboardSnapshotLease";
    }
}
