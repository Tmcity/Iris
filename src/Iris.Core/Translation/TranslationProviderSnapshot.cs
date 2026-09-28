using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using Iris.Core.Common;
using Iris.Core.Security;
using Iris.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Iris.Core.Translation;

/// <summary>
/// 用于比较 Profile 与凭据有效值的构造指纹（specs/PROVIDER_REGISTRY.md §3.2）。
/// Order/Enabled 排除在外；包含 DisplayName、BaseUrl、Model、超时、Token/温度、Options、ExtraHeaders 以及凭据值。
/// </summary>
internal sealed class ProviderConstructionFingerprint : IEquatable<ProviderConstructionFingerprint>
{
    public string Id { get; }
    public TranslationProviderKind Kind { get; }
    public string DisplayName { get; }
    public string? BaseUrl { get; }
    public string Model { get; }
    public int TimeoutSeconds { get; }
    public int? MaxOutputTokens { get; }
    public double? Temperature { get; }
    public string? PromptTemplateId { get; }
    public string? CredentialSetId { get; }
    public OpenAICompatibleOptions? OpenAIOptions { get; }
    public AnthropicOptions? AnthropicOptions { get; }
    public bool IsExplicitUnconfigured { get; }
    public string? ApiKey { get; }
    public string? SecretKey { get; }

    public ProviderConstructionFingerprint(
        TranslationProviderProfile profile,
        ProviderCredentials? credentials,
        bool isExplicitUnconfigured)
    {
        ArgumentNullException.ThrowIfNull(profile);

        Id = profile.Id;
        Kind = profile.Kind;
        DisplayName = profile.DisplayName;
        BaseUrl = profile.BaseUrl;
        Model = profile.Model;
        TimeoutSeconds = profile.TimeoutSeconds;
        MaxOutputTokens = profile.MaxOutputTokens;
        Temperature = profile.Temperature;
        PromptTemplateId = profile.PromptTemplateId;
        CredentialSetId = profile.CredentialSetId;
        OpenAIOptions = profile.OpenAIOptions;
        AnthropicOptions = profile.AnthropicOptions;
        IsExplicitUnconfigured = isExplicitUnconfigured;
        ApiKey = credentials?.ApiKey;
        SecretKey = credentials?.SecretKey;
    }

    public bool Equals(ProviderConstructionFingerprint? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        if (Id != other.Id ||
            Kind != other.Kind ||
            DisplayName != other.DisplayName ||
            BaseUrl != other.BaseUrl ||
            Model != other.Model ||
            TimeoutSeconds != other.TimeoutSeconds ||
            MaxOutputTokens != other.MaxOutputTokens ||
            Nullable.Compare(Temperature, other.Temperature) != 0 ||
            PromptTemplateId != other.PromptTemplateId ||
            CredentialSetId != other.CredentialSetId ||
            IsExplicitUnconfigured != other.IsExplicitUnconfigured ||
            ApiKey != other.ApiKey ||
            SecretKey != other.SecretKey)
        {
            return false;
        }

        if (!OpenAIOptionsEquals(OpenAIOptions, other.OpenAIOptions))
        {
            return false;
        }

        if (!AnthropicOptionsEquals(AnthropicOptions, other.AnthropicOptions))
        {
            return false;
        }

        return true;
    }

    private static bool OpenAIOptionsEquals(OpenAICompatibleOptions? a, OpenAICompatibleOptions? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;

        if (a.Stream != b.Stream ||
            a.IncludeStreamUsage != b.IncludeStreamUsage ||
            a.MaxTokensField != b.MaxTokensField ||
            a.StripThinkTags != b.StripThinkTags ||
            a.ChatCompletionsPath != b.ChatCompletionsPath)
        {
            return false;
        }

        if (a.ExtraHeaders is null && b.ExtraHeaders is null) return true;
        if (a.ExtraHeaders is null || b.ExtraHeaders is null) return false;
        if (a.ExtraHeaders.Count != b.ExtraHeaders.Count) return false;

        return a.ExtraHeaders.SequenceEqual(b.ExtraHeaders);
    }

    private static bool AnthropicOptionsEquals(AnthropicOptions? a, AnthropicOptions? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;

        return a.Effort == b.Effort && a.RefusalFallback == b.RefusalFallback;
    }

    public override bool Equals(object? obj) => Equals(obj as ProviderConstructionFingerprint);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Kind);
        hash.Add(DisplayName);
        hash.Add(BaseUrl);
        hash.Add(Model);
        hash.Add(TimeoutSeconds);
        hash.Add(MaxOutputTokens);
        hash.Add(Temperature);
        hash.Add(PromptTemplateId);
        hash.Add(CredentialSetId);
        hash.Add(IsExplicitUnconfigured);
        hash.Add(ApiKey);
        hash.Add(SecretKey);
        return hash.ToHashCode();
    }
}

/// <summary>
/// 注册表内部跟踪的 Provider 实例代次及其引用计数（specs/PROVIDER_REGISTRY.md §3.3）。
/// 注册表持有一份引用；每个租约快照增加一份引用；退休且引用为零时恰好 Dispose 一次。
/// </summary>
internal sealed partial class TrackedInstance
{
    private readonly Lock _gate = new();
    private int _refCount;
    private bool _retired;
    private bool _disposed;
    private int _providerDisposed;

    public Guid InstanceId { get; }
    public TranslationProviderProfile Profile { get; }
    public ProviderCredentials? Credentials { get; }
    public ITranslationProvider Provider { get; }
    public ProviderConstructionFingerprint Fingerprint { get; }

    public bool IsRetired
    {
        get
        {
            lock (_gate)
            {
                return _retired || _disposed;
            }
        }
    }

    public TrackedInstance(
        TranslationProviderProfile profile,
        ProviderCredentials? credentials,
        ITranslationProvider provider,
        ProviderConstructionFingerprint fingerprint,
        bool initiallyRetired = false)
    {
        InstanceId = Guid.NewGuid();
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        Credentials = credentials;
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        Fingerprint = fingerprint ?? throw new ArgumentNullException(nameof(fingerprint));
        _retired = initiallyRetired;
        _refCount = 1; // 初始拥有者引用（Registry 或 Transient Snapshot）
    }

    public void AddRef()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(TrackedInstance), "Cannot add reference to disposed provider instance.");
            }
            _refCount++;
        }
    }

    public bool ReleaseRef()
    {
        lock (_gate)
        {
            if (_refCount > 0)
            {
                _refCount--;
            }

            if (_refCount == 0 && _retired && !_disposed)
            {
                _disposed = true;
                return true;
            }

            return false;
        }
    }

    public bool MarkRetired()
    {
        lock (_gate)
        {
            _retired = true;
            if (_refCount > 0)
            {
                _refCount--; // 释放注册表所有权引用
            }

            if (_refCount == 0 && !_disposed)
            {
                _disposed = true;
                return true;
            }

            return false;
        }
    }

    public void DisposeProvider(ILogger? logger)
    {
        if (Interlocked.Exchange(ref _providerDisposed, 1) != 0)
        {
            return;
        }

        if (Provider is IDisposable disposable)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                // 日志只记录安全元数据，不包含配置或凭据内容
                if (logger != null) LogDisposeFailed(logger, InstanceId, Profile.Id, Profile.Kind, ex.GetType().Name);
            }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to dispose provider instance {InstanceId} for {ProfileId} ({ProviderKind}): {ExceptionType}")]
    private static partial void LogDisposeFailed(ILogger logger, Guid instanceId, string profileId, TranslationProviderKind providerKind, string exceptionType);
}

/// <summary>
/// 注册表借出的快照租约（specs/PROVIDER_REGISTRY.md §3.1）。
/// 包含捕获的深度不可变 AppSettings 以及按 Order 稳定排序的已启用注册项。
/// 释放租约幂等，所有子任务退出后由消费者释放。
/// </summary>
internal sealed class TranslationProviderSnapshot : ITranslationProviderSnapshot
{
    private readonly AppSettings _settings;
    private readonly ReadOnlyCollection<TranslationProviderRegistration> _providers;
    private readonly ReadOnlyCollection<TrackedInstance> _trackedInstances;
    private readonly ILogger? _logger;
    private int _disposed;

    public TranslationProviderSnapshot(
        AppSettings settings,
        IReadOnlyList<TranslationProviderRegistration> providers,
        IReadOnlyList<TrackedInstance> trackedInstances,
        ILogger? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(trackedInstances);

        _providers = providers as ReadOnlyCollection<TranslationProviderRegistration>
            ?? Array.AsReadOnly(providers.ToArray());
        _trackedInstances = trackedInstances as ReadOnlyCollection<TrackedInstance>
            ?? Array.AsReadOnly(trackedInstances.ToArray());
        _logger = logger;
    }

    public AppSettings Settings
    {
        get
        {
            ThrowIfDisposed();
            return _settings;
        }
    }

    public IReadOnlyList<TranslationProviderRegistration> Providers
    {
        get
        {
            ThrowIfDisposed();
            return _providers;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        List<TrackedInstance> toDispose = [];
        foreach (var instance in _trackedInstances)
        {
            if (instance.ReleaseRef())
            {
                toDispose.Add(instance);
            }
        }

        // 实际 Provider.Dispose 在注册表和快照内部锁之外执行
        foreach (var instance in toDispose)
        {
            instance.DisposeProvider(_logger);
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(TranslationProviderSnapshot), "Snapshot lease has already been disposed.");
        }
    }
}
