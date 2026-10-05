using System.Windows;
using Iris.App.Hosting;

namespace Iris.App.Capture;

/// <summary>Additional existing content-window owners can participate without creating a window.</summary>
internal interface IScreenshotPreparationParticipant
{
    WindowPreparationIntent.Receipt ObservePreparation(Action invalidated);
}

/// <summary>Owner-owned display version. Receipts contain identities, never window content.</summary>
internal sealed class WindowPreparationIntent(Lock ownerGate)
{
    private readonly Lock _gate = ownerGate;
    private long _version, _visibilityVersion, _shownVersion;
    private Window? _shownWindow;
    private readonly HashSet<Receipt> _receipts = [];

    internal Receipt Observe(Action invalidated, IUiDispatcher ui, Func<Window?> window,
        Func<bool> alive, Func<IDisposable>? suppress = null)
    {
        lock (_gate)
        {
            var receipt = new Receipt(this, _version, invalidated, ui, window, alive, suppress);
            _receipts.Add(receipt);
            return receipt;
        }
    }

    internal Action AdvanceLocked(bool hiding = false)
    {
        _version++;
        if (hiding)
        {
            _visibilityVersion = _version;
            _shownVersion = 0;
            _shownWindow = null;
        }
        var receipts = _receipts.ToArray();
        return () => { foreach (var receipt in receipts) receipt.Invalidated(); };
    }
    internal void Advance(bool hiding = false)
    {
        Action notify;
        lock (_gate) notify = AdvanceLocked(hiding);
        notify();
    }

    internal ShowAttempt BeginShowLocked()
    {
        var notify = AdvanceLocked();
        return new(this, _version, notify);
    }

    internal ShowAttempt BeginShow()
    {
        ShowAttempt attempt;
        lock (_gate) attempt = BeginShowLocked();
        attempt.Notify();
        return attempt;
    }

    /// <summary>Intent acceptance is not evidence of successful visibility.</summary>
    internal sealed class ShowAttempt(WindowPreparationIntent owner, long version, Action notify)
    {
        internal void Notify() => notify();
        internal void Complete(Window? window, bool succeeded)
        {
            bool visible = window?.IsVisible == true; // Called on the window's dispatcher, after cleanup.
            lock (owner._gate)
            {
                // A late attempt cannot revoke a newer successful display or explicit Hide.
                if (version < owner._visibilityVersion) return;
                if (succeeded && visible)
                {
                    owner._visibilityVersion = owner._shownVersion = version;
                    owner._shownWindow = window;
                }
                else if (!visible)
                {
                    owner._visibilityVersion = version;
                    owner._shownVersion = 0;
                    owner._shownWindow = null;
                }
            }
        }
    }

    internal sealed class Receipt(WindowPreparationIntent owner, long version, Action invalidated,
        IUiDispatcher ui, Func<Window?> getWindow, Func<bool> alive, Func<IDisposable>? suppress) : IDisposable
    {
        private Window? _window;
        private IDisposable? _suppression;
        private bool _restore, _undoRestore, _disposed;
        internal Action Invalidated { get; } = invalidated;
        internal bool IsCurrent { get { lock (owner._gate) return !_disposed && version == owner._version; } }
        // BeginOperation revokes preparation without erasing someone else's successful display.
        // Hide and settled display failures revoke visibility separately from intent notifications.
        private bool HasNewShow { get { lock (owner._gate) return owner._shownVersion > version && ReferenceEquals(owner._shownWindow, _window); } }
        // Tests replace only visibility calls; identity/version and all lifecycle decisions stay here.
        internal IPreparationVisibility Visibility { get; set; } = PreparationVisibility.Instance;

        internal Task HideAsync() => ui.InvokeAsync(() =>
        {
            if (!IsCurrent) return Task.CompletedTask;
            _window = getWindow();
            if (_window == null || !_window.IsVisible) return Task.CompletedTask;
            // A closing/failed-close owner is ineligible for restoration, but its visible
            // content still prevents preparation. Leave its cleanup with the original owner.
            if (!alive()) throw new InvalidOperationException("Visible preparation owner is closing.");
            _suppression = suppress?.Invoke();
            _restore = true; // Hide may have taken effect before throwing.
            Visibility.Hide(_window);
            if (_window.IsVisible) throw new InvalidOperationException("Preparation hide incomplete.");
            return Task.CompletedTask;
        }, CancellationToken.None);

        internal Task RestoreAsync(Func<bool> stopping) => ui.InvokeAsync(() =>
        {
            if (_undoRestore)
            {
                if (!HasNewShow && ReferenceEquals(_window, getWindow()) && alive()) Visibility.Hide(_window!);
                _undoRestore = false;
            }
            if (_restore && !stopping() && IsCurrent && alive() && ReferenceEquals(_window, getWindow()))
            {
                bool activated = _window!.ShowActivated;
                var errors = new List<string>();
                try { _window.ShowActivated = false; Visibility.Show(_window); }
                catch (Exception ex) { errors.Add($"SHOW=0x{ex.HResult:X8}"); }
                // Check even when the native Show took effect and then failed.
                try
                {
                    if ((stopping() || !IsCurrent) && !HasNewShow && ReferenceEquals(_window, getWindow()))
                    {
                        _undoRestore = true;
                        Visibility.Hide(_window);
                        _undoRestore = false;
                    }
                }
                catch (Exception ex) { errors.Add($"LATE_HIDE=0x{ex.HResult:X8}"); }
                finally { _window.ShowActivated = activated; }
                if (errors.Count != 0) throw new Iris.Core.Errors.IrisException(Iris.Core.Errors.ErrorCode.CaptureFailed,
                    Iris.Core.Errors.ErrorMessages.GetDefault(Iris.Core.Errors.ErrorCode.CaptureFailed), detail: string.Join(';', errors));
            }
            _restore = false;
            _suppression?.Dispose(); _suppression = null;
            return Task.CompletedTask;
        }, CancellationToken.None);

        public void Dispose()
        {
            _suppression?.Dispose(); _suppression = null;
            lock (owner._gate) { _disposed = true; owner._receipts.Remove(this); }
            _window = null;
        }
    }
}

internal interface IPreparationVisibility
{
    void Hide(Window window);
    void Show(Window window);
}
internal sealed class PreparationVisibility : IPreparationVisibility
{
    internal static readonly PreparationVisibility Instance = new();
    public void Hide(Window window) => window.Hide();
    public void Show(Window window) => window.Show();
}
