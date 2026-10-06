using Iris.App.Hosting;
using Iris.App.Selection;
using Iris.App.Settings;
using Iris.App.Translation;
using Iris.Core.Errors;

namespace Iris.App.Capture;

internal enum PreparationRevocation { None, CallerCanceled, DisplayIntent, Shutdown }

/// <summary>A single registered responsibility from admission through actual cleanup.</summary>
internal sealed class ScreenshotPreparationCoordinator(
    TranslateWindowPresenter translation, SettingsWindowPresenter settings,
    HotkeyRecordingCoordinator recording, SelectionIconController mouse,
    IEnumerable<IScreenshotPreparationParticipant>? participants = null)
{
    private readonly TranslateWindowPresenter _translation = translation;
    private readonly IScreenshotPreparationParticipant[] _participants = participants?.ToArray() ?? [];
    private readonly HotkeyRecordingCoordinator _recording = recording;
    private readonly SettingsWindowPresenter _settings = settings;
    private readonly SelectionIconController _mouse = mouse;
    private readonly object _gate = new();
    private Session? _session;
    private bool _stopping;
    private Task? _stop;
    internal IPreparationVisibility Visibility { get; set; } = PreparationVisibility.Instance;

    internal Task<Lease> PrepareAsync(bool hideIrisWindows, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Session session;
        lock (_gate)
        {
            if (_stopping) throw Failure("STOPPED", []);
            Session? previous = _session is { Released: false } pending ? pending : null;
            if (previous != null && !previous.CanRetry) throw Failure("BUSY", []);
            session = new(this, previous, cancellationToken);
            _session = session;
        }
        session.Start(hideIrisWindows);
        return session.Prepared.Task;
    }

    internal void StopAcceptance()
    {
        Session? session;
        lock (_gate) { _stopping = true; session = _session; }
        _recording.StopAcceptance();
        session?.Revoke(PreparationRevocation.Shutdown);
    }
    internal Task StopAsync()
    {
        StopAcceptance();
        lock (_gate)
        {
            if (_session == null) return Task.CompletedTask;
            if (_stop is { IsCompleted: false }) return _stop;
            return _stop = Task.Run(async () =>
            {
                Session? session;
                lock (_gate) session = _session;
                if (session != null)
                {
                    try { await session.ReturnAsync().ConfigureAwait(false); }
                    finally
                    {
                        // The caller's outcome and the cleanup barrier both settle before exit.
                        try { await session.Prepared.Task.ConfigureAwait(false); }
                        catch (Exception) { /* Delivered to the preparing caller; Return reports retained cleanup failures. */ }
                    }
                }
            });
        }
    }
    internal Task RetryCleanupAsync()
    {
        lock (_gate) return _session?.RetryAsync() ?? Task.CompletedTask;
    }
    private static ScreenshotException Failure(string stage, IEnumerable<string> failures) =>
        new ScreenshotException(ErrorCode.CaptureFailed, $"stage={stage};{string.Join(';', failures)}");

    internal sealed class Lease(Session session) : IAsyncDisposable
    {
        internal CancellationToken Revoked => session.Token;
        internal PreparationRevocation Reason => session.Reason;
        public ValueTask DisposeAsync() => new(session.ReturnAsync());
    }

    internal sealed class Session(ScreenshotPreparationCoordinator owner, Session? previous, CancellationToken caller) : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _revoked = new();
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<Lease> Prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _registration;
        private CancellationToken _token;
        private Task? _cancel, _return;
        private Session? _previous = previous;
        private string? _previousFailure;
        private HotkeyRecordingCoordinator.PreparationPause? _recordingPause;
        private SelectionIconController.PreparationPause? _mousePause;
        internal WindowPreparationIntent.Receipt? Translation, Settings;
        private readonly List<WindowPreparationIntent.Receipt> _additionalWindows = [];
        internal volatile bool Released;
        private bool _sealed, _settingsReturned, _translationReturned;
        internal bool CanRetry { get { lock (_gate) return Prepared.Task.IsCompleted && _return is { IsFaulted: true }; } }
        private PreparationRevocation _reason;
        internal PreparationRevocation Reason { get { lock (_gate) return _reason; } }
        internal CancellationToken Token => _token;

        internal void Revoke(PreparationRevocation reason)
        {
            lock (_gate)
            {
                if (Released || _sealed) return;
                if (_reason == PreparationRevocation.None) _reason = reason;
                _cancel ??= Task.Run(async () => await _revoked.CancelAsync().ConfigureAwait(false));
            }
        }
        internal void Start(bool hide) { _token = _revoked.Token; _ = RunAsync(hide); }
        private async Task RunAsync(bool hide)
        {
            Exception? failure = null;
            string stage = "PREVIOUS";
            try
            {
                // Synchronous version/subscription capture precedes every cleanup await and UI queue.
                Translation = owner._translation.ObservePreparation(() => Revoke(PreparationRevocation.DisplayIntent));
                Settings = owner._settings.ObservePreparation(() => Revoke(PreparationRevocation.DisplayIntent));
                Translation.Visibility = Settings.Visibility = owner.Visibility;
                foreach (var participant in owner._participants)
                    _additionalWindows.Add(participant.ObservePreparation(() => Revoke(PreparationRevocation.DisplayIntent)));
                _registration = caller.Register(() => Revoke(PreparationRevocation.CallerCanceled));
                if (_previous != null)
                {
                    try { await _previous.ReturnAsync().ConfigureAwait(false); _previous = null; }
                    catch (Exception ex)
                    {
                        _previousFailure = ex is ScreenshotException { Detail: not null } known ? known.Detail : $"PREVIOUS=0x{ex.HResult:X8}";
                        throw;
                    }
                }
                stage = "INPUT_ADMISSION";
                // Admission closes synchronously; leases are registered before draining anything.
                _mousePause = owner._mouse.PauseForPreparation();
                _recordingPause = owner._recording.PauseForPreparation();
                stage = "RECORDING";
                await _recordingPause.Ready.ConfigureAwait(false);
                await owner._settings.CancelRecordingForPreparationAsync().ConfigureAwait(false);
                await owner._recording.CancelForPreparationAsync().ConfigureAwait(false);
                stage = "MOUSE_DRAIN";
                await _mousePause.DrainAsync().ConfigureAwait(false);
                ThrowIfRevoked();
                if (hide)
                {
                    stage = "HIDE_TRANSLATION";
                    await Translation!.HideAsync().ConfigureAwait(false);
                    ThrowIfRevoked();
                    stage = "HIDE_SETTINGS";
                    await Settings!.HideAsync().ConfigureAwait(false);
                    stage = "HIDE_PARTICIPANT";
                    foreach (var receipt in _additionalWindows)
                    {
                        ThrowIfRevoked();
                        await receipt.HideAsync().ConfigureAwait(false);
                    }
                }
                ThrowIfRevoked();
            }
            catch (Exception ex) { failure = ex; }
            finally { _ready.TrySetResult(); }

            if (failure == null)
            {
                lock (_gate)
                {
                    if (_reason == PreparationRevocation.None) Prepared.TrySetResult(new Lease(this));
                    else failure = new OperationCanceledException();
                }
            }

            if (failure != null)
            {
                try { await ReturnAsync().ConfigureAwait(false); }
                catch (Exception cleanup)
                {
                    Prepared.TrySetException(Failure(stage, [ScreenshotErrors.Describe("PRIMARY", failure),
                        cleanup is ScreenshotException { Detail: not null } known ? known.Detail : $"cleanup=0x{cleanup.HResult:X8}"]));
                    return;
                }
                if (failure is OperationCanceledException && Reason != PreparationRevocation.None) Prepared.TrySetCanceled(new CancellationToken(true));
                else Prepared.TrySetException(Failure(stage, [failure is ScreenshotException { Detail: not null } known ? known.Detail : $"hr=0x{failure.HResult:X8}"]));
                return;
            }
        }
        private void ThrowIfRevoked()
        {
            if (Translation?.IsCurrent == false || Settings?.IsCurrent == false || _additionalWindows.Any(r => !r.IsCurrent))
                Revoke(PreparationRevocation.DisplayIntent);
            lock (_gate) if (Reason != PreparationRevocation.None) throw new OperationCanceledException();
        }
        public ValueTask DisposeAsync() => new(ReturnAsync());
        internal Task RetryAsync()
        {
            lock (_gate) return _return == null ? Task.CompletedTask : ReturnAsync();
        }
        internal Task ReturnAsync()
        {
            lock (_gate)
            {
                if (Released) return Task.CompletedTask;
                if (_return is { IsCompleted: false }) return _return;
                return _return = Task.Run(CleanupAsync);
            }
        }
        private async Task CleanupAsync()
        {
            await _ready.Task.ConfigureAwait(false);
            var errors = new List<string>();
            bool Stopping() { lock (owner._gate) return owner._stopping; }
            async Task Restore(WindowPreparationIntent.Receipt? receipt, string stage)
            {
                if (receipt == null) return;
                try { await receipt.RestoreAsync(Stopping).ConfigureAwait(false); receipt.Dispose(); }
                catch (Exception ex) { errors.Add(ex is ScreenshotException { Detail: not null } known ? $"{stage}[{known.Detail}]" : $"{stage}=0x{ex.HResult:X8}"); }
            }
            for (int i = _additionalWindows.Count - 1; i >= 0; i--)
            {
                int count = errors.Count;
                await Restore(_additionalWindows[i], "PARTICIPANT").ConfigureAwait(false);
                if (errors.Count == count) _additionalWindows.RemoveAt(i);
            }
            if (!_settingsReturned) { int count = errors.Count; await Restore(Settings, "SETTINGS").ConfigureAwait(false); _settingsReturned = count == errors.Count; }
            if (!_translationReturned) { int count = errors.Count; await Restore(Translation, "TRANSLATION").ConfigureAwait(false); _translationReturned = count == errors.Count; }
            if (_recordingPause != null)
            {
                try { await owner._settings.CancelRecordingForPreparationAsync().ConfigureAwait(false);
                    await owner._recording.CancelForPreparationAsync().ConfigureAwait(false);
                    await _recordingPause.DisposeAsync().ConfigureAwait(false); _recordingPause = null; }
                catch (Exception ex) { errors.Add($"RECORDING=0x{ex.HResult:X8}"); }
            }
            if (_mousePause != null)
            {
                try { await _mousePause.DrainAsync().ConfigureAwait(false); await _mousePause.DisposeAsync().ConfigureAwait(false); _mousePause = null; }
                catch (Exception ex) { errors.Add($"MOUSE=0x{ex.HResult:X8}"); }
            }
            await _registration.DisposeAsync().ConfigureAwait(false);
            Task? cancel;
            lock (_gate) { if (errors.Count == 0) _sealed = true; cancel = _cancel; }
            if (cancel != null)
                try { await cancel.ConfigureAwait(false); }
                catch (Exception ex) { errors.Add(ScreenshotErrors.Describe("CANCEL", ex)); lock (_gate) _cancel = null; }
            if (errors.Count != 0) throw Failure("CLEANUP", errors);
            _revoked.Dispose();
            if (_previous != null)
            {
                // A failed preflight acquired no input/window resources. Keep the original retained session,
                // rather than building an unbounded chain of failed retry wrappers.
                lock (owner._gate) { if (ReferenceEquals(owner._session, this)) owner._session = _previous; Released = true; }
                _previous = null;
                throw Failure("PREVIOUS", [_previousFailure!]);
            }
            Released = true;
        }
    }
}
