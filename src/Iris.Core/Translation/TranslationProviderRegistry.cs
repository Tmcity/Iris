using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Security;
using Iris.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Iris.Core.Translation;

/// <summary>
/// 翻译 Provider 注册表实现（specs/PROVIDER_REGISTRY.md §3、§4）。
/// 负责管理常驻 Provider 实例的构建、复用、快照租约借出、安全退休与释放。
/// </summary>
internal sealed partial class TranslationProviderRegistry : ITranslationProviderRegistry
{
    private readonly ISettingsService _settingsService;
    private readonly ISecretStore _secretStore;
    private readonly Dictionary<TranslationProviderKind, ITranslationProviderFactory> _factoriesByKind;
    private readonly ILogger<TranslationProviderRegistry> _logger;

    private readonly SemaphoreSlim _rebuildLock = new(1, 1);
    private readonly Lock _stateLock = new();

    private Dictionary<string, TrackedInstance> _activeInstances = new(StringComparer.Ordinal);
    private ReadOnlyCollection<TranslationProviderRegistration> _currentRegistrations = Array.AsReadOnly(Array.Empty<TranslationProviderRegistration>());
    private ReadOnlyCollection<TrackedInstance> _currentTrackedInstances = Array.AsReadOnly(Array.Empty<TrackedInstance>());
    private AppSettings _currentSettings = new();

    private long _targetVersion = 1;
    private long _publishedVersion;
    private long _failedVersion;
    private Exception? _lastFailure;
    private bool _isInitialized;

    private int _isBackgroundRefreshScheduled;
    private int _disposed;

    public event EventHandler? Changed;

    public TranslationProviderRegistry(
        ISettingsService settingsService,
        ISecretStore secretStore,
        IEnumerable<ITranslationProviderFactory> factories,
        ILogger<TranslationProviderRegistry> logger)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        ArgumentNullException.ThrowIfNull(factories);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var dict = new Dictionary<TranslationProviderKind, ITranslationProviderFactory>();
        foreach (var factory in factories)
        {
            if (factory is null) continue;
            if (!dict.TryAdd(factory.Kind, factory))
            {
                throw new InvalidOperationException($"Duplicate translation provider factory registered for kind '{factory.Kind}'.");
            }
        }
        _factoriesByKind = dict;

        // 订阅设置与密钥变化事件：仅标记失效代次并调度后台刷新，不在事件回调中同步进入锁或等待
        _settingsService.Changed += OnSettingsChanged;
        _secretStore.Changed += OnSecretChanged;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        long target;
        lock (_stateLock)
        {
            ThrowIfDisposed();
            _targetVersion++;
            _failedVersion = 0;
            _lastFailure = null;
            target = _targetVersion;
        }

        await PerformRebuildAsync(target, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ITranslationProviderSnapshot> AcquireSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        while (true)
        {
            long target;
            lock (_stateLock)
            {
                ThrowIfDisposed();

                if (_isInitialized && _publishedVersion >= _targetVersion)
                {
                    foreach (var instance in _currentTrackedInstances)
                    {
                        instance.AddRef();
                    }

                    return new TranslationProviderSnapshot(
                        _currentSettings,
                        _currentRegistrations,
                        _currentTrackedInstances,
                        _logger);
                }

                if (_failedVersion == _targetVersion && _lastFailure != null)
                {
                    throw new IrisException(
                        ErrorCode.Unknown,
                        "Translation provider registry configuration failed.",
                        innerException: null);
                }

                target = _targetVersion;
            }

            await PerformRebuildAsync(target, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<ITranslationProviderSnapshot> CreateTransientAsync(
        TranslationProviderProfile profile,
        ProviderCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(credentials);
        cancellationToken.ThrowIfCancellationRequested();

        // 1. 同步在调用线程捕获草稿与设置快照（防延迟捕获别名竞态）
        var frozenProfile = FreezeDraftProfile(profile);
        var currentSettings = _settingsService.Current;

        // 2. 调度到后台线程池执行构造
        return await Task.Run(() =>
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            if (!_factoriesByKind.TryGetValue(frozenProfile.Kind, out var factory))
            {
                throw new IrisException(ErrorCode.Unknown, $"No factory registered for provider kind '{frozenProfile.Kind}'.");
            }

            ITranslationProvider provider;
            try
            {
                provider = factory.Create(frozenProfile, credentials);
            }
            catch (Exception ex)
            {
                LogTransientCreateFailed(_logger, frozenProfile.Id, frozenProfile.Kind, ex.GetType().Name);
                throw new IrisException(ErrorCode.Unknown, "Failed to create transient provider.", innerException: null);
            }

            var fingerprint = new ProviderConstructionFingerprint(frozenProfile, credentials, isExplicitUnconfigured: false);
            var tracked = new TrackedInstance(frozenProfile, credentials, provider, fingerprint, initiallyRetired: true);

            var handedOver = false;
            try
            {
                ThrowIfDisposed();
                cancellationToken.ThrowIfCancellationRequested();

                var registration = new TranslationProviderRegistration(frozenProfile, provider, tracked.InstanceId);
                var snapshot = new TranslationProviderSnapshot(
                    currentSettings,
                    Array.AsReadOnly([registration]),
                    Array.AsReadOnly([tracked]),
                    _logger);

                handedOver = true;
                return (ITranslationProviderSnapshot)snapshot;
            }
            finally
            {
                if (!handedOver)
                {
                    tracked.DisposeProvider(_logger);
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task PerformRebuildAsync(long targetVersion, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        await _rebuildLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            long currentTarget;
            AppSettings settings;
            lock (_stateLock)
            {
                ThrowIfDisposed();
                if (_isInitialized && _publishedVersion >= targetVersion)
                {
                    return;
                }

                if (_failedVersion == targetVersion && _lastFailure != null)
                {
                    throw new IrisException(
                        ErrorCode.Unknown,
                        "Translation provider registry configuration failed.",
                        innerException: null);
                }

                currentTarget = _targetVersion;
                settings = _settingsService.Current;
            }

            await Task.Run(() => DoRebuildWork(currentTarget, settings, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _rebuildLock.Release();
        }
    }

    private void DoRebuildWork(long targetVersion, AppSettings settings, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var enabledProfiles = settings.Translation.Providers
            .Where(p => p.Enabled)
            .OrderBy(p => p.Order)
            .ToList();

        var nextActive = new Dictionary<string, TrackedInstance>(StringComparer.Ordinal);
        var nextRegistrations = new List<TranslationProviderRegistration>();
        var nextTracked = new List<TrackedInstance>();
        var newlyCreated = new List<TrackedInstance>();
        var committed = false;

        try
        {
            foreach (var profile in enabledProfiles)
            {
                ThrowIfDisposed();
                cancellationToken.ThrowIfCancellationRequested();

                var credResult = ProviderCredentialResolver.Resolve(profile, _secretStore, _logger);
                var fingerprint = new ProviderConstructionFingerprint(profile, credResult.Credentials, credResult.IsExplicitUnconfigured);

                TrackedInstance? existing = null;
                lock (_stateLock)
                {
                    ThrowIfDisposed();
                    if (_activeInstances.TryGetValue(profile.Id, out var candidate) &&
                        candidate.Fingerprint.Equals(fingerprint) &&
                        !candidate.IsRetired)
                    {
                        existing = candidate;
                    }
                }

                if (existing != null)
                {
                    // 复用已有实例：使用本次更新的 Profile，保留同一 Provider 与 InstanceId
                    nextActive[profile.Id] = existing;
                    nextTracked.Add(existing);
                    nextRegistrations.Add(new TranslationProviderRegistration(profile, existing.Provider, existing.InstanceId));
                }
                else
                {
                    ThrowIfDisposed();
                    cancellationToken.ThrowIfCancellationRequested();

                    ITranslationProvider provider;
                    if (credResult.IsExplicitUnconfigured)
                    {
                        provider = new UnavailableTranslationProvider(profile);
                    }
                    else
                    {
                        if (!_factoriesByKind.TryGetValue(profile.Kind, out var factory))
                        {
                            throw new IrisException(ErrorCode.Unknown, $"No factory registered for provider kind '{profile.Kind}'.");
                        }

                        try
                        {
                            provider = factory.Create(profile, credResult.Credentials!);
                        }
                        catch (Exception ex)
                        {
                            LogFactoryCreateFailed(_logger, profile.Id, profile.Kind, ex.GetType().Name);
                            throw new IrisException(ErrorCode.Unknown, "Failed to create provider.", innerException: null);
                        }
                    }

                    var tracked = new TrackedInstance(profile, credResult.Credentials, provider, fingerprint);
                    newlyCreated.Add(tracked);

                    // 同步 Factory 调用返回后检查取消与 disposed
                    ThrowIfDisposed();
                    cancellationToken.ThrowIfCancellationRequested();

                    nextActive[profile.Id] = tracked;
                    nextTracked.Add(tracked);
                    nextRegistrations.Add(new TranslationProviderRegistration(profile, provider, tracked.InstanceId));
                }
            }

            // 最终发布检查
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            List<TrackedInstance> instancesToDispose = [];
            bool hasChanged;

            lock (_stateLock)
            {
                ThrowIfDisposed();
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var (id, active) in _activeInstances)
                {
                    if (!nextActive.TryGetValue(id, out var currentInNext) || !ReferenceEquals(currentInNext, active))
                    {
                        if (active.MarkRetired())
                        {
                            instancesToDispose.Add(active);
                        }
                    }
                }

                hasChanged = HasChanges(_currentRegistrations, nextRegistrations);

                _currentSettings = settings;
                _activeInstances = nextActive;
                _currentRegistrations = Array.AsReadOnly(nextRegistrations.ToArray());
                _currentTrackedInstances = Array.AsReadOnly(nextTracked.ToArray());
                _publishedVersion = targetVersion;
                _isInitialized = true;
                committed = true;
            }

            foreach (var instance in instancesToDispose)
            {
                instance.DisposeProvider(_logger);
            }

            if (hasChanged)
            {
                FireChanged();
            }
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException && ex is not ObjectDisposedException)
            {
                lock (_stateLock)
                {
                    _failedVersion = targetVersion;
                    _lastFailure = ex;
                }
            }
            throw;
        }
        finally
        {
            if (!committed)
            {
                foreach (var instance in newlyCreated)
                {
                    instance.DisposeProvider(_logger);
                }
            }
        }
    }

    private static bool HasChanges(
        ReadOnlyCollection<TranslationProviderRegistration> oldList,
        List<TranslationProviderRegistration> newList)
    {
        if (oldList.Count != newList.Count)
        {
            return true;
        }

        for (var i = 0; i < oldList.Count; i++)
        {
            var oldItem = oldList[i];
            var newItem = newList[i];

            if (oldItem.InstanceId != newItem.InstanceId ||
                oldItem.Profile.Order != newItem.Profile.Order ||
                oldItem.Profile.DisplayName != newItem.Profile.DisplayName ||
                oldItem.Provider.Descriptor.IsConfigured != newItem.Provider.Descriptor.IsConfigured)
            {
                return true;
            }
        }

        return false;
    }

    private void FireChanged()
    {
        var handler = Changed;
        if (handler == null)
        {
            return;
        }

        foreach (var invocation in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler)invocation).Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                LogChangedHandlerFailed(_logger, ex.GetType().Name);
            }
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        lock (_stateLock)
        {
            _targetVersion++;
            _failedVersion = 0;
            _lastFailure = null;
        }

        ScheduleBackgroundRefresh();
    }

    private void OnSecretChanged(object? sender, string key)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        lock (_stateLock)
        {
            _targetVersion++;
            _failedVersion = 0;
            _lastFailure = null;
        }

        ScheduleBackgroundRefresh();
    }

    private void ScheduleBackgroundRefresh()
    {
        if (Interlocked.CompareExchange(ref _isBackgroundRefreshScheduled, 1, 0) == 0)
        {
            _ = Task.Run(ProcessBackgroundRefreshAsync);
        }
    }

    private async Task ProcessBackgroundRefreshAsync()
    {
        try
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                long currentTarget;
                lock (_stateLock)
                {
                    if ((_isInitialized && _publishedVersion >= _targetVersion) ||
                        _failedVersion == _targetVersion)
                    {
                        break;
                    }
                    currentTarget = _targetVersion;
                }

                try
                {
                    await PerformRebuildAsync(currentTarget, CancellationToken.None).ConfigureAwait(false);
                }
                catch (IrisException ex)
                {
                    LogBackgroundRefreshFailed(_logger, ex.GetType().Name);
                    break;
                }
                catch (Exception ex) when (ex is not ObjectDisposedException)
                {
                    LogBackgroundRefreshFailed(_logger, ex.GetType().Name);
                    break;
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isBackgroundRefreshScheduled, 0);

            bool shouldReschedule = false;
            lock (_stateLock)
            {
                if (Volatile.Read(ref _disposed) == 0 &&
                    (!_isInitialized || _publishedVersion < _targetVersion) &&
                    _failedVersion != _targetVersion)
                {
                    shouldReschedule = true;
                }
            }

            if (shouldReschedule)
            {
                ScheduleBackgroundRefresh();
            }
        }
    }

    private static TranslationProviderProfile FreezeDraftProfile(TranslationProviderProfile profile)
    {
        if (profile.OpenAIOptions?.ExtraHeaders == null)
        {
            return profile;
        }

        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in profile.OpenAIOptions.ExtraHeaders)
        {
            headers[pair.Key] = pair.Value;
        }

        var frozenHeaders = new ReadOnlyDictionary<string, string>(headers);
        return profile with
        {
            OpenAIOptions = profile.OpenAIOptions with
            {
                ExtraHeaders = frozenHeaders
            }
        };
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(TranslationProviderRegistry), "Registry has been disposed.");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _settingsService.Changed -= OnSettingsChanged;
        _secretStore.Changed -= OnSecretChanged;

        List<TrackedInstance> instancesToDispose = [];
        lock (_stateLock)
        {
            foreach (var instance in _activeInstances.Values)
            {
                if (instance.MarkRetired())
                {
                    instancesToDispose.Add(instance);
                }
            }
            _activeInstances.Clear();
            _currentRegistrations = Array.AsReadOnly(Array.Empty<TranslationProviderRegistration>());
            _currentTrackedInstances = Array.AsReadOnly(Array.Empty<TrackedInstance>());
        }

        foreach (var instance in instancesToDispose)
        {
            instance.DisposeProvider(_logger);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to create transient provider for {ProfileId} ({ProviderKind}): {ExceptionType}")]
    private static partial void LogTransientCreateFailed(ILogger logger, string profileId, TranslationProviderKind providerKind, string exceptionType);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Factory failed to create provider for {ProfileId} ({ProviderKind}): {ExceptionType}")]
    private static partial void LogFactoryCreateFailed(ILogger logger, string profileId, TranslationProviderKind providerKind, string exceptionType);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "TranslationProviderRegistry Changed event handler threw an exception: {ExceptionType}")]
    private static partial void LogChangedHandlerFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Background refresh of translation provider registry failed. Waiting for next update: {ExceptionType}")]
    private static partial void LogBackgroundRefreshFailed(ILogger logger, string exceptionType);
}
