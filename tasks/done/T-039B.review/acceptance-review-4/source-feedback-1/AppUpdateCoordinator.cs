using System.IO;
using Iris.Core.Errors;
using Iris.Core.Settings;
using Iris.Core.Presentation;
using Iris.Platform.Windows.Maintenance;
using Velopack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iris.App.Updates;

internal enum UpdatePhase { Unsupported, Idle, Checking, NoRelease, UpToDate, Available, Downloading, Ready, Cancelling, CleanupPending, Failed, Stopped }
internal sealed record UpdateSnapshot(UpdatePhase Phase, string CurrentVersion, Guid? OfferId = null, string? NewVersion = null, int Progress = 0, ErrorCode? Error = null);
internal sealed record UpdateCandidate(Guid Id, string Path, long Size, string Sha256, string Version)
{
    public override string ToString() => "UpdateCandidate";
}

/// <summary>Single owner of SDK tasks and all operation resources; cleanup retries never repeat network work.</summary>
internal sealed partial class AppUpdateCoordinator
{
    private readonly Lock _gate = new();
    private readonly UpdateRuntime _runtime;
    private readonly ISettingsService _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<AppUpdateCoordinator> _logger;
    private readonly INotificationService? _notifications;
    private Entry? _entry;
    private Task? _stop;
    private bool _closing, _started, _recoveryAvailable;
    private long _generation;
    private (Guid Id, UpdateInfo Info)? _offer;
    private UpdateCandidate? _candidate, _lastCandidate;
    private UpdateSnapshot _snapshot;
    internal event EventHandler? Changed;
    internal UpdateSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    internal bool HasCandidate { get { lock (_gate) return _candidate != null; } }
    /// <summary>
    /// Indicates that shutdown admission is closed but a previously validated local
    /// candidate can still be prepared again by the shutdown recovery owner.
    /// Ordinary checks and downloads remain closed in this state.
    /// </summary>
    internal bool IsRecoveryAvailable { get { lock (_gate) return _recoveryAvailable && _candidate != null; } }
    internal bool IsAcceptanceClosed { get { lock (_gate) return _closing; } }
    public AppUpdateCoordinator(UpdateRuntime runtime, ISettingsService settings, TimeProvider time, ILogger<AppUpdateCoordinator>? logger = null, INotificationService? notifications = null)
    { _runtime = runtime; _settings = settings; _time = time; _logger = logger ?? NullLogger<AppUpdateCoordinator>.Instance; _notifications = notifications; _snapshot = new(UpdatePhase.Unsupported, runtime.Version); }
    internal void Start()
    {
        lock (_gate)
        {
            if (_started || _closing) return;
            _started = true; _settings.Changed += OnSettingsChanged;
        }
        try { if (_runtime.Identity() == null) return; }
        catch (Exception) { Set(UpdatePhase.Failed, ErrorCode.InvalidResponse); return; }
        lock (_gate) { if (_closing) return; }
        Set(UpdatePhase.Idle);
        if (_settings.Current.General.CheckForUpdates) _ = CheckAsync(null, automatic: true);
    }
    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs args)
    {
        lock (_gate)
        {
            if (!_settings.Current.General.CheckForUpdates && _entry is { Automatic: true, Work.IsCompleted: false })
                _ = CancelAsync(); // Same entry is checked and canceled under the admission lock.
        }
    }
    internal Task CheckAsync(object? owner, bool automatic = false) => Begin(owner, automatic, download: false, null);
    internal Task DownloadAsync(object owner, Guid offerId) => Begin(owner, false, download: true, offerId);
    private Task Begin(object? owner, bool automatic, bool download, Guid? offerId)
    {
        Entry entry;
        lock (_gate)
        {
            if (_closing || _stop is { IsCompleted: false } || (_entry != null && (!_entry.Work.IsCompleted || !_entry.Cleaned)) || _snapshot.Phase == UpdatePhase.Unsupported) return Task.CompletedTask;
            if (download && (_offer == null || _offer.Value.Id != offerId || _snapshot.Phase != UpdatePhase.Available)) return Task.CompletedTask;
            _candidate = null;
            entry = new(owner, automatic, ++_generation, _time, download ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(30));
            _entry = entry;
            _snapshot = _snapshot with { Phase = download ? UpdatePhase.Downloading : UpdatePhase.Checking, Progress = 0, Error = null };
            entry.Work = RunAsync(entry, download);
        }
        return entry.Work;
    }
    private async Task RunAsync(Entry entry, bool download)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        Notify();
        UpdateSnapshot? result = null;
        UpdateCandidate? candidate = null;
        (Guid, UpdateInfo)? offer = null;
        try
        {
            entry.Token.ThrowIfCancellationRequested();
            entry.Operation = _runtime.Create(entry.Token);
            if (!download)
            {
                var update = await entry.Operation.Sdk.CheckForUpdatesAsync().ConfigureAwait(false);
                entry.Token.ThrowIfCancellationRequested();
                if (update == null) result = new(UpdatePhase.UpToDate, _runtime.Version);
                else
                {
                    ValidatedUpdateSource.Validate(update.TargetFullRelease);
                    offer = (Guid.NewGuid(), update);
                    result = new(UpdatePhase.Available, _runtime.Version, offer.Value.Item1, update.TargetFullRelease.Version.ToString());
                }
            }
            else
            {
                UpdateInfo update; Guid id;
                lock (_gate) { (id, update) = _offer!.Value; }
                var asset = update.TargetFullRelease; string path = entry.Operation.PackagePath(asset);
                if (File.Exists(path))
                {
                    try { await ReleasePackage.ValidateAsync(path, asset.Size, asset.SHA256, asset.Version.ToString(), entry.Token).ConfigureAwait(false); }
                    catch (IrisException)
                    {
                        entry.Token.ThrowIfCancellationRequested(); MaintenancePaths.RequireNoLinks(path); File.Delete(path);
                    }
                }
                entry.Token.ThrowIfCancellationRequested();
                await entry.Operation.Sdk.DownloadUpdatesAsync(update, progress => Progress(entry, progress), entry.Token).ConfigureAwait(false);
                entry.Token.ThrowIfCancellationRequested();
                await ReleasePackage.ValidateAsync(path, asset.Size, asset.SHA256, asset.Version.ToString(), entry.Token).ConfigureAwait(false);
                candidate = new(id, path, asset.Size, asset.SHA256, asset.Version.ToString());
                result = new(UpdatePhase.Ready, _runtime.Version, id, candidate.Version, 100);
            }
        }
        catch (OperationCanceledException) { result = new(UpdatePhase.Idle, _runtime.Version); }
        catch (NoCompatibleReleaseException) { result = new(UpdatePhase.NoRelease, _runtime.Version); }
        catch (Exception ex) { entry.Failure = ex; LogFailure(_logger, "operation", ex.GetType().Name); result = new(UpdatePhase.Failed, _runtime.Version, Error: ex is IrisException known ? known.Code : ErrorCode.NetworkUnavailable); }
        finally
        {
            // Completion includes real SDK work. A cleanup failure retains this entry for retry.
            entry.WorkFinished = true;
            try
            {
                await entry.CleanupAsync().ConfigureAwait(false);
                bool notifyUser = false;
                lock (_gate)
                {
                    if (ReferenceEquals(_entry, entry))
                    {
                        if (!_closing && entry.Generation == _generation && !entry.TokenCanceled)
                        {
                            _snapshot = result ?? new(UpdatePhase.Failed, _runtime.Version, Error: ErrorCode.Unknown);
                            if (offer.HasValue) _offer = (offer.Value.Item1, offer.Value.Item2);
                            _candidate = candidate;
                            if (entry.Automatic && result?.Phase == UpdatePhase.Available)
                                notifyUser = true;
                        }
                        else _snapshot = new(_closing ? UpdatePhase.Stopped : UpdatePhase.Idle, _runtime.Version);
                    }
                }
                if (notifyUser)
                {
                    try { _notifications?.Notify(NotificationLevel.Info, UiText.AppName, UiText.UpdateAvailable); }
                    catch (Exception ex) { LogFailure(_logger, "notification", ex.GetType().Name); }
                }
            }
            catch (Exception ex)
            {
                entry.Failure = entry.Failure == null ? ex : new AggregateException(entry.Failure, ex);
                LogFailure(_logger, "cleanup", ex.GetType().Name); Set(UpdatePhase.CleanupPending, ErrorCode.StorageFailure);
            }
            finally { Notify(); }
        }
    }
    private void Progress(Entry entry, int value)
    {
        lock (_gate)
        {
            if (_closing || entry.Generation != _generation || !ReferenceEquals(entry, _entry) || entry.Cleaned || entry.Token.IsCancellationRequested) return;
            _snapshot = _snapshot with { Progress = Math.Clamp(value, 0, 100) };
        }
        Notify();
    }
    internal async Task CloseOwnerAsync(object owner)
    {
        Entry? entry; Task stopping;
        lock (_gate)
        {
            entry = _entry;
            if (!ReferenceEquals(entry?.Owner, owner)) return;
            stopping = CancelAsync();
        }
        await stopping.ConfigureAwait(false);
        lock (_gate)
            if (ReferenceEquals(_entry, entry) && entry is { Cleaned: false })
                throw new IrisException(ErrorCode.StorageFailure, ErrorMessages.GetDefault(ErrorCode.StorageFailure));
    }
    internal Task CancelAsync()
    {
        lock (_gate)
        {
            _entry?.Cancel();
            if (_stop is { IsCompleted: false }) return _stop;
            ++_generation; _candidate = null;
            _snapshot = _snapshot with { Phase = UpdatePhase.Cancelling };
            return _stop = CancelCoreAsync(_entry, _generation, _entry?.Work.IsCompleted == true);
        }
    }
    private async Task CancelCoreAsync(Entry? entry, long generation, bool retry)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        Notify();
        if (entry != null)
        {
            await entry.Work.ConfigureAwait(false);
            try { await entry.JoinCancellationAsync().ConfigureAwait(false); }
            catch (Exception ex) { LogFailure(_logger, "cancellation", ex.GetType().Name); Set(UpdatePhase.CleanupPending, ErrorCode.StorageFailure); return; }
            if (!retry)
            {
                if (!entry.Cleaned) return;
            }
            try { await entry.CleanupAsync().ConfigureAwait(false); }
            catch (Exception) { Set(UpdatePhase.CleanupPending, ErrorCode.StorageFailure); return; }
        }
        lock (_gate)
        {
            if (!ReferenceEquals(_entry, entry) || generation != _generation) return;
            _entry = null; _snapshot = new(_closing ? UpdatePhase.Stopped : UpdatePhase.Idle, _runtime.Version);
        }
        Notify();
    }
    internal Task<UpdateCandidate?> PrepareApplyAsync(object owner, Guid id, CancellationToken token)
    {
        lock (_gate)
        {
            bool recovery = _closing && _recoveryAvailable && _candidate?.Id == id;
            if ((_closing && !recovery) || _stop is { IsCompleted: false } || (_entry != null && (!_entry.Work.IsCompleted || !_entry.Cleaned))
                || _candidate?.Id != id || (_snapshot.Phase != UpdatePhase.Ready && _snapshot.Phase != UpdatePhase.Failed)) return Task.FromResult<UpdateCandidate?>(null);
            var candidate = _candidate; _candidate = null; _lastCandidate = candidate;
            var entry = new Entry(owner, false, ++_generation, _time, TimeSpan.FromSeconds(30), token);
            _entry = entry; _snapshot = _snapshot with { Phase = UpdatePhase.Downloading };
            var work = PrepareCoreAsync(entry, candidate, recovery); entry.Work = work; return work;
        }
    }
    internal void RollbackApply(UpdateCandidate candidate, ErrorCode error)
    {
        lock (_gate)
        {
            _candidate = candidate;
            _lastCandidate = candidate;
            _recoveryAvailable = _closing;
            _snapshot = new(UpdatePhase.Failed, _runtime.Version, candidate.Id, candidate.Version, 100, Error: error);
        }
        Notify();
    }
    internal bool RollbackLastApply(ErrorCode error)
    {
        UpdateCandidate? candidate;
        lock (_gate)
        {
            if (_lastCandidate == null) return false;
            candidate = _lastCandidate;
            _candidate = candidate;
            _recoveryAvailable = true;
            _snapshot = new(UpdatePhase.Failed, _runtime.Version, candidate.Id, candidate.Version, 100, Error: error);
        }
        Notify();
        return true;
    }
    private async Task<UpdateCandidate?> PrepareCoreAsync(Entry entry, UpdateCandidate candidate, bool recovery)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding); Notify();
        bool valid = false;
        ErrorCode? failure = null;
        try { await _runtime.ValidateAsync(candidate, entry.Token).ConfigureAwait(false); entry.Token.ThrowIfCancellationRequested(); valid = true; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { entry.Failure = ex; failure = ex is IrisException known ? known.Code : ErrorCode.InvalidResponse; LogFailure(_logger, "prepare", ex.GetType().Name); }
        finally
        {
            entry.WorkFinished = true;
            try { await entry.CleanupAsync().ConfigureAwait(false); }
            catch (Exception ex) { LogFailure(_logger, "cleanup", ex.GetType().Name); }
        }
        lock (_gate)
        {
            bool keepCandidate = failure != null || !entry.Cleaned || (recovery && _closing);
            if (keepCandidate)
            {
                _candidate = candidate;
                _lastCandidate = candidate;
                _recoveryAvailable = _closing || _recoveryAvailable;
            }
            _snapshot = new(entry.Cleaned ? (_closing && !recovery ? UpdatePhase.Stopped : failure == null ? UpdatePhase.Idle : UpdatePhase.Failed) : UpdatePhase.CleanupPending, _runtime.Version,
                keepCandidate ? candidate.Id : null, keepCandidate ? candidate.Version : null, keepCandidate ? 100 : 0, failure);
        }
        Notify();
        lock (_gate) return valid && entry.Cleaned && (!_closing || recovery) && entry.Generation == _generation && !entry.TokenCanceled ? candidate : null;
    }
    internal void StopAcceptance()
    {
        lock (_gate)
        {
            _closing = true;
            _entry?.Cancel();
            if (_candidate != null) _lastCandidate = _candidate;
            _candidate = null;
            _recoveryAvailable = false;
        }
    }
    internal async Task StopAsync()
    {
        StopAcceptance(); _settings.Changed -= OnSettingsChanged;
        await CancelAsync().ConfigureAwait(false);
        if (Snapshot.Phase == UpdatePhase.CleanupPending) throw new IrisException(ErrorCode.StorageFailure, ErrorMessages.GetDefault(ErrorCode.StorageFailure));
    }
    private void Set(UpdatePhase phase, ErrorCode? error = null) { lock (_gate) _snapshot = _snapshot with { Phase = phase, Error = error }; Notify(); }
    private void Notify()
    {
        foreach (var subscriber in Changed?.GetInvocationList() ?? [])
            try { ((EventHandler)subscriber)(this, EventArgs.Empty); }
            catch (Exception ex) { LogFailure(_logger, "subscriber", ex.GetType().Name); }
    }
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Update {Stage} failed: {ExceptionType}")]
    private static partial void LogFailure(ILogger logger, string stage, string exceptionType);

    private sealed class Entry : IAsyncDisposable
    {
        private readonly CancellationTokenSource _source = new();
        private readonly ITimer _timer;
        private readonly CancellationTokenRegistration _caller;
        private readonly Lock _gate = new();
        private Task? _cancel;
        private bool _cancelJoined, _disposed, _cancellationSealed;
        internal Task Work = Task.CompletedTask;
        internal bool Cleaned => _disposed;
        internal readonly object? Owner; internal readonly bool Automatic; internal readonly long Generation;
        internal UpdateOperation? Operation; internal bool WorkFinished, TokenCanceled;
        internal Exception? Failure;
        internal CancellationToken Token { get; }
        internal Entry(object? owner, bool automatic, long generation, TimeProvider time, TimeSpan deadline, CancellationToken caller = default)
        {
            Owner = owner; Automatic = automatic; Generation = generation; Token = _source.Token;
            _timer = time.CreateTimer(_ => Cancel(), null, deadline, Timeout.InfiniteTimeSpan);
            _caller = caller.UnsafeRegister(_ => Cancel(), null);
        }
        internal void Cancel()
        {
            lock (_gate)
            {
                if (_disposed) return;
                TokenCanceled = true;
                if (!_cancellationSealed) _cancel ??= _source.CancelAsync();
            }
        }
        internal async Task CleanupAsync()
        {
            if (!WorkFinished) throw new InvalidOperationException("Update work is still active.");
            if (_disposed) return;
            await _caller.DisposeAsync().ConfigureAwait(false);
            await _timer.DisposeAsync().ConfigureAwait(false);
            Exception? failure = null;
            try { if (Operation != null) await Operation.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { failure = ex; }
            // Manual cancellation remains accepted throughout resource disposal. Seal only after it ends.
            lock (_gate) { if (failure == null) _cancellationSealed = true; }
            try { await JoinCancellationAsync().ConfigureAwait(false); }
            catch (Exception ex) { failure = failure == null ? ex : new AggregateException(failure, ex); }
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            lock (_gate) { TokenCanceled |= _source.IsCancellationRequested; _source.Dispose(); _disposed = true; }
        }
        internal async Task JoinCancellationAsync()
        {
            Task? cancel; lock (_gate) cancel = _cancel;
            if (cancel != null && !_cancelJoined)
            { try { await cancel.ConfigureAwait(false); } finally { _cancelJoined = true; } }
        }
        public ValueTask DisposeAsync() => new(CleanupAsync());
    }
}
