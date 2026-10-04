using System.Buffers.Binary;
using System.Text;

namespace Iris.Platform.Windows.Clipboard;

internal enum ClipboardTextStatus { Success, Empty, Changed, TooLong, Invalid }
internal sealed record ClipboardTextResult(ClipboardTextStatus Status, ClipboardVersion Version, string? Text = null)
{
    public override string ToString() => $"ClipboardTextResult Status={Status}";
}

// Retained by the selection transaction even when Close or a later Set fails.
// A version only authorizes a conditional retry, never an unconditional overwrite.
internal sealed class ClipboardWriteReceipt
{
    public bool Written { get; set; }
    public ClipboardVersion? Version { get; set; }
    // Publication is a fact independent of version observation and frame cleanup.
    public bool AllFormatsPublished { get; set; }
    public override string ToString() => $"ClipboardWriteReceipt Published={AllFormatsPublished}";
}

internal sealed partial class ClipboardSnapshotService
{
    public Task<ClipboardSnapshotResult> TryClearForSelectionAsync(ClipboardVersion expected, ClipboardWriteReceipt receipt, CancellationToken token) =>
        Submit(ct => Commit(null, expected, ct, receipt), token);

    public Task<ClipboardSnapshotResult> TryRestoreForSelectionAsync(ClipboardSnapshot snapshot, ClipboardVersion expected,
        ClipboardWriteReceipt receipt, CancellationToken token)
    {
        var lease = snapshot.Borrow(this);
        return Submit(ct => Commit(lease, expected, ct, receipt), token, lease);
    }

    public Task<ClipboardTextResult> ReadTextIfVersionAsync(ClipboardVersion expected, CancellationToken cancellationToken = default) =>
        Submit(token => ReadConditionalText(expected, token), cancellationToken);

    private ClipboardTextResult ReadConditionalText(ClipboardVersion expected, CancellationToken token)
    {
        Open(token);
        var before = ReadVersion();
        if (before != expected) return new(ClipboardTextStatus.Changed, before);
        if (!_native.IsClipboardFormatAvailable(13)) return new(ClipboardTextStatus.Empty, before);
        nint handle = _native.GetClipboardData(13);
        Check(handle != 0, "GET_TEXT");
        nuint size = _native.GlobalSize(handle);
        if (size == 0 || (size & 1) != 0) return new(ClipboardTextStatus.Invalid, before);
        int count = checked((int)Math.Min((ulong)size, (65_536UL + 1) * 2));
        var bytes = new byte[count];
        try
        {
            var block = new MemoryBlock(handle, false);
            nint pointer = _native.GlobalLock(handle);
            Check(pointer != 0, "LOCK_TEXT");
            block.Locked = true;
            _blocks.Add(block);
            _native.CopyFrom(pointer, bytes);
            Unlock(block);
            _blocks.Remove(block);
            var after = ReadVersion();
            CheckCancellation(token);
            if (before != after) return new(ClipboardTextStatus.Changed, after);
            int terminator = -1;
            for (int i = 0; i < count; i += 2)
                if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i, 2)) == 0) { terminator = i; break; }
            if (terminator < 0)
                return new(count / 2 > 65_536 ? ClipboardTextStatus.TooLong : ClipboardTextStatus.Invalid, after);
            string text;
            try { text = new UnicodeEncoding(false, false, true).GetString(bytes, 0, terminator); }
            catch (DecoderFallbackException) { return new(ClipboardTextStatus.Invalid, after); }
            return string.IsNullOrWhiteSpace(text) ? new(ClipboardTextStatus.Empty, after) : new(ClipboardTextStatus.Success, after, text);
        }
        finally { Array.Clear(bytes); }
    }
}
