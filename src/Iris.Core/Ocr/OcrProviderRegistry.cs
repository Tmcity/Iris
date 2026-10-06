using Iris.Core.Common;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Microsoft.Extensions.Logging;

namespace Iris.Core.Ocr;

internal sealed partial class OcrProviderRegistry : IOcrProviderRegistry, IDisposable
{
    private readonly object _gate = new();
    private readonly ISettingsService _settings;
    private readonly ISecretStore _secrets;
    private readonly Dictionary<OcrProviderKind, IOcrProviderFactory> _factories = [];
    private readonly ILogger<OcrProviderRegistry> _logger;
    private readonly HashSet<Instance> _owned = [];
    private readonly HashSet<Task> _constructing = [];
    private Instance[] _current = [];
    private IReadOnlyList<OcrProviderRegistration> _registrations = Array.AsReadOnly(Array.Empty<OcrProviderRegistration>());
    private AppSettings? _publishedSettings;
    private long _version = 1, _publishedVersion, _failedVersion;
    private OcrRegistryException? _failure;
    private Task? _refresh, _stop;
    private readonly HashSet<Task> _maintenance = [];
    private TaskCompletionSource _changedState = NewSignal();
    private bool _stopping, _disposed;
    private EventHandler? _changed;
    public event EventHandler? Changed
    {
        add { lock (_gate) { if (!_stopping) _changed += value; } }
        remove { lock (_gate) _changed -= value; }
    }
    public OcrProviderRegistry(ISettingsService settings, ISecretStore secrets, IEnumerable<IOcrProviderFactory> factories, ILogger<OcrProviderRegistry> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings)); _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger)); ArgumentNullException.ThrowIfNull(factories);
        try { foreach (var factory in factories) if (!_factories.TryAdd(factory.Kind, factory)) throw new OcrRegistryException("DUPLICATE_FACTORY"); }
        catch (Exception ex) { throw Safe("FACTORIES", ex); }
        _settings.Changed += OnSettings; _secrets.Changed += OnSecret;
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void PulseLocked() { var old = _changedState; _changedState = NewSignal(); old.TrySetResult(); }
    private void CheckAcceptingLocked() => ObjectDisposedException.ThrowIf(_stopping, this);
    private static OcrRegistryException Safe(string stage, Exception ex) => ex as OcrRegistryException ?? new(stage, $"hr=0x{ex.HResult:X8}");
    private static string Diagnostic(Exception ex) => ex is OcrRegistryException safe ? safe.Detail! : $"hr=0x{ex.HResult:X8}";
    private void Log(string stage, Exception ex)
    { try { LogFailure(_logger, stage, ex.GetType().Name); } catch (Exception) { /* Logging cannot abandon registry-owned work. */ } }
    [LoggerMessage(Level = LogLevel.Warning, Message = "OCR registry stage={Stage}; type={ExceptionType}")]
    private static partial void LogFailure(ILogger logger, string stage, string exceptionType);

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) { CheckAcceptingLocked(); return StartRefreshLocked().WaitAsync(cancellationToken); }
    }
    public async Task<IOcrProviderSnapshot> AcquireSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (true)
        {
            Task refresh;
            lock (_gate)
            {
                CheckAcceptingLocked(); cancellationToken.ThrowIfCancellationRequested();
                if (_refresh != null) refresh = _refresh;
                else if (_failure != null && _failedVersion == _version) throw _failure;
                else if (_publishedSettings != null && _publishedVersion == _version)
                {
                    // No await separates this final cancellation check from granting the lease.
                    foreach (var item in _current) item.Leases++;
                    return new OcrProviderSnapshot(this, _publishedSettings, _current.ToArray(), _registrations);
                }
                else refresh = StartRefreshLocked();
            }
            await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
    private Task StartRefreshLocked()
    {
        if (_refresh != null) return _refresh;
        var completion = NewSignal(); _refresh = completion.Task;
        _ = Task.Run(async () =>
        {
            long attempted = 0; OcrRegistryException? failure = null;
            try { await RefreshCoreAsync(v => attempted = v).ConfigureAwait(false); }
            catch (Exception ex) { failure = Safe("REFRESH", ex); Log("REFRESH", failure); }
            lock (_gate)
            {
                _refresh = null;
                if (failure != null) { _failure = failure; _failedVersion = attempted; completion.TrySetException(failure); _ = completion.Task.Exception; }
                else { _failure = null; _failedVersion = 0; completion.TrySetResult(); }
                PulseLocked();
                // A new invalidation during failed work gets one new attempt; the same bad version never spins.
                if (!_stopping && _publishedVersion != _version && (failure == null || _failedVersion != _version)) StartRefreshLocked();
            }
        });
        return completion.Task;
    }
    private async Task RefreshCoreAsync(Action<long> attempt)
    {
        Instance[] debt;
        lock (_gate) { attempt(_version); debt = EligibleLocked(); }
        await CleanupInstancesAsync(debt).ConfigureAwait(false);
        while (true)
        {
            long version; Instance[] current;
            lock (_gate)
            {
                if (_stopping) return;
                version = _version; attempt(version);
                if (_publishedSettings != null && _publishedVersion == version) return;
                current = _current;
            }
            var created = new List<Instance>(); var next = new List<Instance>(); var registrations = new List<OcrProviderRegistration>();
            AppSettings? frozen = null; Exception? failure = null;
            try
            {
                frozen = OcrRegistryFreeze.Settings(_settings.Current);
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var profile in frozen.Ocr.Providers.Where(p => p.Enabled))
                {
                    lock (_gate) if (_stopping || version != _version) break;
                    if (string.IsNullOrWhiteSpace(profile.Id) || !ids.Add(profile.Id)) throw new OcrRegistryException("PROFILE_ID");
                    if (!_factories.TryGetValue(profile.Kind, out var factory)) throw new OcrRegistryException("MISSING_FACTORY");
                    var credentials = ProviderCredentialResolver.Resolve(profile, _secrets, _logger);
                    var existing = current.FirstOrDefault(i => i.Matches(profile, credentials));
                    if (existing == null)
                    {
                        IOcrProvider provider = credentials.IsExplicitUnconfigured ? new UnavailableOcrProvider(profile) : factory.Create(profile, credentials.Credentials!);
                        existing = Register(profile, credentials, provider); created.Add(existing);
                    }
                    // Reading a descriptor can fail; ownership was already registered.
                    var descriptor = existing.Provider!.Descriptor ?? throw new OcrRegistryException("NULL_DESCRIPTOR");
                    existing.CandidateDescriptor = descriptor;
                    next.Add(existing); registrations.Add(new(profile, existing.Provider!, existing.Id));
                }
            }
            catch (Exception ex) { failure = Safe("BUILD", ex); }

            bool publish, changed = false, stopping;
            lock (_gate)
            {
                stopping = _stopping;
                publish = failure == null && !stopping && version == _version;
                if (publish)
                {
                    changed = _current.Length != next.Count || _current.Where((item, i) => i < next.Count &&
                        (!ReferenceEquals(item, next[i]) || item.Descriptor != next[i].CandidateDescriptor)).Any();
                    foreach (var old in _current) if (!next.Contains(old)) old.Current = false;
                    foreach (var item in next) item.Descriptor = item.CandidateDescriptor;
                    _current = next.ToArray(); _registrations = Array.AsReadOnly(registrations.ToArray());
                    _publishedSettings = frozen; _publishedVersion = version; _failure = null; _failedVersion = 0;
                }
                else foreach (var item in created) item.Current = false;
                debt = publish ? EligibleLocked() : created.ToArray(); PulseLocked();
            }
            Exception? cleanup = null;
            try { await CleanupInstancesAsync(debt).ConfigureAwait(false); } catch (Exception ex) { cleanup = ex; }
            if (publish && changed) NotifyChanged();
            if (cleanup != null) throw new OcrRegistryException("CANDIDATE_CLEANUP", $"primary=[{(failure == null ? "none" : Diagnostic(failure))}];cleanup=[{Diagnostic(cleanup)}]");
            lock (_gate)
            {
                if (_stopping) return;
                if (version != _version) continue;
            }
            if (failure != null) throw Safe("BUILD", failure);
            if (publish) return;
        }
    }
    private Instance Register(OcrProviderProfile profile, CredentialResolutionResult credentials, IOcrProvider provider)
    {
        var item = new Instance(profile, credentials, provider ?? throw new OcrRegistryException("NULL_PROVIDER"));
        lock (_gate) { _owned.Add(item); PulseLocked(); }
        return item;
    }
    public Task<IOcrProviderSnapshot> CreateTransientAsync(OcrProviderProfile profile, ProviderCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile); ArgumentNullException.ThrowIfNull(credentials); cancellationToken.ThrowIfCancellationRequested();
        var frozen = OcrRegistryFreeze.Profile(profile); // Capture the caller's draft before any queue.
        lock (_gate)
        {
            CheckAcceptingLocked();
            var completion = new TaskCompletionSource<IOcrProviderSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var barrier = NewSignal(); _constructing.Add(barrier.Task);
            _ = Task.Run(async () =>
            {
                Instance? item = null; Exception? failure = null; bool delivered = false;
                try
                {
                    lock (_gate) CheckAcceptingLocked(); cancellationToken.ThrowIfCancellationRequested();
                    var settings = OcrRegistryFreeze.Settings(_settings.Current);
                    if (!_factories.TryGetValue(frozen.Kind, out var factory)) throw new OcrRegistryException("MISSING_FACTORY");
                    item = Register(frozen, CredentialResolutionResult.Success(credentials), factory.Create(frozen, credentials));
                    _ = item.Provider!.Descriptor;
                    lock (_gate)
                    {
                        CheckAcceptingLocked(); cancellationToken.ThrowIfCancellationRequested();
                        item.Current = false; item.Leases = 1;
                        completion.TrySetResult(new OcrProviderSnapshot(this, settings, [item], Array.AsReadOnly(new[] { new OcrProviderRegistration(frozen, item.Provider!, item.Id) })));
                        delivered = true;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { failure = new OperationCanceledException(cancellationToken); }
                catch (Exception ex) { failure = Safe("TRANSIENT", ex); }
                if (!delivered && item != null)
                {
                    lock (_gate) item.Current = false;
                    try { await CleanupInstancesAsync([item]).ConfigureAwait(false); }
                    catch (Exception ex) { failure = new OcrRegistryException("TRANSIENT_CLEANUP", $"primary=[{(failure == null ? "none" : Diagnostic(failure))}];{Diagnostic(ex)}"); }
                }
                lock (_gate)
                {
                    if (!delivered)
                    {
                        if (failure is OperationCanceledException) completion.TrySetCanceled(cancellationToken);
                        else completion.TrySetException(failure ?? new OcrRegistryException("TRANSIENT"));
                    }
                    _constructing.Remove(barrier.Task); barrier.TrySetResult(); PulseLocked();
                }
            }, CancellationToken.None);
            return completion.Task;
        }
    }
    internal Instance[] ReleaseReferences(Instance[] instances)
    {
        lock (_gate)
        {
            foreach (var item in instances) { if (item.Leases <= 0) throw new OcrRegistryException("LEASE_UNDERFLOW"); item.Leases--; }
            PulseLocked(); return instances.Where(i => !i.Current && i.Leases == 0).ToArray();
        }
    }
    private Instance[] EligibleLocked() => _owned.Where(i => !i.Current && i.Leases == 0).ToArray();
    internal async Task CleanupInstancesAsync(Instance[] instances)
    {
        var tasks = instances.Select(CleanupInstance).ToArray(); var errors = new List<string>();
        foreach (var task in tasks) try { await task.ConfigureAwait(false); } catch (Exception ex) { errors.Add(Diagnostic(ex)); }
        if (errors.Count != 0) throw new OcrRegistryException("DISPOSE_INSTANCES", string.Join(';', errors));
    }
    private Task CleanupInstance(Instance item)
    {
        lock (_gate)
        {
            if (item.Current || item.Leases != 0 || item.Provider == null) return Task.CompletedTask;
            if (item.Cleanup is { IsCompleted: false }) return item.Cleanup;
            var completion = NewSignal(); item.Cleanup = completion.Task;
            _ = Task.Run(async () =>
            {
                Exception? failure = null;
                try
                {
                    if (item.Provider is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    else if (item.Provider is IDisposable disposable) disposable.Dispose();
                }
                catch (Exception ex) { failure = new OcrRegistryException("PROVIDER_DISPOSE", $"instance={item.Id};hr=0x{ex.HResult:X8}"); }
                lock (_gate)
                {
                    if (failure == null) { item.Clear(); _owned.Remove(item); completion.TrySetResult(); }
                    else { completion.TrySetException(failure); _ = completion.Task.Exception; }
                    item.Cleanup = null; PulseLocked();
                }
            });
            return completion.Task;
        }
    }
    public Task RetryCleanupAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            var completion = NewSignal(); var items = EligibleLocked(); _maintenance.Add(completion.Task);
            // Capture/join current attempts at admission, before a worker queue delay can turn
            // a concurrent failed attempt into an unintended second disposal.
            var cleanup = CleanupInstancesAsync(items);
            _ = Task.Run(async () =>
            {
                Exception? failure = null;
                try { await cleanup.ConfigureAwait(false); } catch (Exception ex) { failure = ex; }
                lock (_gate)
                {
                    if (failure == null && _publishedVersion == _version && EligibleLocked().Length == 0) { _failure = null; _failedVersion = 0; }
                    if (failure != null) { completion.TrySetException(failure); _ = completion.Task.Exception; } else completion.TrySetResult();
                    _maintenance.Remove(completion.Task); PulseLocked();
                }
            }, CancellationToken.None);
            return completion.Task.WaitAsync(cancellationToken);
        }
    }
    public void StopAccepting()
    {
        lock (_gate) { if (_stopping) return; _stopping = true; _changed = null; PulseLocked(); }
        _settings.Changed -= OnSettings; _secrets.Changed -= OnSecret;
    }
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        StopAccepting();
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            if (_stop is not { IsCompleted: false })
            {
                _stop = Task.Run(StopCoreAsync, CancellationToken.None);
                _ = _stop.ContinueWith(static done => { _ = done.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            return _stop.WaitAsync(cancellationToken);
        }
    }
    private async Task StopCoreAsync()
    {
        Task[] constructing;
        lock (_gate) constructing = _constructing.Concat(_refresh == null ? [] : new[] { _refresh }).Concat(_maintenance).ToArray();
        foreach (var task in constructing) try { await task.ConfigureAwait(false); } catch (Exception ex) { Log("STOP_JOIN", ex); }
        lock (_gate)
        {
            foreach (var item in _current) item.Current = false;
            _current = []; _registrations = Array.AsReadOnly(Array.Empty<OcrProviderRegistration>()); _publishedSettings = null; PulseLocked();
        }
        while (true)
        {
            Instance[] ready; Task changed;
            lock (_gate) ready = EligibleLocked();
            await CleanupInstancesAsync(ready).ConfigureAwait(false);
            lock (_gate)
            {
                if (_owned.Count == 0 && _maintenance.Count == 0) { _failure = null; _factories.Clear(); _disposed = true; return; }
                if (_owned.Any(i => !i.Current && i.Leases == 0)) continue;
                changed = _changedState.Task;
            }
            await changed.ConfigureAwait(false);
        }
    }
    public ValueTask DisposeAsync() => new(StopAsync());
    public void Dispose()
    {
        StopAccepting();
        lock (_gate)
        {
            if (_disposed) return;
            if (_owned.Count != 0 || _constructing.Count != 0 || _refresh != null || _maintenance.Count != 0 || _stop is { IsCompleted: false })
                throw new OcrRegistryException("ASYNC_STOP_REQUIRED");
            _publishedSettings = null; _failure = null; _factories.Clear(); _disposed = true;
        }
    }
    private void OnSettings(object? sender, SettingsChangedEventArgs args) => Invalidate();
    private void OnSecret(object? sender, string key) => Invalidate();
    private void Invalidate() { lock (_gate) { if (_stopping) return; _version++; StartRefreshLocked(); } }
    private void NotifyChanged()
    {
        Delegate[] handlers; lock (_gate) { if (_stopping) return; handlers = _changed?.GetInvocationList() ?? []; }
        foreach (var handler in handlers) { lock (_gate) if (_stopping) break; try { ((EventHandler)handler)(this, EventArgs.Empty); } catch (Exception ex) { Log("CHANGED", ex); } }
    }
    internal sealed class Instance
    {
        internal Guid Id { get; } = Guid.NewGuid();
        internal IOcrProvider? Provider;
        internal ProviderDescriptor? Descriptor, CandidateDescriptor;
        private OcrProviderProfile? _fingerprint;
        private CredentialResolutionResult _credentials;
        internal bool Current = true;
        internal int Leases;
        internal Task? Cleanup;
        internal Instance(OcrProviderProfile profile, CredentialResolutionResult credentials, IOcrProvider provider)
        { _fingerprint = profile with { Enabled = false }; _credentials = credentials; Provider = provider; }
        internal bool Matches(OcrProviderProfile profile, CredentialResolutionResult credentials) => Current && _fingerprint == (profile with { Enabled = false }) && _credentials == credentials;
        internal void Clear() { Provider = null; _fingerprint = null; _credentials = default; Descriptor = CandidateDescriptor = null; }
    }
}
