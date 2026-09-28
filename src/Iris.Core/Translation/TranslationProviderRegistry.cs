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

    private readonly SemaphoreSlim _refreshSemaphore = new(1, 1);
    private readonly Lock _stateLock = new();

    private Dictionary<string, TrackedInstance> _activeInstances = new(StringComparer.Ordinal);
    private IReadOnlyList<TranslationProviderRegistration> _currentRegistrations = [];
    private IReadOnlyList<TrackedInstance> _currentTrackedInstances = [];
    private AppSettings _currentSettings = new();

    private bool _isInitialized;
    private int _needsRefresh = 1;
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

        // 订阅设置与密钥变化事件：仅标记失效并调度后台刷新，不在事件回调中同步进入锁或等待
        _settingsService.Changed += OnSettingsChanged;
        _secretStore.Changed += OnSecretChanged;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await RefreshInternalAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ITranslationProviderSnapshot> AcquireSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        // 首次调用或标记失效时执行刷新屏障
        if (Volatile.Read(ref _needsRefresh) != 0 || !_isInitialized)
        {
            await RefreshInternalAsync(cancellationToken).ConfigureAwait(false);
        }

        lock (_stateLock)
        {
            ThrowIfDisposed();

            // 为当前快照持有的所有实例增加引用
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
    }

    public Task<ITranslationProviderSnapshot> CreateTransientAsync(
        TranslationProviderProfile profile,
        ProviderCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(credentials);
        cancellationToken.ThrowIfCancellationRequested();

        // 复制并只读封装 ExtraHeaders，隔离调用方草稿后续修改
        var frozenProfile = FreezeDraftProfile(profile);

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

        var registration = new TranslationProviderRegistration(frozenProfile, provider, tracked.InstanceId);
        var snapshot = new TranslationProviderSnapshot(
            _settingsService.Current,
            [registration],
            [tracked],
            _logger);

        return Task.FromResult<ITranslationProviderSnapshot>(snapshot);
    }

    private async Task RefreshInternalAsync(CancellationToken cancellationToken)
    {
        await _refreshSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            Interlocked.Exchange(ref _needsRefresh, 0);

            // 重新在屏障内读取当前设置
            var settings = _settingsService.Current;
            var enabledProfiles = settings.Translation.Providers
                .Where(p => p.Enabled)
                .OrderBy(p => p.Order)
                .ToList();

            var nextActive = new Dictionary<string, TrackedInstance>(StringComparer.Ordinal);
            var nextRegistrations = new List<TranslationProviderRegistration>();
            var nextTracked = new List<TrackedInstance>();
            var newlyCreated = new List<TrackedInstance>();

            try
            {
                foreach (var profile in enabledProfiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var credResult = ProviderCredentialResolver.Resolve(profile, _secretStore, _logger);
                    var fingerprint = new ProviderConstructionFingerprint(profile, credResult.Credentials, credResult.IsExplicitUnconfigured);

                    if (_activeInstances.TryGetValue(profile.Id, out var existing) && existing.Fingerprint.Equals(fingerprint))
                    {
                        // 命中指纹复用：保留原有实例与 InstanceId
                        nextActive[profile.Id] = existing;
                        nextTracked.Add(existing);
                        nextRegistrations.Add(new TranslationProviderRegistration(existing.Profile, existing.Provider, existing.InstanceId));
                    }
                    else
                    {
                        // 构造新代实例
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
                        nextActive[profile.Id] = tracked;
                        nextTracked.Add(tracked);
                        nextRegistrations.Add(new TranslationProviderRegistration(profile, provider, tracked.InstanceId));
                    }
                }
            }
            catch
            {
                // 失败回滚：释放本次已新建的实例，不改变当前注册表发布状态
                foreach (var instance in newlyCreated)
                {
                    instance.DisposeProvider(_logger);
                }
                throw;
            }

            // 原子发布与退休计算
            List<TrackedInstance> instancesToDispose = [];
            bool hasChanged;

            lock (_stateLock)
            {
                ThrowIfDisposed();

                // 计算退休实例：原本在 _activeInstances 中，但在新表中被移除或被新代替换的实例
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
                _currentRegistrations = nextRegistrations;
                _currentTrackedInstances = nextTracked;
                _isInitialized = true;
            }

            // 在注册表锁外清理退休且无快照引用的实例
            foreach (var instance in instancesToDispose)
            {
                instance.DisposeProvider(_logger);
            }

            // 在注册表锁外触发 Changed 事件
            if (hasChanged)
            {
                FireChanged();
            }
        }
        finally
        {
            _refreshSemaphore.Release();
        }
    }

    private static bool HasChanges(
        IReadOnlyList<TranslationProviderRegistration> oldList,
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

        Interlocked.Exchange(ref _needsRefresh, 1);
        ScheduleBackgroundRefresh();
    }

    private void OnSecretChanged(object? sender, string key)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _needsRefresh, 1);
        ScheduleBackgroundRefresh();
    }

    private void ScheduleBackgroundRefresh()
    {
        if (Interlocked.CompareExchange(ref _isBackgroundRefreshScheduled, 1, 0) == 0)
        {
            _ = Task.Run(async () => await ProcessBackgroundRefreshAsync().ConfigureAwait(false));
        }
    }

    private async Task ProcessBackgroundRefreshAsync()
    {
        while (Volatile.Read(ref _needsRefresh) != 0 && Volatile.Read(ref _disposed) == 0)
        {
            try
            {
                await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogBackgroundRefreshFailed(_logger, ex.GetType().Name);
                Interlocked.Exchange(ref _needsRefresh, 0);
                break;
            }
        }

        Interlocked.Exchange(ref _isBackgroundRefreshScheduled, 0);

        if (Volatile.Read(ref _needsRefresh) != 0 && Volatile.Read(ref _disposed) == 0)
        {
            ScheduleBackgroundRefresh();
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
            _currentRegistrations = [];
            _currentTrackedInstances = [];
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
