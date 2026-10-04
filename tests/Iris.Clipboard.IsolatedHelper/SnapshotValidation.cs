using System.Runtime.InteropServices;
using Iris.Platform.Windows.Clipboard;

namespace Iris.Clipboard.IsolatedHelper;

internal static class SnapshotValidation
{
    public static bool Skipped { get; private set; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormatW(string name);

    public static (bool Success, string? ErrorMessage) Run(IIsolatedStationEnvironment environment) =>
        RunAsync(environment).GetAwaiter().GetResult(); // Helper's dedicated coordinator thread, never an App/UI thread.

    private static async Task<(bool Success, string? ErrorMessage)> RunAsync(IIsolatedStationEnvironment environment)
    {
        // Executed only after the coordinator created a new CWF_CREATE_ONLY station
        // and bound its own desktop. The service worker must bind that same desktop
        // before creating ANY HWND; a new thread does not inherit this binding.
        nint desktop = environment.GetThreadDesktop(environment.GetCurrentThreadId());
        int bindingError = 0;
        var native = new NativeClipboardSnapshotAdapter(() =>
        {
            if (!Program.SetThreadDesktop(desktop))
            {
                bindingError = Marshal.GetLastWin32Error();
                throw new InvalidOperationException("WORKER_DESKTOP_BIND_FAILED");
            }
        });
        var service = new ClipboardSnapshotService(native);
        Exception? body = null;
        Exception? cleanup = null;
        ClipboardSnapshot? snapshot = null;
        ClipboardSnapshot? recaptured = null;
        try
        {
            // Seed synchronously on the coordinator's already-bound thread, before
            // the first await can move this helper continuation to the thread pool.
            var expected = Seed();
            await service.ReadVersionAsync().ConfigureAwait(false);
            ClipboardSnapshotResult captured = await service.CaptureSnapshotAsync().ConfigureAwait(false);
            if (captured.Status == ClipboardSnapshotStatus.Changed)
                captured = await service.CaptureSnapshotAsync().ConfigureAwait(false);
            snapshot = captured.Snapshot;
            if (captured.Status != ClipboardSnapshotStatus.Success || snapshot == null)
                throw new InvalidOperationException("SNAPSHOT_CAPTURE_FAILED");
            using (var lease = snapshot.Borrow(service)) Validate(lease, expected);
            var cleared = await service.TryClearAsync(captured.Version).ConfigureAwait(false);
            if (cleared.Status != ClipboardSnapshotStatus.Applied) throw new InvalidOperationException("CONDITIONAL_CLEAR_FAILED");
            var changed = await service.TryRestoreAsync(snapshot, captured.Version).ConfigureAwait(false);
            if (changed.Status != ClipboardSnapshotStatus.Changed) throw new InvalidOperationException("STALE_VERSION_ACCEPTED");
            var restored = await service.TryRestoreAsync(snapshot, cleared.Version).ConfigureAwait(false);
            if (restored.Status != ClipboardSnapshotStatus.Applied) throw new InvalidOperationException("CONDITIONAL_RESTORE_FAILED");
            var final = await service.CaptureSnapshotAsync().ConfigureAwait(false);
            recaptured = final.Snapshot;
            if (final.Status != ClipboardSnapshotStatus.Success || recaptured == null) throw new InvalidOperationException("RECAPTURE_FAILED");
            using (var lease = recaptured.Borrow(service)) Validate(lease, expected);
            environment.WriteLine("[SNAPSHOTS] PASS: raw formats, conditional clear/restore, stale rejection, worker desktop binding");
        }
        catch (Exception ex) { body = ex; }
        finally
        {
            snapshot?.Dispose(); recaptured?.Dispose();
            try { await service.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { cleanup = ex; }
        }
        if (bindingError != 0 && cleanup == null)
        {
            Skipped = true;
            environment.WriteLine($"[ISOLATED_CLIPBOARD] SKIP: Snapshot worker desktop binding failed (error={bindingError})");
            return (true, null);
        }
        if (body != null || cleanup != null)
            return (false, $"SNAPSHOT_FAILURE body=0x{body?.HResult ?? 0:X8} cleanup=0x{cleanup?.HResult ?? 0:X8}");
        return (true, null);
    }

    private static void Validate(ClipboardSnapshot.Lease lease, Dictionary<uint, byte[]> expected)
    {
        foreach (var pair in expected)
        {
            int index = -1;
            for (int i = 0; i < lease.Count; i++) if (lease.FormatAt(i) == pair.Key) index = i;
            if (index < 0 || !lease.BytesAt(index).SequenceEqual(pair.Value)) throw new InvalidOperationException("RAW_BYTES_DIFFER");
        }
    }

    private static Dictionary<uint, byte[]> Seed()
    {
        var native = NativeClipboardAdapter.Instance;
        var expected = new Dictionary<uint, byte[]>();
        foreach (string name in new[] { "HTML Format", "Rich Text Format", "PNG" })
        {
            uint format = RegisterClipboardFormatW(name);
            if (format == 0) throw new InvalidOperationException("REGISTER_FORMAT_FAILED");
            expected.Add(format, [0, 17, 255, 128, 0, 23, 0, 0]);
        }
        nint window = 0;
        bool opened = false;
        var owned = new List<(nint Handle, bool Locked)>();
        var failures = new List<Exception>();
        try
        {
            window = native.CreateOwnerWindow();
            if (window == 0 || !native.OpenClipboard(window)) throw new InvalidOperationException("SEED_OPEN_FAILED");
            opened = true;
            if (!native.EmptyClipboard()) throw new InvalidOperationException("SEED_EMPTY_FAILED");
            foreach (var pair in expected)
            {
                nint handle = native.GlobalAlloc(2, (nuint)pair.Value.Length);
                if (handle == 0) throw new InvalidOperationException("SEED_ALLOC_FAILED");
                owned.Add((handle, false));
                nint pointer = native.GlobalLock(handle);
                if (pointer == 0) throw new InvalidOperationException("SEED_LOCK_FAILED");
                owned[^1] = (handle, true);
                Marshal.Copy(pair.Value, 0, pointer, pair.Value.Length);
                if (!native.GlobalUnlock(handle) && native.GetLastError() != 0) throw new InvalidOperationException("SEED_UNLOCK_FAILED");
                owned[^1] = (handle, false);
                if (native.SetClipboardData(pair.Key, handle) == 0) throw new InvalidOperationException("SEED_SET_FAILED");
                owned.RemoveAt(owned.Count - 1);
            }
        }
        catch (Exception ex) { failures.Add(ex); }
        finally
        {
            foreach (var block in owned)
            {
                bool unlocked = !block.Locked || native.GlobalUnlock(block.Handle) || native.GetLastError() == 0;
                if (!unlocked) failures.Add(new InvalidOperationException("SEED_CLEANUP_UNLOCK_FAILED"));
                else if (native.GlobalFree(block.Handle) != 0) failures.Add(new InvalidOperationException("SEED_FREE_FAILED"));
            }
            if (opened && !native.CloseClipboard()) failures.Add(new InvalidOperationException("SEED_CLOSE_FAILED"));
            if (window != 0 && !native.DestroyWindow(window)) failures.Add(new InvalidOperationException("SEED_DESTROY_FAILED"));
        }
        if (failures.Count != 0) throw new AggregateException(failures);
        return expected;
    }
}
