using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Common;
using Iris.Core.DependencyInjection;
using Iris.Core.Errors;
using Iris.Core.Presentation;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.Core.Tests.Settings;

public sealed class SettingsEditCoordinatorTests
{
    #region Test Fakes & Mocks

    private sealed class TestSettingsService : ISettingsService
    {
        private AppSettings _current;
        public int UpdateCallCount { get; private set; }
        public Func<AppSettings, AppSettings>? MutateInterceptor { get; set; }
        public Func<AppSettings, AppSettings>? BeforeMutateHook { get; set; }
        public Func<CancellationToken, Task>? BeforeUpdateHookAsync { get; set; }
        public Func<AppSettings, Task>? AfterUpdateHookAsync { get; set; }
        public bool UpdateTokenCanCancel { get; private set; }

        public TestSettingsService(AppSettings initial)
        {
            _current = initial;
        }

        public Exception? CurrentFailure { get; set; }
        public AppSettings Current => CurrentFailure != null ? throw CurrentFailure : _current;

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default)
        {
            UpdateTokenCanCancel = cancellationToken.CanBeCanceled;
            if (BeforeUpdateHookAsync != null)
            {
                await BeforeUpdateHookAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();

            UpdateCallCount++;
            var old = _current;
            if (BeforeMutateHook != null)
            {
                _current = BeforeMutateHook(_current);
                old = _current;
            }
            var next = mutate(old);
            if (MutateInterceptor != null)
            {
                next = MutateInterceptor(next);
            }
            _current = next;
            Changed?.Invoke(this, new SettingsChangedEventArgs(old, _current));

            if (AfterUpdateHookAsync != null)
            {
                await AfterUpdateHookAsync(_current);
            }
        }

        public event EventHandler<SettingsChangedEventArgs>? Changed;
    }

    private sealed class TestSecretStore : ISecretStore
    {
        public ConcurrentDictionary<string, string> Storage { get; } = new(StringComparer.Ordinal);
        public List<string> SetCalls { get; } = new();
        public List<string> RemoveCalls { get; } = new();
        public int CallerThreadId { get; set; }
        public bool ReadOnCallerThread { get; private set; }

        public Func<string, string, bool>? FailOnSet { get; set; }
        public Func<string, bool>? FailOnRemove { get; set; }
        public Action<string>? FailOnGet { get; set; }
        public Action<string>? OnSetHook { get; set; }
        public Action<string>? OnGetHook { get; set; }

        public string? Get(string key)
        {
            if (CallerThreadId != 0 && Environment.CurrentManagedThreadId == CallerThreadId)
            {
                ReadOnCallerThread = true;
            }
            OnGetHook?.Invoke(key);
            FailOnGet?.Invoke(key);
            Storage.TryGetValue(key, out var val);
            return val;
        }

        public void Set(string key, string value)
        {
            SetCalls.Add(key);
            OnSetHook?.Invoke(key);
            if (FailOnSet != null && FailOnSet(key, value))
            {
                throw new InvalidOperationException($"Simulated secret store failure on Set key: {key}");
            }
            Storage[key] = value;
            Changed?.Invoke(this, key);
        }

        public bool Remove(string key)
        {
            RemoveCalls.Add(key);
            if (FailOnRemove != null && FailOnRemove(key))
            {
                throw new InvalidOperationException($"Simulated secret store failure on Remove key: {key}");
            }
            var removed = Storage.TryRemove(key, out _);
            if (removed)
            {
                Changed?.Invoke(this, key);
            }
            return removed;
        }

        public event EventHandler<string>? Changed;
    }

    private sealed class TestConfigurationObserver : IProviderConfigurationObserver
    {
        public int RefreshCount { get; private set; }
        public bool Fail { get; set; }
        public Func<CancellationToken, Task>? OnRefreshAsync { get; set; }

        public async Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            if (Fail)
            {
                throw new InvalidOperationException("Simulated observer refresh failure");
            }
            if (OnRefreshAsync != null)
            {
                await OnRefreshAsync(cancellationToken);
            }
        }
    }

    private sealed class TestTranslationProvider : ITranslationProvider, IModelCatalogSource, IDisposable
    {
        public ProviderDescriptor Descriptor { get; }
        public TranslationProviderProfile Profile { get; }
        public ProviderCredentials Credentials { get; }
        public bool IsDisposed { get; private set; }
        public int TestCallCount { get; private set; }
        public int ListModelsCallCount { get; private set; }
        public Func<CancellationToken, Task<ProviderTestResult>>? OnTestAsync { get; set; }
        public Func<CancellationToken, Task<IReadOnlyList<string>>>? OnListModelsAsync { get; set; }

        public TestTranslationProvider(TranslationProviderProfile profile, ProviderCredentials credentials)
        {
            Profile = profile;
            Credentials = credentials;
            Descriptor = new ProviderDescriptor(
                profile.Id,
                profile.DisplayName,
                profile.Kind.ToString(),
                profile.Model,
                IsConfigured: !string.IsNullOrWhiteSpace(credentials.ApiKey) && !string.IsNullOrWhiteSpace(profile.Model));
        }

        public async IAsyncEnumerable<TranslationChunk> TranslateStreamAsync(
            ProviderTranslateRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (!Descriptor.IsConfigured)
            {
                throw new IrisException(ErrorCode.NotConfigured, "Provider not configured", retryable: false, providerId: Profile.Id);
            }
            await Task.Yield();
            yield return new TranslationChunk("translated text");
        }

        public async Task<ProviderTestResult> TestAsync(CancellationToken cancellationToken)
        {
            TestCallCount++;
            if (!Descriptor.IsConfigured)
            {
                return new ProviderTestResult(false, TimeSpan.FromMilliseconds(10), Error: new ErrorInfo(ErrorCode.NotConfigured, "Provider not configured"));
            }
            if (OnTestAsync != null)
            {
                return await OnTestAsync(cancellationToken);
            }
            return new ProviderTestResult(true, TimeSpan.FromMilliseconds(50), "Hello test");
        }

        public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken)
        {
            ListModelsCallCount++;
            if (string.IsNullOrWhiteSpace(Credentials.ApiKey) && !IsLocalAddress(Profile.BaseUrl))
            {
                throw new IrisException(
                    ErrorCode.NotConfigured,
                    "Remote provider missing API key",
                    retryable: false,
                    providerId: Profile.Id);
            }
            if (OnListModelsAsync != null)
            {
                return await OnListModelsAsync(cancellationToken);
            }
            return new List<string> { "model-1", "model-2" };
        }

        private static bool IsLocalAddress(string? url)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }
            return uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    private sealed class TestTranslationProviderWithoutCatalog : ITranslationProvider, IDisposable
    {
        public ProviderDescriptor Descriptor { get; }
        public TranslationProviderProfile Profile { get; }
        public ProviderCredentials Credentials { get; }
        public bool IsDisposed { get; private set; }

        public TestTranslationProviderWithoutCatalog(TranslationProviderProfile profile, ProviderCredentials credentials)
        {
            Profile = profile;
            Credentials = credentials;
            Descriptor = new ProviderDescriptor(
                profile.Id,
                profile.DisplayName,
                profile.Kind.ToString(),
                profile.Model,
                IsConfigured: !string.IsNullOrWhiteSpace(credentials.ApiKey));
        }

        public async IAsyncEnumerable<TranslationChunk> TranslateStreamAsync(
            ProviderTranslateRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new TranslationChunk("translated");
        }

        public Task<ProviderTestResult> TestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderTestResult(true, TimeSpan.FromMilliseconds(10), "ok"));

        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    private sealed class TestProviderSnapshot : ITranslationProviderSnapshot
    {
        public AppSettings Settings { get; }
        public IReadOnlyList<TranslationProviderRegistration> Providers { get; }
        public bool IsDisposed { get; private set; }
        public Action? OnDispose { get; set; }

        public TestProviderSnapshot(AppSettings settings, IReadOnlyList<TranslationProviderRegistration> providers)
        {
            Settings = settings;
            Providers = providers;
        }

        public void Dispose()
        {
            IsDisposed = true;
            OnDispose?.Invoke();
        }
    }

    private sealed class TestTranslationProviderRegistry : ITranslationProviderRegistry
    {
        private readonly ISettingsService _settingsService;
        public int RefreshCount { get; private set; }
        public List<TestProviderSnapshot> CreatedSnapshots { get; } = new();
        public Func<TranslationProviderProfile, ProviderCredentials, ITranslationProvider>? ProviderFactory { get; set; }

        public TestTranslationProviderRegistry(ISettingsService settingsService)
        {
            _settingsService = settingsService;
        }

        public Task<ITranslationProviderSnapshot> AcquireSnapshotAsync(CancellationToken cancellationToken = default)
        {
            var registrations = _settingsService.Current.Translation.Providers.Select(p =>
            {
                var prov = ProviderFactory != null
                    ? ProviderFactory(p, ProviderCredentials.None)
                    : new TestTranslationProvider(p, ProviderCredentials.None);
                return new TranslationProviderRegistration(p, prov, Guid.NewGuid());
            }).ToList();

            var snap = new TestProviderSnapshot(_settingsService.Current, registrations);
            CreatedSnapshots.Add(snap);
            return Task.FromResult<ITranslationProviderSnapshot>(snap);
        }

        public Task<ITranslationProviderSnapshot> CreateTransientAsync(
            TranslationProviderProfile profile,
            ProviderCredentials credentials,
            CancellationToken cancellationToken = default)
        {
            var prov = ProviderFactory != null
                ? ProviderFactory(profile, credentials)
                : new TestTranslationProvider(profile, credentials);

            var reg = new TranslationProviderRegistration(profile, prov, Guid.NewGuid());
            var snap = new TestProviderSnapshot(_settingsService.Current, new[] { reg });
            CreatedSnapshots.Add(snap);
            return Task.FromResult<ITranslationProviderSnapshot>(snap);
        }

        public Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public event EventHandler? Changed;

        public void Dispose() { }
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }

    #endregion

    private static AppSettings CreateInitialSettings(
        out string setIdOpenAI,
        out string setIdAnthropic)
    {
        setIdOpenAI = "cs-" + Guid.NewGuid().ToString("N");
        setIdAnthropic = "cs-" + Guid.NewGuid().ToString("N");

        return new AppSettings
        {
            General = new GeneralSettings
            {
                Theme = AppTheme.System,
                LaunchAtStartup = false,
                ShowStartupNotification = true,
                LogLevel = "Information"
            },
            Translation = new TranslationSettings
            {
                Providers = new List<TranslationProviderProfile>
                {
                    new()
                    {
                        Id = "tp-openai",
                        DisplayName = "OpenAI",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        BaseUrl = "https://api.openai.com/v1",
                        Model = "gpt-4o",
                        CredentialSetId = setIdOpenAI,
                        Order = 0,
                        Enabled = true
                    },
                    new()
                    {
                        Id = "tp-anthropic",
                        DisplayName = "Anthropic",
                        Kind = TranslationProviderKind.Anthropic,
                        BaseUrl = "",
                        Model = "claude-sonnet-5",
                        CredentialSetId = setIdAnthropic,
                        Order = 1,
                        Enabled = true
                    }
                }
            }
        };
    }

    // =========================================================================
    // 场景 1: 常规补丁 + 多 Profile + 排序
    // 关键断言: 恰好一次发布，未编辑字段取最新，Current/Registry 结果一致
    // =========================================================================
    [Fact]
    public async Task Scenario1_GeneralPatch_MultipleProfiles_Ordering_SinglePublish_And_SyncSnapshot()
    {
        var initial = CreateInitialSettings(out var openAiSetId, out var anthropicSetId);
        var settingsService = new TestSettingsService(initial);
        var secretStore = new TestSecretStore();
        secretStore.Set(SecretKeys.CredentialSet(openAiSetId), "{\"format\":\"iris-provider-credentials\",\"version\":1,\"apiKey\":\"sk-openai\",\"secretKey\":null}");
        secretStore.Set(SecretKeys.CredentialSet(anthropicSetId), "{\"format\":\"iris-provider-credentials\",\"version\":1,\"apiKey\":\"sk-anthropic\",\"secretKey\":null}");

        var registry = new TestTranslationProviderRegistry(settingsService);
        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { registry },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        const string newProfileId = "tp-000000000001";
        var plan = new SettingsEditPlan(
            General: new GeneralSettingsPatch(
                Theme: null, // 未编辑
                LaunchAtStartup: null, // 未编辑
                ShowStartupNotification: null, // 未编辑
                LogLevel: new SettingsFieldEdit<string>("Information", "Warning")),
            TranslationChanges: new[]
            {
                // Add: new profile
                new TranslationProfileChange(
                    TranslationProfileChangeKind.Add,
                    BaseProfile: null,
                    TargetProfile: new TranslationProviderProfile
                    {
                        Id = newProfileId,
                        DisplayName = "New Provider",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        BaseUrl = "https://api.openai.com/v1",
                        Model = "gpt-4o",
                        Enabled = true
                    },
                    Credentials: new ProviderCredentialEdit(CredentialEditKind.Replace, new ProviderCredentials("sk-new", null))),
                // Update: tp-openai Keep
                new TranslationProfileChange(
                    TranslationProfileChangeKind.Update,
                    BaseProfile: initial.Translation.Providers[0],
                    TargetProfile: initial.Translation.Providers[0] with { Model = "gpt-4o-mini" },
                    Credentials: new ProviderCredentialEdit(CredentialEditKind.Keep)),
                // Delete: tp-anthropic
                new TranslationProfileChange(
                    TranslationProfileChangeKind.Delete,
                    BaseProfile: initial.Translation.Providers[1],
                    TargetProfile: null,
                    Credentials: new ProviderCredentialEdit(CredentialEditKind.Keep))
            },
            TranslationOrder: new TranslationOrderChange(
                BaseIds: new[] { "tp-openai", "tp-anthropic" },
                TargetIds: new[] { newProfileId, "tp-openai" }));

        var result = await coordinator.CommitAsync(plan, TestContext.Current.CancellationToken);

        // 1. 恰好一次发布
        result.Status.ShouldBe(SettingsEditStatus.Applied);
        settingsService.UpdateCallCount.ShouldBe(1);

        // 2. 未编辑字段取最新
        var current = settingsService.Current;
        current.General.Theme.ShouldBe(AppTheme.System);
        current.General.LaunchAtStartup.ShouldBeFalse();
        current.General.ShowStartupNotification.ShouldBeTrue();
        current.General.LogLevel.ShouldBe("Warning");

        // 3. Current / Registry 结果一致
        current.Translation.Providers.Count.ShouldBe(2);
        current.Translation.Providers[0].Id.ShouldBe(newProfileId);
        current.Translation.Providers[0].Order.ShouldBe(0);
        current.Translation.Providers[0].CredentialSetId.ShouldStartWith("cs-");

        current.Translation.Providers[1].Id.ShouldBe("tp-openai");
        current.Translation.Providers[1].Order.ShouldBe(1);
        current.Translation.Providers[1].Model.ShouldBe("gpt-4o-mini");
        current.Translation.Providers[1].CredentialSetId.ShouldBe(openAiSetId);

        // 4. 旧凭据已被清理，新凭据已写入
        secretStore.Get(SecretKeys.CredentialSet(anthropicSetId)).ShouldBeNull();
        secretStore.Get(SecretKeys.CredentialSet(current.Translation.Providers[0].CredentialSetId!)).ShouldNotBeNull();
        secretStore.Get(SecretKeys.CredentialSet(openAiSetId)).ShouldNotBeNull();

        // 5. Registry 观察者被调用，快照与 Current 一致
        registry.RefreshCount.ShouldBe(1);
        var snapshot = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);
        snapshot.Providers.Select(p => p.Profile.Id).ShouldBe(new[] { newProfileId, "tp-openai" });
    }

    // =========================================================================
    // 场景 2: 字典/列表排队后被调用方修改
    // 关键断言: 实际提交与网络调用使用已捕获值
    // =========================================================================
    [Fact]
    public async Task Scenario2_QueuedInputMutatedByCaller_UsesCapturedValues()
    {
        var initial = CreateInitialSettings(out _, out _);
        var settingsService = new TestSettingsService(initial);
        var secretStore = new TestSecretStore();
        var registry = new TestTranslationProviderRegistry(settingsService);
        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { registry },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        const string newProfileId = "tp-000000000002";
        var headers = new Dictionary<string, string> { { "X-Custom", "OriginalValue" } };
        var targetProfile = new TranslationProviderProfile
        {
            Id = newProfileId,
            DisplayName = "Captured Test",
            Kind = TranslationProviderKind.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o",
            OpenAIOptions = new OpenAICompatibleOptions
            {
                ExtraHeaders = headers
            },
            Enabled = true
        };

        var changesList = new List<TranslationProfileChange>
        {
            new(TranslationProfileChangeKind.Add,
                BaseProfile: null,
                TargetProfile: targetProfile,
                Credentials: new ProviderCredentialEdit(CredentialEditKind.Replace, new ProviderCredentials("sk-test", null)))
        };

        var orderTargetIds = new List<string> { "tp-openai", "tp-anthropic", newProfileId };
        var plan = new SettingsEditPlan(
            General: new GeneralSettingsPatch(),
            TranslationChanges: changesList,
            TranslationOrder: new TranslationOrderChange(
                BaseIds: new[] { "tp-openai", "tp-anthropic" },
                TargetIds: orderTargetIds));

        // 调用入口同步捕获后，调用方修改原始字典与列表
        var commitTask = coordinator.CommitAsync(plan, TestContext.Current.CancellationToken);

        headers["X-Custom"] = "ModifiedValue";
        headers["X-Injected"] = "InjectedValue";
        changesList.Clear();
        orderTargetIds.Clear();

        var result = await commitTask;
        result.Status.ShouldBe(SettingsEditStatus.Applied);

        var savedProfile = settingsService.Current.Translation.Providers.Single(p => p.Id == newProfileId);
        savedProfile.OpenAIOptions.ShouldNotBeNull();
        savedProfile.OpenAIOptions.ExtraHeaders.ShouldNotBeNull();
        savedProfile.OpenAIOptions.ExtraHeaders["X-Custom"].ShouldBe("OriginalValue");
        savedProfile.OpenAIOptions.ExtraHeaders.ContainsKey("X-Injected").ShouldBeFalse();
    }

    // =========================================================================
    // 场景 3: 第二个凭据 Set 失败 / staging 后冲突或取消
    // 关键断言: 配置/旧凭据不变，本次全部无引用 staged 值尽力回收
    // =========================================================================
    [Fact]
    public async Task Scenario3a_SecondCredentialSetFails_RollsBackAllStaged_SettingsUnchanged()
    {
        var initial = CreateInitialSettings(out var openAiSetId, out var anthropicSetId);
        var settingsService = new TestSettingsService(initial);
        var secretStore = new TestSecretStore();
        secretStore.Set(SecretKeys.CredentialSet(openAiSetId), "{\"format\":\"iris-provider-credentials\",\"version\":1,\"apiKey\":\"sk-openai\",\"secretKey\":null}");

        // 模拟写入第二个凭据时抛出存储异常
        var setCount = 0;
        secretStore.FailOnSet = (key, val) =>
        {
            setCount++;
            return setCount >= 2;
        };

        var registry = new TestTranslationProviderRegistry(settingsService);
        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { registry },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        var plan = new SettingsEditPlan(
            General: new GeneralSettingsPatch(),
            TranslationChanges: new[]
            {
                new TranslationProfileChange(
                    TranslationProfileChangeKind.Add,
                    BaseProfile: null,
                    TargetProfile: new TranslationProviderProfile
                    {
                        Id = "tp-00000000000a",
                        DisplayName = "P1",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        BaseUrl = "https://api.openai.com/v1",
                        Model = "gpt-4o",
                        Enabled = true
                    },
                    Credentials: new ProviderCredentialEdit(CredentialEditKind.Replace, new ProviderCredentials("sk-1", null))),
                new TranslationProfileChange(
                    TranslationProfileChangeKind.Add,
                    BaseProfile: null,
                    TargetProfile: new TranslationProviderProfile
                    {
                        Id = "tp-00000000000b",
                        DisplayName = "P2",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        BaseUrl = "https://api.openai.com/v1",
                        Model = "gpt-4o",
                        Enabled = true
                    },
                    Credentials: new ProviderCredentialEdit(CredentialEditKind.Replace, new ProviderCredentials("sk-2", null)))
            },
            TranslationOrder: new TranslationOrderChange(
                BaseIds: new[] { "tp-openai", "tp-anthropic" },
                TargetIds: new[] { "tp-openai", "tp-anthropic", "tp-00000000000a", "tp-00000000000b" }));

        var result = await coordinator.CommitAsync(plan, TestContext.Current.CancellationToken);

        // 1. 失败且未提交
        result.Status.ShouldBe(SettingsEditStatus.Uncommitted);
        result.IsConflict.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        settingsService.UpdateCallCount.ShouldBe(0);

        // 2. 本次第一个 staged 值已被回滚回收，旧凭据不变
        secretStore.RemoveCalls.Count.ShouldBeGreaterThan(0);
        secretStore.Get(SecretKeys.CredentialSet(openAiSetId)).ShouldNotBeNull();
        secretStore.Storage.Count(kv => kv.Key.StartsWith("credential-set/cs-", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task Scenario3b_StagingSucceeds_ConflictDuringUpdate_RollsBackAllStaged()
    {
        var initial = CreateInitialSettings(out var openAiSetId, out _);
        var settingsService = new TestSettingsService(initial);
        var secretStore = new TestSecretStore();
        secretStore.Set(SecretKeys.CredentialSet(openAiSetId), "{\"format\":\"iris-provider-credentials\",\"version\":1,\"apiKey\":\"sk-openai\",\"secretKey\":null}");

        // 在 UpdateAsync.mutate 执行前使配置发生并发变化，触发 mutate 内冲突复验
        settingsService.BeforeMutateHook = _ =>
        {
            // 并发在外部删除了一个 profile
            return initial with
            {
                Translation = initial.Translation with
                {
                    Providers = new List<TranslationProviderProfile> { initial.Translation.Providers[0] }
                }
            };
        };

        var registry = new TestTranslationProviderRegistry(settingsService);
        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { registry },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        const string newId = "tp-000000000003";
        var plan = new SettingsEditPlan(
            General: new GeneralSettingsPatch(),
            TranslationChanges: new[]
            {
                new TranslationProfileChange(
                    TranslationProfileChangeKind.Add,
                    BaseProfile: null,
                    TargetProfile: new TranslationProviderProfile
                    {
                        Id = newId,
                        DisplayName = "P3",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        BaseUrl = "https://api.openai.com/v1",
                        Model = "gpt-4o",
                        Enabled = true
                    },
                    Credentials: new ProviderCredentialEdit(CredentialEditKind.Replace, new ProviderCredentials("sk-3", null)))
            },
            TranslationOrder: new TranslationOrderChange(
                BaseIds: new[] { "tp-openai", "tp-anthropic" },
                TargetIds: new[] { "tp-openai", "tp-anthropic", newId }));

        var result = await coordinator.CommitAsync(plan, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(SettingsEditStatus.Uncommitted);
        result.IsConflict.ShouldBeTrue();

        // staged 凭据已被回收
        secretStore.RemoveCalls.Count.ShouldBe(1);
        secretStore.Storage.Count(kv => kv.Key.StartsWith("credential-set/cs-", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task Scenario3c_CancellationDuringStaging_RollsBackAllStaged_ThrowsOCE()
    {
        var initial = CreateInitialSettings(out _, out _);
        var settingsService = new TestSettingsService(initial);
        var secretStore = new TestSecretStore();
        var registry = new TestTranslationProviderRegistry(settingsService);
        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        using var cts = new CancellationTokenSource();
        // 第一次 Set 后立即触发取消
        secretStore.OnSetHook = _ => cts.Cancel();

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { registry },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        var plan = new SettingsEditPlan(
            General: new GeneralSettingsPatch(),
            TranslationChanges: new[]
            {
                new TranslationProfileChange(
                    TranslationProfileChangeKind.Add,
                    BaseProfile: null,
                    TargetProfile: new TranslationProviderProfile
                    {
                        Id = "tp-000000000004",
                        DisplayName = "P4",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        BaseUrl = "https://api.openai.com/v1",
                        Model = "gpt-4o",
                        Enabled = true
                    },
                    Credentials: new ProviderCredentialEdit(CredentialEditKind.Replace, new ProviderCredentials("sk-4", null))),
                new TranslationProfileChange(
                    TranslationProfileChangeKind.Add,
                    BaseProfile: null,
                    TargetProfile: new TranslationProviderProfile
                    {
                        Id = "tp-000000000005",
                        DisplayName = "P5",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        BaseUrl = "https://api.openai.com/v1",
                        Model = "gpt-4o",
                        Enabled = true
                    },
                    Credentials: new ProviderCredentialEdit(CredentialEditKind.Replace, new ProviderCredentials("sk-5", null)))
            },
            TranslationOrder: new TranslationOrderChange(
                BaseIds: new[] { "tp-openai", "tp-anthropic" },
                TargetIds: new[] { "tp-openai", "tp-anthropic", "tp-000000000004", "tp-000000000005" }));

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await coordinator.CommitAsync(plan, cts.Token);
        });

        // staged 项全部回滚
        secretStore.Storage.Count(kv => kv.Key.StartsWith("credential-set/cs-", StringComparison.Ordinal)).ShouldBe(0);
        settingsService.UpdateCallCount.ShouldBe(0);
    }

    // =========================================================================
    // 场景 4: Clear 且存在 legacy / 新 Profile Keep
    // 关键断言: 显式 null 两字段均存在，不读取/复活 legacy
    // =========================================================================
    [Fact]
    public async Task Scenario4_ClearWithExistingLegacy_And_NewProfileKeep_DoNotReadOrReviveLegacy()
    {
        // 初始配置包含一个 legacy profile (CredentialSetId == null)
        var initial = new AppSettings
        {
            Translation = new TranslationSettings
            {
                Providers = new List<TranslationProviderProfile>
                {
                    new()
                    {
                        Id = "tp-legacy",
                        DisplayName = "Legacy Profile",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        BaseUrl = "https://api.openai.com/v1",
                        Model = "gpt-4o",
                        CredentialSetId = null,
                        Order = 0,
                        Enabled = true
                    }
                }
            }
        };

        var settingsService = new TestSettingsService(initial);
        var secretStore = new TestSecretStore();
        // 存储中存在旧 legacy 键，以及同名新 Profile 的残留 legacy 键
        secretStore.Set(SecretKeys.ApiKey("tp-legacy"), "old-legacy-api-key");
        secretStore.Set(SecretKeys.SecretKey("tp-legacy"), "old-legacy-secret-key");
        secretStore.Set(SecretKeys.ApiKey("tp-000000000006"), "sneaky-legacy-api-key");

        var registry = new TestTranslationProviderRegistry(settingsService);
        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { registry },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        const string newProfileId = "tp-000000000006";
        var plan = new SettingsEditPlan(
            General: new GeneralSettingsPatch(),
            TranslationChanges: new[]
            {
                // Update tp-legacy with Clear
                new TranslationProfileChange(
                    TranslationProfileChangeKind.Update,
                    BaseProfile: initial.Translation.Providers[0],
                    TargetProfile: initial.Translation.Providers[0],
                    Credentials: new ProviderCredentialEdit(CredentialEditKind.Clear)),
                // Add new profile with Keep
                new TranslationProfileChange(
                    TranslationProfileChangeKind.Add,
                    BaseProfile: null,
                    TargetProfile: new TranslationProviderProfile
                    {
                        Id = newProfileId,
                        DisplayName = "New Keep",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        BaseUrl = "https://api.openai.com/v1",
                        Model = "gpt-4o",
                        Enabled = true
                    },
                    Credentials: new ProviderCredentialEdit(CredentialEditKind.Keep))
            },
            TranslationOrder: new TranslationOrderChange(
                BaseIds: new[] { "tp-legacy" },
                TargetIds: new[] { "tp-legacy", newProfileId }));

        var result = await coordinator.CommitAsync(plan, TestContext.Current.CancellationToken);
        result.Status.ShouldBe(SettingsEditStatus.Applied);

        var updatedLegacy = settingsService.Current.Translation.Providers.Single(p => p.Id == "tp-legacy");
        var updatedNew = settingsService.Current.Translation.Providers.Single(p => p.Id == newProfileId);

        updatedLegacy.CredentialSetId.ShouldNotBeNull();
        updatedNew.CredentialSetId.ShouldNotBeNull();

        // 验证 SecretStore 中的 JSON：两个显式 null 均存在
        var legacyJson = secretStore.Get(SecretKeys.CredentialSet(updatedLegacy.CredentialSetId!));
        legacyJson.ShouldNotBeNull();
        legacyJson.ShouldContain("\"apiKey\":null");
        legacyJson.ShouldContain("\"secretKey\":null");

        var newJson = secretStore.Get(SecretKeys.CredentialSet(updatedNew.CredentialSetId!));
        newJson.ShouldNotBeNull();
        newJson.ShouldContain("\"apiKey\":null");
        newJson.ShouldContain("\"secretKey\":null");

        // 验证 ProviderCredentialResolver 解析：解析为有效 None，不复活 legacy 键
        var resolvedLegacy = ProviderCredentialResolver.Resolve(updatedLegacy, secretStore);
        resolvedLegacy.Credentials.ShouldNotBeNull();
        resolvedLegacy.Credentials.ShouldBe(ProviderCredentials.None);
        resolvedLegacy.Credentials.ApiKey.ShouldBeNull();
        resolvedLegacy.Credentials.SecretKey.ShouldBeNull();

        var resolvedNew = ProviderCredentialResolver.Resolve(updatedNew, secretStore);
        resolvedNew.Credentials.ShouldNotBeNull();
        resolvedNew.Credentials.ShouldBe(ProviderCredentials.None);
        resolvedNew.Credentials.ApiKey.ShouldBeNull();
        resolvedNew.Credentials.SecretKey.ShouldBeNull();
    }

    // =========================================================================
    // 场景 5: 观察者一项失败、另一项仍运行
    // 关键断言: 无提前旧值删除，所有已启动任务退出后返回已提交待应用
    // =========================================================================
    [Fact]
    public async Task Scenario5_ObserverFailsWhileAnotherRuns_NoPrematureDeletion_ReturnsApplyPending()
    {
        var initial = CreateInitialSettings(out var openAiSetId, out var anthropicSetId);
        var settingsService = new TestSettingsService(initial);
        var secretStore = new TestSecretStore();
        secretStore.Set(SecretKeys.CredentialSet(openAiSetId), "{\"format\":\"iris-provider-credentials\",\"version\":1,\"apiKey\":\"sk-openai\",\"secretKey\":null}");
        secretStore.Set(SecretKeys.CredentialSet(anthropicSetId), "{\"format\":\"iris-provider-credentials\",\"version\":1,\"apiKey\":\"sk-anthropic\",\"secretKey\":null}");

        var observer1 = new TestConfigurationObserver
        {
            OnRefreshAsync = _ => throw new InvalidOperationException("Observer 1 failed")
        };

        var observer2Completed = false;
        var observer2 = new TestConfigurationObserver
        {
            OnRefreshAsync = async _ =>
            {
                await Task.Delay(50, CancellationToken.None);
                observer2Completed = true;
            }
        };

        var registry = new TestTranslationProviderRegistry(settingsService);
        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { observer1, observer2 },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        // 删除 tp-anthropic
        var plan = new SettingsEditPlan(
            General: new GeneralSettingsPatch(),
            TranslationChanges: new[]
            {
                new TranslationProfileChange(
                    TranslationProfileChangeKind.Delete,
                    BaseProfile: initial.Translation.Providers[1],
                    TargetProfile: null,
                    Credentials: new ProviderCredentialEdit(CredentialEditKind.Keep))
            },
            TranslationOrder: new TranslationOrderChange(
                BaseIds: new[] { "tp-openai", "tp-anthropic" },
                TargetIds: new[] { "tp-openai" }));

        var result = await coordinator.CommitAsync(plan, TestContext.Current.CancellationToken);

        // 1. 已提交但待应用
        result.Status.ShouldBe(SettingsEditStatus.ApplyPending);
        result.PendingApply.ShouldNotBeNull();
        result.Error.ShouldNotBeNull();
        result.Error.Code.ShouldBe(ErrorCode.ProviderUnavailable);

        // 2. 所有已启动任务均已退出
        observer2Completed.ShouldBeTrue();

        // 3. 无提前旧值删除：anthropicSetId 仍在存储中
        secretStore.Get(SecretKeys.CredentialSet(anthropicSetId)).ShouldNotBeNull();
        secretStore.RemoveCalls.ShouldBeEmpty();
    }

    // =========================================================================
    // 场景 6: 提交后 caller 取消
    // 关键断言: 不返回未提交/OCE，后续状态准确
    // =========================================================================
    [Fact]
    public async Task Scenario6_CallerCancelsAfterCommitPoint_CompletesAndReturnsAccurateState()
    {
        var initial = CreateInitialSettings(out var openAiSetId, out _);
        var settingsService = new TestSettingsService(initial);
        var secretStore = new TestSecretStore();
        secretStore.Set(SecretKeys.CredentialSet(openAiSetId), "{\"format\":\"iris-provider-credentials\",\"version\":1,\"apiKey\":\"sk-openai\",\"secretKey\":null}");

        using var cts = new CancellationTokenSource();

        // 在观察者刷新时触发 CTS 取消（提交点已过）
        var observer = new TestConfigurationObserver
        {
            OnRefreshAsync = _ =>
            {
                cts.Cancel();
                return Task.CompletedTask;
            }
        };

        var registry = new TestTranslationProviderRegistry(settingsService);
        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { observer },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        const string newId = "tp-000000000007";
        var plan = new SettingsEditPlan(
            General: new GeneralSettingsPatch(),
            TranslationChanges: new[]
            {
                new TranslationProfileChange(
                    TranslationProfileChangeKind.Add,
                    BaseProfile: null,
                    TargetProfile: new TranslationProviderProfile
                    {
                        Id = newId,
                        DisplayName = "Late Cancel Test",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        BaseUrl = "https://api.openai.com/v1",
                        Model = "gpt-4o",
                        Enabled = true
                    },
                    Credentials: new ProviderCredentialEdit(CredentialEditKind.Replace, new ProviderCredentials("sk-late", null)))
            },
            TranslationOrder: new TranslationOrderChange(
                BaseIds: new[] { "tp-openai", "tp-anthropic" },
                TargetIds: new[] { "tp-openai", "tp-anthropic", newId }));

        // 即使 caller 取消，也不抛出 OCE，而是返回准确的已提交状态
        var result = await coordinator.CommitAsync(plan, cts.Token);
        result.Status.ShouldBe(SettingsEditStatus.Applied);
        settingsService.UpdateCallCount.ShouldBe(1);
        settingsService.Current.Translation.Providers.Any(p => p.Id == newId).ShouldBeTrue();
    }

    // =========================================================================
    // 场景 7: disabled/OCR 共享集合或 legacy 同 ID
    // 关键断言: 保留共享值，最后引用移除且屏障通过后才回收
    // =========================================================================
    [Fact]
    public async Task Scenario7_SharedAndDisabledAndOcrCredentials_RetainedUntilLastReferenceRemoved()
    {
        var sharedSetId = "cs-" + Guid.NewGuid().ToString("N");
        var initial = new AppSettings
        {
            Translation = new TranslationSettings
            {
                Providers = new List<TranslationProviderProfile>
                {
                    new()
                    {
                        Id = "tp-active",
                        DisplayName = "Active",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        BaseUrl = "https://api.openai.com/v1",
                        Model = "gpt-4o",
                        CredentialSetId = sharedSetId,
                        Order = 0,
                        Enabled = true
                    },
                    new()
                    {
                        Id = "tp-disabled",
                        DisplayName = "Disabled",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        BaseUrl = "https://api.openai.com/v1",
                        Model = "gpt-4o",
                        CredentialSetId = sharedSetId,
                        Order = 1,
                        Enabled = false // 禁用项
                    }
                }
            },
            Ocr = new OcrSettings
            {
                Providers = new List<OcrProviderProfile>
                {
                    new()
                    {
                        Id = "ocr-shared",
                        DisplayName = "OCR Shared",
                        Kind = OcrProviderKind.WindowsLocal,
                        CredentialSetId = sharedSetId, // OCR 共享同一个凭据集
                        Enabled = true
                    }
                }
            }
        };

        var settingsService = new TestSettingsService(initial);
        var secretStore = new TestSecretStore();
        secretStore.Set(SecretKeys.CredentialSet(sharedSetId), "{\"format\":\"iris-provider-credentials\",\"version\":1,\"apiKey\":\"sk-shared\",\"secretKey\":null}");

        var registry = new TestTranslationProviderRegistry(settingsService);
        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { registry },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        // 1. 删除 tp-active：因为 tp-disabled 和 ocr-shared 仍在使用，不回收 sharedSetId
        var plan1 = new SettingsEditPlan(
            General: new GeneralSettingsPatch(),
            TranslationChanges: new[]
            {
                new TranslationProfileChange(
                    TranslationProfileChangeKind.Delete,
                    BaseProfile: initial.Translation.Providers[0],
                    TargetProfile: null,
                    Credentials: new ProviderCredentialEdit(CredentialEditKind.Keep))
            },
            TranslationOrder: new TranslationOrderChange(
                BaseIds: new[] { "tp-active", "tp-disabled" },
                TargetIds: new[] { "tp-disabled" }));

        var res1 = await coordinator.CommitAsync(plan1, TestContext.Current.CancellationToken);
        res1.Status.ShouldBe(SettingsEditStatus.Applied);
        secretStore.Get(SecretKeys.CredentialSet(sharedSetId)).ShouldNotBeNull();

        // 2. 删除 tp-disabled：即使翻译提供商已无引用，ocr-shared 仍在使用，不回收 sharedSetId
        var plan2 = new SettingsEditPlan(
            General: new GeneralSettingsPatch(),
            TranslationChanges: new[]
            {
                new TranslationProfileChange(
                    TranslationProfileChangeKind.Delete,
                    BaseProfile: settingsService.Current.Translation.Providers[0],
                    TargetProfile: null,
                    Credentials: new ProviderCredentialEdit(CredentialEditKind.Keep))
            },
            TranslationOrder: new TranslationOrderChange(
                BaseIds: new[] { "tp-disabled" },
                TargetIds: Array.Empty<string>()));

        var res2 = await coordinator.CommitAsync(plan2, TestContext.Current.CancellationToken);
        res2.Status.ShouldBe(SettingsEditStatus.Applied);
        secretStore.Get(SecretKeys.CredentialSet(sharedSetId)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Scenario7_LegacySharedIdWithOcr_RetainedUntilLastReferenceRemoved()
    {
        const string sharedId = "shared-provider-id";
        var initial = new AppSettings
        {
            Translation = new TranslationSettings
            {
                Providers = new List<TranslationProviderProfile>
                {
                    new()
                    {
                        Id = sharedId,
                        DisplayName = "Translation Legacy",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        BaseUrl = "https://api.openai.com/v1",
                        Model = "gpt-4o",
                        CredentialSetId = null, // Legacy
                        Order = 0,
                        Enabled = true
                    }
                }
            },
            Ocr = new OcrSettings
            {
                Providers = new List<OcrProviderProfile>
                {
                    new()
                    {
                        Id = sharedId, // OCR 拥有相同 ID 的 legacy 使用者
                        DisplayName = "OCR Legacy",
                        Kind = OcrProviderKind.WindowsLocal,
                        CredentialSetId = null, // Legacy
                        Enabled = true
                    }
                }
            }
        };

        var settingsService = new TestSettingsService(initial);
        var secretStore = new TestSecretStore();
        secretStore.Set(SecretKeys.ApiKey(sharedId), "legacy-shared-api-key");
        secretStore.Set(SecretKeys.SecretKey(sharedId), "legacy-shared-secret-key");

        var registry = new TestTranslationProviderRegistry(settingsService);
        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { registry },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        // 删除翻译端的 sharedId
        var plan = new SettingsEditPlan(
            General: new GeneralSettingsPatch(),
            TranslationChanges: new[]
            {
                new TranslationProfileChange(
                    TranslationProfileChangeKind.Delete,
                    BaseProfile: initial.Translation.Providers[0],
                    TargetProfile: null,
                    Credentials: new ProviderCredentialEdit(CredentialEditKind.Keep))
            },
            TranslationOrder: new TranslationOrderChange(
                BaseIds: new[] { sharedId },
                TargetIds: Array.Empty<string>()));

        var result = await coordinator.CommitAsync(plan, TestContext.Current.CancellationToken);
        result.Status.ShouldBe(SettingsEditStatus.Applied);

        // 因为 OCR 仍在引用该 legacy ID，legacy key 不被删除
        secretStore.Get(SecretKeys.ApiKey(sharedId)).ShouldBe("legacy-shared-api-key");
        secretStore.Get(SecretKeys.SecretKey(sharedId)).ShouldBe("legacy-shared-secret-key");
    }

    // =========================================================================
    // 场景 8: 清理失败后重试，期间又有新提交
    // 关键断言: 不重复 staging/发布，不恢复旧草稿、不删当前引用，成功后幂等
    // =========================================================================
    [Fact]
    public async Task Scenario8_CleanupFailure_RetryAfterNewCommit_Idempotent_PreservesCurrentReferences()
    {
        var initial = CreateInitialSettings(out var openAiSetId, out var anthropicSetId);
        var settingsService = new TestSettingsService(initial);
        var secretStore = new TestSecretStore();
        secretStore.Set(SecretKeys.CredentialSet(openAiSetId), "{\"format\":\"iris-provider-credentials\",\"version\":1,\"apiKey\":\"sk-openai\",\"secretKey\":null}");
        secretStore.Set(SecretKeys.CredentialSet(anthropicSetId), "{\"format\":\"iris-provider-credentials\",\"version\":1,\"apiKey\":\"sk-anthropic\",\"secretKey\":null}");

        // 第一次 Remove 失败
        var removeFail = true;
        secretStore.FailOnRemove = _ => removeFail;

        var registry = new TestTranslationProviderRegistry(settingsService);
        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { registry },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        // 删除 tp-anthropic
        var plan = new SettingsEditPlan(
            General: new GeneralSettingsPatch(),
            TranslationChanges: new[]
            {
                new TranslationProfileChange(
                    TranslationProfileChangeKind.Delete,
                    BaseProfile: initial.Translation.Providers[1],
                    TargetProfile: null,
                    Credentials: new ProviderCredentialEdit(CredentialEditKind.Keep))
            },
            TranslationOrder: new TranslationOrderChange(
                BaseIds: new[] { "tp-openai", "tp-anthropic" },
                TargetIds: new[] { "tp-openai" }));

        var result = await coordinator.CommitAsync(plan, TestContext.Current.CancellationToken);
        result.Status.ShouldBe(SettingsEditStatus.ApplyPending);
        result.PendingApply.ShouldNotBeNull();
        var receipt = result.PendingApply;

        // 期间发生新提交：修改 General 级别
        var newPlan = new SettingsEditPlan(
            General: new GeneralSettingsPatch(LogLevel: new SettingsFieldEdit<string>("Information", "Warning")),
            TranslationChanges: Array.Empty<TranslationProfileChange>(),
            TranslationOrder: null);
        var newResult = await coordinator.CommitAsync(newPlan, TestContext.Current.CancellationToken);
        newResult.Status.ShouldBe(SettingsEditStatus.Applied);

        // 重试清理：解除 Remove 失败拦截
        removeFail = false;
        var setCallCountBeforeRetry = secretStore.SetCalls.Count;

        var retryResult = await coordinator.RetryApplyAsync(receipt, TestContext.Current.CancellationToken);
        retryResult.IsComplete.ShouldBeTrue();

        // 1. 不重复 staging（没有新的 Set 调用）
        secretStore.SetCalls.Count.ShouldBe(setCallCountBeforeRetry);

        // 2. 不恢复旧草稿（General LogLevel 依然是 Warning，tp-anthropic 依然已删除）
        settingsService.Current.General.LogLevel.ShouldBe("Warning");
        settingsService.Current.Translation.Providers.Any(p => p.Id == "tp-anthropic").ShouldBeFalse();

        // 3. 不删当前引用（openAiSetId 依然存在）
        secretStore.Get(SecretKeys.CredentialSet(openAiSetId)).ShouldNotBeNull();

        // 4. 旧凭据已被清理
        secretStore.Get(SecretKeys.CredentialSet(anthropicSetId)).ShouldBeNull();

        // 5. 再次调用幂等
        var idempotentRetry = await coordinator.RetryApplyAsync(receipt, TestContext.Current.CancellationToken);
        idempotentRetry.IsComplete.ShouldBeTrue();
    }

    // =========================================================================
    // 场景 9: Keep 草稿捕获与保存竞争、网络挂起
    // 关键断言: 捕获不使用被回收值，网络阶段不占编辑串行权，租约不提前释放
    // =========================================================================
    [Fact]
    public async Task Scenario9_KeepDraftResolve_DoesNotBlockCommitOrPrematurelyDisposeLease()
    {
        var initial = CreateInitialSettings(out var openAiSetId, out _);
        var settingsService = new TestSettingsService(initial);
        var secretStore = new TestSecretStore();
        secretStore.Set(SecretKeys.CredentialSet(openAiSetId), "{\"format\":\"iris-provider-credentials\",\"version\":1,\"apiKey\":\"sk-openai\",\"secretKey\":null}");

        var testAsyncEntered = new TaskCompletionSource();
        var testAsyncRelease = new TaskCompletionSource();

        var provider = new TestTranslationProvider(initial.Translation.Providers[0], new ProviderCredentials("sk-openai", null))
        {
            OnTestAsync = async _ =>
            {
                testAsyncEntered.TrySetResult();
                await testAsyncRelease.Task;
                return new ProviderTestResult(true, TimeSpan.FromMilliseconds(50), "Test passed");
            }
        };

        var registry = new TestTranslationProviderRegistry(settingsService)
        {
            ProviderFactory = (p, c) => provider
        };

        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { registry },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        var draft = new TranslationProviderDraft(
            Profile: initial.Translation.Providers[0],
            BaseProfile: initial.Translation.Providers[0],
            Credentials: new ProviderCredentialEdit(CredentialEditKind.Keep));

        // 1. 启动 TestDraftAsync
        var testDraftTask = coordinator.TestDraftAsync(draft, TestContext.Current.CancellationToken);

        // 等待进入 provider.TestAsync 网络阶段
        await testAsyncEntered.Task;

        // 2. 在网络挂起期间，验证协调器串行门没有被长期占住：并发 CommitAsync 可以顺利执行！
        var generalPlan = new SettingsEditPlan(
            General: new GeneralSettingsPatch(LogLevel: new SettingsFieldEdit<string>("Information", "Warning")),
            TranslationChanges: Array.Empty<TranslationProfileChange>(),
            TranslationOrder: null);

        var commitResult = await coordinator.CommitAsync(generalPlan, TestContext.Current.CancellationToken);
        commitResult.Status.ShouldBe(SettingsEditStatus.Applied);

        // 3. 租约快照尚未提前释放
        var transientSnapshot = registry.CreatedSnapshots.Last();
        transientSnapshot.IsDisposed.ShouldBeFalse();

        // 4. 释放网络调用
        testAsyncRelease.TrySetResult();
        var testResult = await testDraftTask;
        testResult.Success.ShouldBeTrue();

        // 5. 工作真正退出后，租约被释放
        transientSnapshot.IsDisposed.ShouldBeTrue();
    }

    // =========================================================================
    // 场景 10: 空模型的两种 Provider 获取列表
    // 关键断言: fake HTTP 实际收到 GET，模型返回；Descriptor 仍不可翻译，Test 无翻译请求
    // (在 Coordinator 层通过 ListDraftModelsAsync 与 TestDraftAsync 验证集成)
    // =========================================================================
    [Fact]
    public async Task Scenario10_Coordinator_ListDraftModels_EmptyModel_ReturnsModels_TestDraftFailsWithoutRequest()
    {
        var initial = CreateInitialSettings(out _, out _);
        var settingsService = new TestSettingsService(initial);
        var secretStore = new TestSecretStore();

        var emptyModelProfile = new TranslationProviderProfile
        {
            Id = "tp-000000000008",
            DisplayName = "Empty Model Provider",
            Kind = TranslationProviderKind.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            Model = "", // 空模型
            Enabled = true
        };

        var provider = new TestTranslationProvider(emptyModelProfile, new ProviderCredentials("sk-test", null));
        var registry = new TestTranslationProviderRegistry(settingsService)
        {
            ProviderFactory = (p, c) => provider
        };

        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { registry },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        var draft = new TranslationProviderDraft(
            Profile: emptyModelProfile,
            BaseProfile: null,
            Credentials: new ProviderCredentialEdit(CredentialEditKind.Replace, new ProviderCredentials("sk-test", null)));

        // 1. ListDraftModelsAsync 能够正常获取模型列表
        var models = await coordinator.ListDraftModelsAsync(draft, TestContext.Current.CancellationToken);
        models.Count.ShouldBe(2);
        provider.ListModelsCallCount.ShouldBe(1);

        // 2. TestDraftAsync 针对空模型拒绝（返回安全 NotConfigured 错误），无翻译请求
        var testResult = await coordinator.TestDraftAsync(draft, TestContext.Current.CancellationToken);
        testResult.Success.ShouldBeFalse();
        testResult.Error.ShouldNotBeNull();
        testResult.Error.Code.ShouldBe(ErrorCode.NotConfigured);
    }

    // =========================================================================
    // 场景 11: 缺 Key、非法端点、显式坏引用及无模型能力
    // 关键断言: 安全错误，无不合格请求，不能把坏引用当本地免 Key
    // =========================================================================
    [Fact]
    public async Task Scenario11_MissingKey_BadEndpoint_ExplicitBadReference_SafeErrors()
    {
        var initial = CreateInitialSettings(out _, out _);
        var settingsService = new TestSettingsService(initial);
        var secretStore = new TestSecretStore();
        var registry = new TestTranslationProviderRegistry(settingsService);
        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { registry },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        // 1. 缺 Key：远程提供商传 None 或 Clear 凭据取模型
        var remoteDraftNoKey = new TranslationProviderDraft(
            Profile: new TranslationProviderProfile
            {
                Id = "tp-000000000009",
                DisplayName = "Remote No Key",
                Kind = TranslationProviderKind.OpenAICompatible,
                BaseUrl = "https://api.openai.com/v1",
                Model = "gpt-4o",
                Enabled = true
            },
            BaseProfile: null,
            Credentials: new ProviderCredentialEdit(CredentialEditKind.Clear));

        var ex1 = await Should.ThrowAsync<IrisException>(async () =>
        {
            await coordinator.ListDraftModelsAsync(remoteDraftNoKey, TestContext.Current.CancellationToken);
        });
        ex1.Code.ShouldBe(ErrorCode.NotConfigured);

        // 2. 非法端点：CommitAsync 校验拦截
        var badEndpointPlan = new SettingsEditPlan(
            General: new GeneralSettingsPatch(),
            TranslationChanges: new[]
            {
                new TranslationProfileChange(
                    TranslationProfileChangeKind.Add,
                    BaseProfile: null,
                    TargetProfile: new TranslationProviderProfile
                    {
                        Id = "tp-000000000010",
                        DisplayName = "Bad Endpoint",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        BaseUrl = "not-a-valid-url",
                        Model = "gpt-4o",
                        Enabled = true
                    },
                    Credentials: new ProviderCredentialEdit(CredentialEditKind.Replace, new ProviderCredentials("sk-test", null)))
            },
            TranslationOrder: new TranslationOrderChange(
                BaseIds: new[] { "tp-openai", "tp-anthropic" },
                TargetIds: new[] { "tp-openai", "tp-anthropic", "tp-000000000010" }));

        var res2 = await coordinator.CommitAsync(badEndpointPlan, TestContext.Current.CancellationToken);
        res2.Status.ShouldBe(SettingsEditStatus.Uncommitted);
        res2.Error.ShouldNotBeNull();
        res2.Error.Code.ShouldBe(ErrorCode.InvalidArgument);

        // 3. 显式坏引用：即使 BaseUrl 为本地免 Key 地址 (http://localhost:11434)，也不能当作 None 免 Key 通过！
        var badRefLocalDraft = new TranslationProviderDraft(
            Profile: new TranslationProviderProfile
            {
                Id = "tp-000000000011",
                DisplayName = "Local Bad Ref",
                Kind = TranslationProviderKind.OpenAICompatible,
                BaseUrl = "http://localhost:11434/v1",
                Model = "llama3",
                CredentialSetId = "cs-malformed-bad-reference", // 显式坏引用
                Enabled = true
            },
            BaseProfile: null,
            Credentials: new ProviderCredentialEdit(CredentialEditKind.Keep));

        var ex3 = await Should.ThrowAsync<IrisException>(async () =>
        {
            await coordinator.ListDraftModelsAsync(badRefLocalDraft, TestContext.Current.CancellationToken);
        });
        ex3.Code.ShouldBe(ErrorCode.NotConfigured);

        var testRes3 = await coordinator.TestDraftAsync(badRefLocalDraft, TestContext.Current.CancellationToken);
        testRes3.Success.ShouldBeFalse();
        testRes3.Error.ShouldNotBeNull();
        testRes3.Error.Code.ShouldBe(ErrorCode.NotConfigured);

        // 4. 无模型列表能力：Provider 未实现 IModelCatalogSource
        var noCatalogRegistry = new TestTranslationProviderRegistry(settingsService)
        {
            ProviderFactory = (p, c) => new TestTranslationProviderWithoutCatalog(p, c)
        };
        var coordNoCatalog = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            noCatalogRegistry,
            new IProviderConfigurationObserver[] { noCatalogRegistry },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        var noCatalogDraft = new TranslationProviderDraft(
            Profile: new TranslationProviderProfile
            {
                Id = "tp-000000000012",
                DisplayName = "No Catalog",
                Kind = TranslationProviderKind.OpenAICompatible,
                BaseUrl = "http://localhost:11434/v1",
                Model = "llama3",
                Enabled = true
            },
            BaseProfile: null,
            Credentials: new ProviderCredentialEdit(CredentialEditKind.Replace, new ProviderCredentials(null, null)));

        var ex4 = await Should.ThrowAsync<IrisException>(async () =>
        {
            await coordNoCatalog.ListDraftModelsAsync(noCatalogDraft, TestContext.Current.CancellationToken);
        });
        ex4.Code.ShouldBe(ErrorCode.NotConfigured);
    }

    // =========================================================================
    // 场景 12: 假敏感标记经输入/失败路径
    // 关键断言: 不进入 settings.json、日志异常参数、回执或 DTO 的 ToString
    // =========================================================================
    [Fact]
    public async Task Scenario12_SensitiveTokens_NeverLeakToSettings_Logs_Receipt_Or_ToString()
    {
        const string dummySensitiveToken = "sk-SENSITIVE-DUMMY-SECRET-987654321";

        var credEdit = new ProviderCredentialEdit(CredentialEditKind.Replace, new ProviderCredentials(dummySensitiveToken, "super-secret-key"));
        var credEditStr = credEdit.ToString();
        credEditStr.ShouldNotContain(dummySensitiveToken);
        credEditStr.ShouldNotContain("super-secret-key");

        var initial = CreateInitialSettings(out _, out _);
        var settingsService = new TestSettingsService(initial);
        var secretStore = new TestSecretStore();
        // 模拟 Set 失败以触发日志和错误返回
        secretStore.FailOnSet = (_, _) => throw new InvalidOperationException("Simulated failure with dummy: " + dummySensitiveToken);

        var registry = new TestTranslationProviderRegistry(settingsService);
        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { registry },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        var plan = new SettingsEditPlan(
            General: new GeneralSettingsPatch(),
            TranslationChanges: new[]
            {
                new TranslationProfileChange(
                    TranslationProfileChangeKind.Add,
                    BaseProfile: null,
                    TargetProfile: new TranslationProviderProfile
                    {
                        Id = "tp-000000000013",
                        DisplayName = "Sensitive Test",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        BaseUrl = "https://api.openai.com/v1",
                        Model = "gpt-4o",
                        Enabled = true
                    },
                    Credentials: credEdit)
            },
            TranslationOrder: new TranslationOrderChange(
                BaseIds: new[] { "tp-openai", "tp-anthropic" },
                TargetIds: new[] { "tp-openai", "tp-anthropic", "tp-000000000013" }));

        var result = await coordinator.CommitAsync(plan, TestContext.Current.CancellationToken);

        // 1. Error Detail 不包含敏感凭据
        result.Error.ShouldNotBeNull();
        result.Error.Detail.ShouldNotBeNull();
        result.Error.Detail.ShouldNotContain(dummySensitiveToken);

        // 2. Settings JSON 中不包含敏感凭据
        var serializedSettings = JsonSerializer.Serialize(settingsService.Current);
        serializedSettings.ShouldNotContain(dummySensitiveToken);

        // 3. Logger 中不包含敏感凭据
        foreach (var msg in logger.Messages)
        {
            msg.ShouldNotContain(dummySensitiveToken);
        }
    }

    // =========================================================================
    // 场景 13: 真实 DI 路径测试
    // 关键断言: 确认构造无 I/O、重复 AddIrisCore 不重复注册、首次 Commit 才生效
    // =========================================================================
    [Fact]
    public async Task Scenario13_DI_ConstructionNoIO_SingleRegistration_FirstCommitWorks()
    {
        var services = new ServiceCollection();

        var initial = CreateInitialSettings(out var openAiSetId, out _);
        var settingsService = new TestSettingsService(initial);
        var secretStore = new TestSecretStore();
        secretStore.Set(SecretKeys.CredentialSet(openAiSetId), "{\"format\":\"iris-provider-credentials\",\"version\":1,\"apiKey\":\"sk-openai\",\"secretKey\":null}");

        services.AddSingleton<ISettingsService>(settingsService);
        services.AddSingleton<ISecretStore>(secretStore);
        services.AddSingleton<ITranslationHistoryRepository>(Substitute.For<ITranslationHistoryRepository>());
        services.AddSingleton<ISettingsWindowPresenter>(Substitute.For<ISettingsWindowPresenter>());
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        // 重复调用 AddIrisCore
        services.AddIrisCore();
        services.AddIrisCore();

        // The OCR observer owns background refresh, including post-commit secret invalidation.
        await using var provider = services.BuildServiceProvider();

        // 1. 重复 AddIrisCore 不重复注册
        provider.GetServices<ISettingsEditCoordinator>().Count().ShouldBe(1);

        // 2. 构造无 I/O
        var initialSetCount = secretStore.SetCalls.Count;
        var initialRemoveCount = secretStore.RemoveCalls.Count;
        var initialUpdateCount = settingsService.UpdateCallCount;

        var coordinator = provider.GetRequiredService<ISettingsEditCoordinator>();
        coordinator.ShouldNotBeNull();

        secretStore.SetCalls.Count.ShouldBe(initialSetCount);
        secretStore.RemoveCalls.Count.ShouldBe(initialRemoveCount);
        settingsService.UpdateCallCount.ShouldBe(initialUpdateCount);

        // 3. 首次 Commit 才生效
        var plan = new SettingsEditPlan(
            General: new GeneralSettingsPatch(LogLevel: new SettingsFieldEdit<string>("Information", "Warning")),
            TranslationChanges: Array.Empty<TranslationProfileChange>(),
            TranslationOrder: null);

        var result = await coordinator.CommitAsync(plan, TestContext.Current.CancellationToken);
        result.Status.ShouldBe(SettingsEditStatus.Applied);
        settingsService.Current.General.LogLevel.ShouldBe("Warning");
    }

    // =========================================================================
    // 场景 14: 写前门闩取消 (R1-1)
    // 关键断言: 传入有效 token，UpdateAsync 开始前取消抛出 OCE，不写入配置，staged 凭据被回收
    // =========================================================================
    [Fact]
    public async Task Scenario14_CommitAsync_CancelBeforeMutate_ThrowsOCE_DoesNotPublishSettings_RollsBackStaged()
    {
        var initial = CreateInitialSettings(out _, out _);
        var settingsService = new TestSettingsService(initial);
        var secretStore = new TestSecretStore();
        var registry = new TestTranslationProviderRegistry(settingsService);
        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { registry },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        using var cts = new CancellationTokenSource();
        settingsService.BeforeUpdateHookAsync = async _ =>
        {
            await Task.Yield();
            cts.Cancel();
        };

        var plan = new SettingsEditPlan(
            General: new GeneralSettingsPatch(),
            TranslationChanges: new[]
            {
                new TranslationProfileChange(
                    TranslationProfileChangeKind.Add,
                    BaseProfile: null,
                    TargetProfile: new TranslationProviderProfile
                    {
                        Id = "tp-000000000014",
                        DisplayName = "Cancel Probe",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        BaseUrl = "https://api.openai.com/v1",
                        Model = "gpt-4o",
                        Enabled = true
                    },
                    Credentials: new ProviderCredentialEdit(CredentialEditKind.Replace, new ProviderCredentials("fake-key-14", null)))
            },
            TranslationOrder: new TranslationOrderChange(
                BaseIds: new[] { "tp-openai", "tp-anthropic" },
                TargetIds: new[] { "tp-openai", "tp-anthropic", "tp-000000000014" }));

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await coordinator.CommitAsync(plan, cts.Token);
        });

        settingsService.UpdateTokenCanCancel.ShouldBeTrue();
        settingsService.UpdateCallCount.ShouldBe(0);
        settingsService.Current.Translation.Providers.Count.ShouldBe(2);

        // 验证 staged 临时凭据被回收
        secretStore.RemoveCalls.Count.ShouldBe(1);
        secretStore.Storage.Count(kv => kv.Key.StartsWith("credential-set/cs-", StringComparison.Ordinal)).ShouldBe(0);
    }

    // =========================================================================
    // 场景 15: UpdateAsync 成功返回后取消 (R1-1)
    // 关键断言: 越过提交点后承认已提交，正常报告 Applied，不抛 OCE
    // =========================================================================
    [Fact]
    public async Task Scenario15_CommitAsync_CanceledAfterUpdateAsyncSucceeds_AcknowledgesCommit()
    {
        var initial = CreateInitialSettings(out _, out _);
        var settingsService = new TestSettingsService(initial);
        var secretStore = new TestSecretStore();
        var registry = new TestTranslationProviderRegistry(settingsService);
        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { registry },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        using var cts = new CancellationTokenSource();
        settingsService.AfterUpdateHookAsync = _ =>
        {
            cts.Cancel();
            return Task.CompletedTask;
        };

        var plan = new SettingsEditPlan(
            General: new GeneralSettingsPatch(LogLevel: new SettingsFieldEdit<string>("Information", "Warning")),
            TranslationChanges: Array.Empty<TranslationProfileChange>(),
            TranslationOrder: null);

        var result = await coordinator.CommitAsync(plan, cts.Token);
        result.Status.ShouldBe(SettingsEditStatus.Applied);
        settingsService.Current.General.LogLevel.ShouldBe("Warning");
    }

    // =========================================================================
    // 场景 16: 零候选观察者失败与重试 (R1-2)
    // 关键断言: 无旧凭据候选时观察者失败返回 ApplyPending，重试再次调用观察者，成功后才 Complete
    // =========================================================================
    [Fact]
    public async Task Scenario16_EmptyCandidates_ObserverFails_RetrySucceedsOnlyWhenObserverSucceeds()
    {
        var initial = CreateInitialSettings(out _, out _);
        var settingsService = new TestSettingsService(initial);
        var secretStore = new TestSecretStore();
        var registry = new TestTranslationProviderRegistry(settingsService);
        var observer = new TestConfigurationObserver { Fail = true };
        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { registry, observer },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        var plan = new SettingsEditPlan(
            General: new GeneralSettingsPatch(),
            TranslationChanges: new[]
            {
                new TranslationProfileChange(
                    TranslationProfileChangeKind.Add,
                    BaseProfile: null,
                    TargetProfile: new TranslationProviderProfile
                    {
                        Id = "tp-000000000016",
                        DisplayName = "Add Provider",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        BaseUrl = "https://api.openai.com/v1",
                        Model = "gpt-4o",
                        Enabled = true
                    },
                    Credentials: new ProviderCredentialEdit(CredentialEditKind.Replace, new ProviderCredentials("fake-key-16", null)))
            },
            TranslationOrder: new TranslationOrderChange(
                BaseIds: new[] { "tp-openai", "tp-anthropic" },
                TargetIds: new[] { "tp-openai", "tp-anthropic", "tp-000000000016" }));

        var committed = await coordinator.CommitAsync(plan, TestContext.Current.CancellationToken);
        committed.Status.ShouldBe(SettingsEditStatus.ApplyPending);
        committed.PendingApply.ShouldNotBeNull();
        observer.RefreshCount.ShouldBe(1);

        // 第一次重试，观察者依然失败
        var retry1 = await coordinator.RetryApplyAsync(committed.PendingApply, TestContext.Current.CancellationToken);
        retry1.IsComplete.ShouldBeFalse();
        observer.RefreshCount.ShouldBe(2);

        // 第二次重试，解除观察者失败
        observer.Fail = false;
        var retry2 = await coordinator.RetryApplyAsync(committed.PendingApply, TestContext.Current.CancellationToken);
        retry2.IsComplete.ShouldBeTrue();
        observer.RefreshCount.ShouldBe(3);
    }

    // =========================================================================
    // 场景 17: 纯排序走观察者屏障 (R1-2)
    // 关键断言: 纯排序不能跳过屏障，观察者失败返回 ApplyPending，重试成功后幂等
    // =========================================================================
    [Fact]
    public async Task Scenario17_OrderOnly_ObserverFails_ReturnsApplyPending_RetryIdempotent()
    {
        var initial = CreateInitialSettings(out _, out _);
        var settingsService = new TestSettingsService(initial);
        var secretStore = new TestSecretStore();
        var registry = new TestTranslationProviderRegistry(settingsService);
        var observer = new TestConfigurationObserver { Fail = true };
        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { registry, observer },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        var plan = new SettingsEditPlan(
            General: new GeneralSettingsPatch(),
            TranslationChanges: Array.Empty<TranslationProfileChange>(),
            TranslationOrder: new TranslationOrderChange(
                BaseIds: new[] { "tp-openai", "tp-anthropic" },
                TargetIds: new[] { "tp-anthropic", "tp-openai" }));

        var result = await coordinator.CommitAsync(plan, TestContext.Current.CancellationToken);
        result.Status.ShouldBe(SettingsEditStatus.ApplyPending);
        result.PendingApply.ShouldNotBeNull();
        observer.RefreshCount.ShouldBe(1);

        // 配置已按顺序更新
        settingsService.Current.Translation.Providers[0].Id.ShouldBe("tp-anthropic");

        // 恢复观察者后重试
        observer.Fail = false;
        var retried = await coordinator.RetryApplyAsync(result.PendingApply, TestContext.Current.CancellationToken);
        retried.IsComplete.ShouldBeTrue();
        observer.RefreshCount.ShouldBe(2);

        // 再次重试保持幂等
        var idempotent = await coordinator.RetryApplyAsync(result.PendingApply, TestContext.Current.CancellationToken);
        idempotent.IsComplete.ShouldBeTrue();
        observer.RefreshCount.ShouldBe(2);
    }

    // =========================================================================
    // 场景 18: 非法枚举（CredentialEditKind / TranslationProfileChangeKind）(R1-3)
    // 关键断言: 999 等未定义枚举被拒绝为 InvalidArgument，绝不走 Clear，无任何持久副作用
    // =========================================================================
    [Fact]
    public async Task Scenario18_InvalidEnums_ReturnsInvalidArgument_NoSideEffects()
    {
        var initial = CreateInitialSettings(out var openAiSetId, out _);
        var settingsService = new TestSettingsService(initial);
        var secretStore = new TestSecretStore();
        secretStore.Set(SecretKeys.CredentialSet(openAiSetId), "{\"format\":\"iris-provider-credentials\",\"version\":1,\"apiKey\":\"sk-openai\",\"secretKey\":null}");

        var registry = new TestTranslationProviderRegistry(settingsService);
        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { registry },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        // 1. 非法 CredentialEditKind: 999
        var targetProfile = initial.Translation.Providers[0] with { DisplayName = "Changed Name" };
        var plan1 = new SettingsEditPlan(
            General: new GeneralSettingsPatch(),
            TranslationChanges: new[]
            {
                new TranslationProfileChange(
                    TranslationProfileChangeKind.Update,
                    BaseProfile: initial.Translation.Providers[0],
                    TargetProfile: targetProfile,
                    Credentials: new ProviderCredentialEdit((CredentialEditKind)999))
            },
            TranslationOrder: null);

        var res1 = await coordinator.CommitAsync(plan1, TestContext.Current.CancellationToken);
        res1.Status.ShouldBe(SettingsEditStatus.Uncommitted);
        res1.Error.ShouldNotBeNull();
        res1.Error.Code.ShouldBe(ErrorCode.InvalidArgument);

        // 验证没有副作用：旧密钥完好，未调用 Set/Remove
        secretStore.Get(SecretKeys.CredentialSet(openAiSetId)).ShouldNotBeNull();
        secretStore.RemoveCalls.Count.ShouldBe(0);

        // 2. 非法 TranslationProfileChangeKind: 999
        var plan2 = new SettingsEditPlan(
            General: new GeneralSettingsPatch(),
            TranslationChanges: new[]
            {
                new TranslationProfileChange(
                    (TranslationProfileChangeKind)999,
                    BaseProfile: initial.Translation.Providers[0],
                    TargetProfile: targetProfile,
                    Credentials: new ProviderCredentialEdit(CredentialEditKind.Keep))
            },
            TranslationOrder: null);

        var res2 = await coordinator.CommitAsync(plan2, TestContext.Current.CancellationToken);
        res2.Status.ShouldBe(SettingsEditStatus.Uncommitted);
        res2.Error.ShouldNotBeNull();
        res2.Error.Code.ShouldBe(ErrorCode.InvalidArgument);
    }

    // =========================================================================
    // 场景 19: Null 集合结构拒绝 (R1-3)
    // 关键断言: 拒绝 null changes、null Credentials 等结构，不静默丢弃
    // =========================================================================
    [Fact]
    public async Task Scenario19_NullStructure_ReturnsInvalidArgument()
    {
        var initial = CreateInitialSettings(out _, out _);
        var settingsService = new TestSettingsService(initial);
        var secretStore = new TestSecretStore();
        var registry = new TestTranslationProviderRegistry(settingsService);
        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { registry },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        // 1. null element in TranslationChanges
        var plan1 = new SettingsEditPlan(
            General: new GeneralSettingsPatch(),
            TranslationChanges: new TranslationProfileChange[] { null! },
            TranslationOrder: null);

        var res1 = await coordinator.CommitAsync(plan1, TestContext.Current.CancellationToken);
        res1.Status.ShouldBe(SettingsEditStatus.Uncommitted);
        res1.Error.ShouldNotBeNull();
        res1.Error.Code.ShouldBe(ErrorCode.InvalidArgument);

        // 2. null Credentials in TranslationProfileChange
        var plan2 = new SettingsEditPlan(
            General: new GeneralSettingsPatch(),
            TranslationChanges: new[]
            {
                new TranslationProfileChange(
                    TranslationProfileChangeKind.Add,
                    BaseProfile: null,
                    TargetProfile: new TranslationProviderProfile
                    {
                        Id = "tp-000000000019",
                        DisplayName = "Null Credentials",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        BaseUrl = "https://api.openai.com/v1",
                        Model = "gpt-4o",
                        Enabled = true
                    },
                    Credentials: null!)
            },
            TranslationOrder: null);

        var res2 = await coordinator.CommitAsync(plan2, TestContext.Current.CancellationToken);
        res2.Status.ShouldBe(SettingsEditStatus.Uncommitted);
        res2.Error.ShouldNotBeNull();
        res2.Error.Code.ShouldBe(ErrorCode.InvalidArgument);
    }

    // =========================================================================
    // 场景 20: Haiku 默认 Options 校验 (R1-3)
    // 关键断言: Haiku 补齐默认 Effort="low" 时被拒绝为 InvalidArgument，显式 Effort=null 通过
    // =========================================================================
    [Fact]
    public async Task Scenario20_Haiku_DefaultOptionsChecked_RejectsEffortNotNull_AcceptsExplicitNull()
    {
        var initial = CreateInitialSettings(out _, out _);
        var settingsService = new TestSettingsService(initial);
        var secretStore = new TestSecretStore();
        var registry = new TestTranslationProviderRegistry(settingsService);
        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { registry },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        // 1. AnthropicOptions == null: 补齐默认 Effort="low"，但 Haiku 不支持 Effort：TestDraft 被拒绝，但 ListDraftModels 允许模型发现 (R2-3)
        var haikuNullOptions = new TranslationProviderDraft(
            Profile: new TranslationProviderProfile
            {
                Id = "tp-000000000020",
                DisplayName = "Haiku Provider",
                Kind = TranslationProviderKind.Anthropic,
                Model = "claude-3-5-haiku-20241022",
                AnthropicOptions = null,
                Enabled = true
            },
            BaseProfile: null,
            Credentials: new ProviderCredentialEdit(CredentialEditKind.Replace, new ProviderCredentials("sk-ant-test", null)));

        var testResult = await coordinator.TestDraftAsync(haikuNullOptions, TestContext.Current.CancellationToken);
        testResult.Success.ShouldBeFalse();
        testResult.Error.ShouldNotBeNull();
        testResult.Error.Code.ShouldBe(ErrorCode.InvalidArgument);

        var listModels = await coordinator.ListDraftModelsAsync(haikuNullOptions, TestContext.Current.CancellationToken);
        listModels.Count.ShouldBeGreaterThan(0);

        // 2. 显式指定 Effort = null: 翻译测试与模型列表均通过
        var haikuExplicitNullEffort = new TranslationProviderDraft(
            Profile: new TranslationProviderProfile
            {
                Id = "tp-000000000020",
                DisplayName = "Haiku Provider",
                Kind = TranslationProviderKind.Anthropic,
                Model = "claude-3-5-haiku-20241022",
                AnthropicOptions = new AnthropicOptions { Effort = null },
                Enabled = true
            },
            BaseProfile: null,
            Credentials: new ProviderCredentialEdit(CredentialEditKind.Replace, new ProviderCredentials("sk-ant-test", null)));

        var testExplicitResult = await coordinator.TestDraftAsync(haikuExplicitNullEffort, TestContext.Current.CancellationToken);
        testExplicitResult.Success.ShouldBeTrue();

        var models = await coordinator.ListDraftModelsAsync(haikuExplicitNullEffort, TestContext.Current.CancellationToken);
        models.Count.ShouldBeGreaterThan(0);
    }

    // =========================================================================
    // 场景 21: 陈旧 Keep 草稿准入拦截 (R1-4)
    // 关键断言: BaseProfile 与当前配置不一致返回 Conflict，不创建瞬态实例，不使用旧密钥
    // =========================================================================
    [Fact]
    public async Task Scenario21_StaleKeepDraft_ReturnsConflict_DoesNotCreateTransient_DoesNotUseStaleKey()
    {
        var initial = CreateInitialSettings(out _, out _);
        var oldSet = "cs-" + new string('a', 32);
        var newSet = "cs-" + new string('b', 32);

        var oldProfile = initial.Translation.Providers[0] with { CredentialSetId = oldSet };
        var newerProfile = oldProfile with { CredentialSetId = newSet, BaseUrl = "https://new.example.com/v1" };

        var settings = initial with
        {
            Translation = initial.Translation with
            {
                Providers = new List<TranslationProviderProfile> { newerProfile }
            }
        };

        var settingsService = new TestSettingsService(settings);
        var secretStore = new TestSecretStore();
        secretStore.Set(SecretKeys.CredentialSet(oldSet), "{\"format\":\"iris-provider-credentials\",\"version\":1,\"apiKey\":\"fake-old-key\",\"secretKey\":null}");
        secretStore.Set(SecretKeys.CredentialSet(newSet), "{\"format\":\"iris-provider-credentials\",\"version\":1,\"apiKey\":\"fake-new-key\",\"secretKey\":null}");

        var registry = new TestTranslationProviderRegistry(settingsService);
        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { registry },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        // 使用陈旧的 oldProfile 作为 BaseProfile 进行 Keep 测试
        var staleDraft = new TranslationProviderDraft(
            Profile: oldProfile,
            BaseProfile: oldProfile,
            Credentials: new ProviderCredentialEdit(CredentialEditKind.Keep));

        var result = await coordinator.TestDraftAsync(staleDraft, TestContext.Current.CancellationToken);
        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.Detail.ShouldBe("BaseProfileConflict");

        // 未调用瞬态工厂创建实例
        registry.CreatedSnapshots.Count.ShouldBe(0);
    }

    // =========================================================================
    // 场景 22: Keep 草稿后台线程读取凭据 (R1-4)
    // 关键断言: 凭据解析不在调用线程同步执行，不阻塞 UI/调用线程
    // =========================================================================
    [Fact]
    public async Task Scenario22_KeepDraft_SecretReadOnBackgroundThread_DoesNotBlockCaller()
    {
        var initial = CreateInitialSettings(out var openAiSetId, out _);
        var settingsService = new TestSettingsService(initial);
        var secretStore = new TestSecretStore();
        secretStore.Set(SecretKeys.CredentialSet(openAiSetId), "{\"format\":\"iris-provider-credentials\",\"version\":1,\"apiKey\":\"sk-openai\",\"secretKey\":null}");
        secretStore.CallerThreadId = Environment.CurrentManagedThreadId;

        var registry = new TestTranslationProviderRegistry(settingsService);
        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { registry },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        var draft = new TranslationProviderDraft(
            Profile: initial.Translation.Providers[0],
            BaseProfile: initial.Translation.Providers[0],
            Credentials: new ProviderCredentialEdit(CredentialEditKind.Keep));

        var tcs = new TaskCompletionSource<ProviderTestResult>();
        var thread = new Thread(() =>
        {
            secretStore.CallerThreadId = Environment.CurrentManagedThreadId;
            try
            {
                var res = coordinator.TestDraftAsync(draft, CancellationToken.None).GetAwaiter().GetResult();
                tcs.SetResult(res);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        })
        {
            IsBackground = true
        };
        thread.Start();

        var result = await tcs.Task;
        result.Success.ShouldBeTrue();
        secretStore.ReadOnCallerThread.ShouldBeFalse();
    }

    // =========================================================================
    // 场景 23: Replace 修复旧坏引用 (R1-4)
    // 关键断言: 旧 BaseProfile 的坏引用不阻断 Replace 新密钥测试
    // =========================================================================
    [Fact]
    public async Task Scenario23_Replace_FixesBadOldReference_Succeeds()
    {
        var badProfile = new TranslationProviderProfile
        {
            Id = "tp-000000000023",
            DisplayName = "Bad Ref Profile",
            Kind = TranslationProviderKind.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o",
            CredentialSetId = "invalid-reference-string",
            Enabled = true
        };

        var settings = new AppSettings
        {
            Translation = new TranslationSettings
            {
                Providers = new List<TranslationProviderProfile> { badProfile }
            }
        };

        var settingsService = new TestSettingsService(settings);
        var secretStore = new TestSecretStore();
        var registry = new TestTranslationProviderRegistry(settingsService);
        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { registry },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        var replaceDraft = new TranslationProviderDraft(
            Profile: badProfile,
            BaseProfile: badProfile,
            Credentials: new ProviderCredentialEdit(CredentialEditKind.Replace, new ProviderCredentials("sk-replacement-key", null)));

        var result = await coordinator.TestDraftAsync(replaceDraft, TestContext.Current.CancellationToken);
        result.Success.ShouldBeTrue();
        result.Error.ShouldBeNull();
        registry.CreatedSnapshots.Count.ShouldBe(1);
    }

    // =========================================================================
    // 场景 24: 无效超时等在工厂前拒绝 (R1-4)
    // 关键断言: TimeoutSeconds=0 拒绝为 InvalidArgument，工厂调用次数为 0
    // =========================================================================
    [Fact]
    public async Task Scenario24_ListDraftModelsAsync_InvalidTimeout_RejectedBeforeFactory()
    {
        var initial = CreateInitialSettings(out _, out _);
        var settingsService = new TestSettingsService(initial);
        var secretStore = new TestSecretStore();
        var registry = new TestTranslationProviderRegistry(settingsService);
        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { registry },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        var invalidTimeoutDraft = new TranslationProviderDraft(
            Profile: initial.Translation.Providers[0] with { TimeoutSeconds = 0 },
            BaseProfile: null,
            Credentials: new ProviderCredentialEdit(CredentialEditKind.Replace, new ProviderCredentials("fake-key", null)));

        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await coordinator.ListDraftModelsAsync(invalidTimeoutDraft, TestContext.Current.CancellationToken);
        });
        ex.Code.ShouldBe(ErrorCode.InvalidArgument);
        registry.CreatedSnapshots.Count.ShouldBe(0);
    }

    // =========================================================================
    // 场景 25: 瞬态工厂异常安全转换为 ProviderTestResult (R1-5)
    // 关键断言: 瞬态工厂抛出异常时封装为 ProviderTestResult(Success: false)，不向外漏 IrisException
    // =========================================================================
    [Fact]
    public async Task Scenario25_TestDraftAsync_TransientFactoryThrows_ConvertsToProviderTestResult()
    {
        var initial = CreateInitialSettings(out _, out _);
        var settingsService = new TestSettingsService(initial);
        var secretStore = new TestSecretStore();
        var registry = new TestTranslationProviderRegistry(settingsService)
        {
            ProviderFactory = (p, c) => throw new IrisException(ErrorCode.Unknown, "Injected factory failure")
        };

        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { registry },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        var draft = new TranslationProviderDraft(
            Profile: initial.Translation.Providers[0],
            BaseProfile: null,
            Credentials: new ProviderCredentialEdit(CredentialEditKind.Clear));

        var result = await coordinator.TestDraftAsync(draft, TestContext.Current.CancellationToken);
        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }

    // =========================================================================
    // 场景 26: TestDraftAsync caller 取消抛出 OCE (R1-5)
    // 关键断言: caller 取消时按契约抛出 OperationCanceledException
    // =========================================================================
    [Fact]
    public async Task Scenario26_TestDraftAsync_CallerCancellation_ThrowsOCE()
    {
        var initial = CreateInitialSettings(out _, out _);
        var settingsService = new TestSettingsService(initial);
        var secretStore = new TestSecretStore();
        var registry = new TestTranslationProviderRegistry(settingsService);
        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { registry },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var draft = new TranslationProviderDraft(
            Profile: initial.Translation.Providers[0],
            BaseProfile: null,
            Credentials: new ProviderCredentialEdit(CredentialEditKind.Replace, new ProviderCredentials("fake-key", null)));

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await coordinator.TestDraftAsync(draft, cts.Token);
        });
    }

    // =========================================================================
    // 场景 27: Keep 凭据捕获期间持有编辑权，网络阶段释放编辑权 (R2-1)
    // 关键断言: 密钥读取期间持有 gate 阻止 Commit，网络阶段释放 gate 允许 Commit 完成并删除旧键，草稿测试成功
    // =========================================================================
    [Fact]
    public async Task Scenario27_KeepCapture_SuspendedGet_LocksOutCommit_ReleasesBeforeNetwork()
    {
        var initial = CreateInitialSettings(out _, out _);
        var oldProfile = initial.Translation.Providers[0];
        const string set = "cs-0123456789abcdef0123456789abcdef";
        var profileWithSet = oldProfile with { CredentialSetId = set };
        initial = initial with { Translation = initial.Translation with { Providers = new[] { profileWithSet } } };

        var settingsService = new TestSettingsService(initial);
        var secretStore = new TestSecretStore();
        secretStore.Storage[SecretKeys.CredentialSet(set)] = JsonSerializer.Serialize(new { format = "iris-provider-credentials", version = 1, apiKey = "old-api-key", secretKey = (string?)null });

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var networkEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var networkRelease = new ManualResetEventSlim();
        int gated = 0;

        secretStore.OnGetHook = key =>
        {
            if (key == SecretKeys.CredentialSet(set) && Interlocked.CompareExchange(ref gated, 1, 0) == 0)
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            }
        };

        var registry = new TestTranslationProviderRegistry(settingsService);
        registry.ProviderFactory = (p, c) =>
        {
            var provider = new TestTranslationProvider(p, c);
            provider.OnTestAsync = async ct =>
            {
                await Task.Yield();
                networkEntered.TrySetResult();
                if (!networkRelease.Wait(TimeSpan.FromSeconds(5), ct)) throw new TimeoutException();
                return new ProviderTestResult(true, TimeSpan.FromMilliseconds(5));
            };
            return provider;
        };

        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { registry },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        var draft = new TranslationProviderDraft(
            Profile: profileWithSet,
            BaseProfile: profileWithSet,
            Credentials: new ProviderCredentialEdit(CredentialEditKind.Keep));

        var draftTask = coordinator.TestDraftAsync(draft, TestContext.Current.CancellationToken);

        // 1. 等待草稿进入后台读取
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // 2. 发起 Replace 提交
        var replacePlan = new SettingsEditPlan(
            General: new GeneralSettingsPatch(),
            TranslationChanges: new[]
            {
                new TranslationProfileChange(
                    TranslationProfileChangeKind.Update,
                    BaseProfile: profileWithSet,
                    TargetProfile: profileWithSet,
                    Credentials: new ProviderCredentialEdit(CredentialEditKind.Replace, new ProviderCredentials("new-key", null)))
            });

        var commitTask = coordinator.CommitAsync(replacePlan, TestContext.Current.CancellationToken);

        // 3. 断言 Commit 在读取结束前被编辑锁阻塞，旧密钥未被删除
        var completedPrematurely = await Task.WhenAny(commitTask, Task.Delay(100, TestContext.Current.CancellationToken)) == commitTask;
        completedPrematurely.ShouldBeFalse();
        secretStore.Storage.ContainsKey(SecretKeys.CredentialSet(set)).ShouldBeTrue();

        // 4. 放行凭据读取，进入网络阶段
        release.Set();

        // 5. 等待草稿进入网络测试，此时编辑锁已释放，Commit 能够顺利完成并清理旧键
        await networkEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var commitResult = await commitTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        commitResult.Status.ShouldBe(SettingsEditStatus.Applied);
        secretStore.Storage.ContainsKey(SecretKeys.CredentialSet(set)).ShouldBeFalse();

        // 6. 放行网络调用，草稿测试成功
        networkRelease.Set();
        var draftResult = await draftTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        draftResult.Success.ShouldBeTrue();
    }

    // =========================================================================
    // 场景 28: 草稿错误路径脱敏与安全边界 (R2-2)
    // 关键断言: 任何下层异常不得在 Message, Detail, InnerException, ToString 中泄露敏感标记
    // =========================================================================
    [Fact]
    public async Task Scenario28_DraftErrors_NeverLeakSensitiveMarkers()
    {
        const string marker = "SUPER_SECRET_TOKEN_TO_HIDE_12345";
        var initial = CreateInitialSettings(out _, out _);
        var settingsService = new TestSettingsService(initial);
        var secretStore = new TestSecretStore();
        var registry = new TestTranslationProviderRegistry(settingsService);
        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { registry },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        // 1. ListDraftModels: Provider 抛出含 marker 异常 -> 封装为安全 IrisException，剥离 InnerException
        registry.ProviderFactory = (p, c) =>
        {
            var provider = new TestTranslationProvider(p, c);
            provider.OnListModelsAsync = ct => throw new InvalidOperationException($"Failed with key: {marker}");
            return provider;
        };

        var draftReplace = new TranslationProviderDraft(
            Profile: initial.Translation.Providers[0],
            BaseProfile: null,
            Credentials: new ProviderCredentialEdit(CredentialEditKind.Replace, new ProviderCredentials("fake-key", null)));

        var listEx = await Should.ThrowAsync<IrisException>(async () =>
        {
            await coordinator.ListDraftModelsAsync(draftReplace, TestContext.Current.CancellationToken);
        });
        listEx.InnerException.ShouldBeNull();
        listEx.Message.Contains(marker, StringComparison.Ordinal).ShouldBeFalse();
        listEx.Detail?.Contains(marker, StringComparison.Ordinal).ShouldNotBe(true);
        listEx.ToString().Contains(marker, StringComparison.Ordinal).ShouldBeFalse();

        // 2. TestDraft: SecretStore 抛出含 marker 异常 -> ProviderTestResult 包含安全错误信息
        const string set = "cs-0123456789abcdef0123456789abcdef";
        var profileWithSet = initial.Translation.Providers[0] with { CredentialSetId = set };
        initial = initial with { Translation = initial.Translation with { Providers = new[] { profileWithSet } } };
        var settingsService2 = new TestSettingsService(initial);
        var secretStore2 = new TestSecretStore();
        secretStore2.FailOnGet = key => throw new InvalidOperationException($"Secret error with: {marker}");
        var coordinator2 = new SettingsEditCoordinator(
            settingsService2,
            secretStore2,
            registry,
            new IProviderConfigurationObserver[] { registry },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        var draftKeep = new TranslationProviderDraft(
            Profile: profileWithSet,
            BaseProfile: profileWithSet,
            Credentials: new ProviderCredentialEdit(CredentialEditKind.Keep));

        var testResult = await coordinator2.TestDraftAsync(draftKeep, TestContext.Current.CancellationToken);
        testResult.Success.ShouldBeFalse();
        testResult.Error.ShouldNotBeNull();
        testResult.Error.Message.Contains(marker, StringComparison.Ordinal).ShouldBeFalse();
        testResult.Error.Detail?.Contains(marker, StringComparison.Ordinal).ShouldNotBe(true);

        // 3. State.Current 异常时脱敏
        settingsService2.CurrentFailure = new InvalidOperationException($"Settings error with: {marker}");
        var testCurrentResult = await coordinator2.TestDraftAsync(draftKeep, TestContext.Current.CancellationToken);
        testCurrentResult.Success.ShouldBeFalse();
        testCurrentResult.Error.ShouldNotBeNull();
        testCurrentResult.Error.Message.Contains(marker, StringComparison.Ordinal).ShouldBeFalse();
        testCurrentResult.Error.Detail?.Contains(marker, StringComparison.Ordinal).ShouldNotBe(true);

        var listCurrentEx = await Should.ThrowAsync<IrisException>(async () =>
        {
            await coordinator2.ListDraftModelsAsync(draftKeep, TestContext.Current.CancellationToken);
        });
        listCurrentEx.InnerException.ShouldBeNull();
        listCurrentEx.ToString().Contains(marker, StringComparison.Ordinal).ShouldBeFalse();
    }

    // =========================================================================
    // 场景 29: 草稿操作类型区分校验 (R2-3)
    // 关键断言: 空 ID / 缺失 Prompt / 篡改 CredentialSetId 拦截；模型列表放行 Prompt
    // =========================================================================
    [Fact]
    public async Task Scenario29_ValidateDraft_Operations_RejectionsAndPermissions()
    {
        var initial = CreateInitialSettings(out _, out _);
        var settingsService = new TestSettingsService(initial);
        var secretStore = new TestSecretStore();
        int factoryCalls = 0;
        var registry = new TestTranslationProviderRegistry(settingsService)
        {
            ProviderFactory = (p, c) =>
            {
                factoryCalls++;
                return new TestTranslationProvider(p, c);
            }
        };
        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { registry },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        // 1. 空 ID 草稿 -> 结构校验拦截，不调用工厂
        var emptyIdDraft = new TranslationProviderDraft(
            Profile: initial.Translation.Providers[0] with { Id = "" },
            BaseProfile: null,
            Credentials: new ProviderCredentialEdit(CredentialEditKind.Clear));

        var emptyTestResult = await coordinator.TestDraftAsync(emptyIdDraft, TestContext.Current.CancellationToken);
        emptyTestResult.Success.ShouldBeFalse();
        emptyTestResult.Error.ShouldNotBeNull();
        emptyTestResult.Error.Code.ShouldBe(ErrorCode.InvalidArgument);
        factoryCalls.ShouldBe(0);

        var emptyListEx = await Should.ThrowAsync<IrisException>(async () =>
        {
            await coordinator.ListDraftModelsAsync(emptyIdDraft, TestContext.Current.CancellationToken);
        });
        emptyListEx.Code.ShouldBe(ErrorCode.InvalidArgument);
        factoryCalls.ShouldBe(0);

        // 2. 缺失 PromptTemplateId -> TestDraft 拦截，ListDraftModels 放行
        var missingPromptDraft = new TranslationProviderDraft(
            Profile: initial.Translation.Providers[0] with { Id = "tp-000000000029", CredentialSetId = null, PromptTemplateId = "missing-custom-prompt" },
            BaseProfile: null,
            Credentials: new ProviderCredentialEdit(CredentialEditKind.Replace, new ProviderCredentials("valid-key", null)));

        var promptTestResult = await coordinator.TestDraftAsync(missingPromptDraft, TestContext.Current.CancellationToken);
        promptTestResult.Success.ShouldBeFalse();
        promptTestResult.Error.ShouldNotBeNull();
        promptTestResult.Error.Code.ShouldBe(ErrorCode.InvalidArgument);
        factoryCalls.ShouldBe(0);

        var models = await coordinator.ListDraftModelsAsync(missingPromptDraft, TestContext.Current.CancellationToken);
        models.Count.ShouldBeGreaterThan(0);
        factoryCalls.ShouldBe(1);

        // 3. 已有草稿篡改 CredentialSetId -> 拦截
        const string set1 = "cs-11111111111111111111111111111111";
        const string set2 = "cs-22222222222222222222222222222222";
        var tamperedDraft = new TranslationProviderDraft(
            Profile: initial.Translation.Providers[0] with { CredentialSetId = set2 },
            BaseProfile: initial.Translation.Providers[0] with { CredentialSetId = set1 },
            Credentials: new ProviderCredentialEdit(CredentialEditKind.Keep));

        var tamperedResult = await coordinator.TestDraftAsync(tamperedDraft, TestContext.Current.CancellationToken);
        tamperedResult.Success.ShouldBeFalse();
        tamperedResult.Error.ShouldNotBeNull();
        tamperedResult.Error.Code.ShouldBe(ErrorCode.InvalidArgument);
    }

    // =========================================================================
    // 场景 30: 重试未完成时重新建立当前观察者屏障 (R2-4)
    // 关键断言: 观察者失败阻止删除旧键，恢复后重试成功删除，后续重试幂等
    // =========================================================================
    [Fact]
    public async Task Scenario30_RetryApply_ReEstablishesObserverBarrier_PreventsPrematureCleanup()
    {
        const string set = "cs-0123456789abcdef0123456789abcdef";
        var initial = CreateInitialSettings(out _, out _);
        var oldProfile = initial.Translation.Providers[0] with { CredentialSetId = set };
        initial = initial with { Translation = initial.Translation with { Providers = new[] { oldProfile } } };

        var settingsService = new TestSettingsService(initial);
        var secretStore = new TestSecretStore();
        secretStore.Storage[SecretKeys.CredentialSet(set)] = JsonSerializer.Serialize(new { format = "iris-provider-credentials", version = 1, apiKey = "old-key" });

        // 初始提交：删除旧键失败
        secretStore.FailOnRemove = k => k == SecretKeys.CredentialSet(set);

        var observer = new TestConfigurationObserver();
        var registry = new TestTranslationProviderRegistry(settingsService);
        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { registry, observer },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        var plan = new SettingsEditPlan(
            General: new GeneralSettingsPatch(),
            TranslationChanges: new[]
            {
                new TranslationProfileChange(
                    TranslationProfileChangeKind.Update,
                    BaseProfile: oldProfile,
                    TargetProfile: oldProfile,
                    Credentials: new ProviderCredentialEdit(CredentialEditKind.Replace, new ProviderCredentials("new-key", null)))
            });

        var initialResult = await coordinator.CommitAsync(plan, TestContext.Current.CancellationToken);
        initialResult.Status.ShouldBe(SettingsEditStatus.ApplyPending);
        initialResult.PendingApply.ShouldNotBeNull();
        observer.RefreshCount.ShouldBe(1);

        // 第一次重试：移除删除拦截，但观察者失败 -> 必须重新刷新观察者并拦截清理
        secretStore.FailOnRemove = null;
        observer.Fail = true;

        var retry1 = await coordinator.RetryApplyAsync(initialResult.PendingApply, TestContext.Current.CancellationToken);
        retry1.IsComplete.ShouldBeFalse();
        observer.RefreshCount.ShouldBe(2);
        // 旧密钥不得被删除
        secretStore.Storage.ContainsKey(SecretKeys.CredentialSet(set)).ShouldBeTrue();

        // 第二次重试：恢复观察者 -> 观察者成功且旧密钥被清理
        observer.Fail = false;
        var retry2 = await coordinator.RetryApplyAsync(initialResult.PendingApply, TestContext.Current.CancellationToken);
        retry2.IsComplete.ShouldBeTrue();
        observer.RefreshCount.ShouldBe(3);
        secretStore.Storage.ContainsKey(SecretKeys.CredentialSet(set)).ShouldBeFalse();

        // 第三次重试：回执已完成，幂等返回，不再调用观察者
        var retry3 = await coordinator.RetryApplyAsync(initialResult.PendingApply, TestContext.Current.CancellationToken);
        retry3.IsComplete.ShouldBeTrue();
        observer.RefreshCount.ShouldBe(3);
    }

    // =========================================================================
    // 场景 31: 模型发现校验分流范围收尾 (R3)
    // 关键断言:
    // 1. OpenAICompatible 未填写 ChatCompletionsPath 或 MaxTokensField 未定义:
    //    ListDraftModelsAsync 允许并调用工厂获取模型列表;
    //    TestDraftAsync 与 CommitAsync 均拒绝为 InvalidArgument;
    // 2. Anthropic Effort 为非法未支持值:
    //    ListDraftModelsAsync 允许并调用工厂获取模型列表;
    //    TestDraftAsync 与 CommitAsync 均拒绝为 InvalidArgument;
    // 3. 认证敏感请求头或非法 BaseUrl:
    //    ListDraftModelsAsync, TestDraftAsync 与 CommitAsync 均拒绝为 InvalidArgument, 工厂不被调用;
    // 4. 所有失败情况设置均无副作用.
    // =========================================================================
    [Fact]
    public async Task Scenario31_CatalogValidationScope_ScopedToCatalogInputs()
    {
        var initial = CreateInitialSettings(out _, out _);
        var settingsService = new TestSettingsService(initial);
        var secretStore = new TestSecretStore();
        int factoryCalls = 0;
        var registry = new TestTranslationProviderRegistry(settingsService)
        {
            ProviderFactory = (p, c) =>
            {
                Interlocked.Increment(ref factoryCalls);
                return new TestTranslationProvider(p, c);
            }
        };
        var logger = new TestLogger<SettingsEditCoordinator>();
        var promptProvider = Substitute.For<IPromptTemplateProvider>();
        promptProvider.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var coordinator = new SettingsEditCoordinator(
            settingsService,
            secretStore,
            registry,
            new IProviderConfigurationObserver[] { registry },
            promptProvider,
            new FakeTimeProvider(),
            logger);

        const string validKey = "fake-valid-key";
        var creds = new ProviderCredentialEdit(CredentialEditKind.Replace, new ProviderCredentials(validKey, null));

        SettingsEditPlan MakeAddPlan(TranslationProviderProfile profile)
        {
            var baseIds = settingsService.Current.Translation.Providers.Select(p => p.Id).ToList();
            var targetIds = baseIds.Concat(new[] { profile.Id }).ToList();
            return new SettingsEditPlan(
                new GeneralSettingsPatch(),
                new[] { new TranslationProfileChange(TranslationProfileChangeKind.Add, null, profile, creds) },
                new TranslationOrderChange(baseIds, targetIds));
        }

        // 1. Valid 对照
        var validProfile = new TranslationProviderProfile
        {
            Id = "tp-000000000031",
            DisplayName = "Valid OpenAI",
            Kind = TranslationProviderKind.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o",
            OpenAIOptions = new OpenAICompatibleOptions()
        };
        var validDraft = new TranslationProviderDraft(validProfile, null, creds);
        var validModels = await coordinator.ListDraftModelsAsync(validDraft, TestContext.Current.CancellationToken);
        validModels.Count.ShouldBeGreaterThan(0);
        factoryCalls.ShouldBe(1);

        var validTest = await coordinator.TestDraftAsync(validDraft, TestContext.Current.CancellationToken);
        validTest.Success.ShouldBeTrue();

        var validCommit = await coordinator.CommitAsync(MakeAddPlan(validProfile), TestContext.Current.CancellationToken);
        validCommit.Status.ShouldBe(SettingsEditStatus.Applied);

        // 2. OpenAICompatible: Empty ChatCompletionsPath -> List 放行，Test/Commit 拒绝
        factoryCalls = 0;
        var emptyChatPathProfile = new TranslationProviderProfile
        {
            Id = "tp-000000000032",
            DisplayName = "Empty Chat Path",
            Kind = TranslationProviderKind.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o",
            OpenAIOptions = new OpenAICompatibleOptions { ChatCompletionsPath = "" }
        };
        var emptyChatPathDraft = new TranslationProviderDraft(emptyChatPathProfile, null, creds);

        var listModelsEmptyChat = await coordinator.ListDraftModelsAsync(emptyChatPathDraft, TestContext.Current.CancellationToken);
        listModelsEmptyChat.Count.ShouldBeGreaterThan(0);
        factoryCalls.ShouldBe(1);

        var testEmptyChat = await coordinator.TestDraftAsync(emptyChatPathDraft, TestContext.Current.CancellationToken);
        testEmptyChat.Success.ShouldBeFalse();
        testEmptyChat.Error.ShouldNotBeNull();
        testEmptyChat.Error.Code.ShouldBe(ErrorCode.InvalidArgument);
        factoryCalls.ShouldBe(1); // 工厂在 Test 中未被调用

        var commitEmptyChat = await coordinator.CommitAsync(MakeAddPlan(emptyChatPathProfile), TestContext.Current.CancellationToken);
        commitEmptyChat.Status.ShouldBe(SettingsEditStatus.Uncommitted);
        settingsService.Current.Translation.Providers.Any(p => p.Id == emptyChatPathProfile.Id).ShouldBeFalse();

        // 3. OpenAICompatible: Unknown MaxTokensField -> List 放行，Test/Commit 拒绝
        factoryCalls = 0;
        var unknownTokensProfile = new TranslationProviderProfile
        {
            Id = "tp-000000000033",
            DisplayName = "Unknown Tokens Field",
            Kind = TranslationProviderKind.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o",
            OpenAIOptions = new OpenAICompatibleOptions { MaxTokensField = (MaxTokensField)999 }
        };
        var unknownTokensDraft = new TranslationProviderDraft(unknownTokensProfile, null, creds);

        var listModelsUnknownTokens = await coordinator.ListDraftModelsAsync(unknownTokensDraft, TestContext.Current.CancellationToken);
        listModelsUnknownTokens.Count.ShouldBeGreaterThan(0);
        factoryCalls.ShouldBe(1);

        var testUnknownTokens = await coordinator.TestDraftAsync(unknownTokensDraft, TestContext.Current.CancellationToken);
        testUnknownTokens.Success.ShouldBeFalse();
        testUnknownTokens.Error.ShouldNotBeNull();
        testUnknownTokens.Error.Code.ShouldBe(ErrorCode.InvalidArgument);
        factoryCalls.ShouldBe(1);

        var commitUnknownTokens = await coordinator.CommitAsync(MakeAddPlan(unknownTokensProfile), TestContext.Current.CancellationToken);
        commitUnknownTokens.Status.ShouldBe(SettingsEditStatus.Uncommitted);
        settingsService.Current.Translation.Providers.Any(p => p.Id == unknownTokensProfile.Id).ShouldBeFalse();

        // 4. Anthropic: Invalid Effort -> List 放行，Test/Commit 拒绝
        factoryCalls = 0;
        var invalidEffortProfile = new TranslationProviderProfile
        {
            Id = "tp-000000000034",
            DisplayName = "Invalid Effort Anthropic",
            Kind = TranslationProviderKind.Anthropic,
            BaseUrl = null,
            Model = "claude-3-7-sonnet",
            AnthropicOptions = new AnthropicOptions { Effort = "unfinished-input" }
        };
        var invalidEffortDraft = new TranslationProviderDraft(invalidEffortProfile, null, creds);

        var listModelsInvalidEffort = await coordinator.ListDraftModelsAsync(invalidEffortDraft, TestContext.Current.CancellationToken);
        listModelsInvalidEffort.Count.ShouldBeGreaterThan(0);
        factoryCalls.ShouldBe(1);

        var testInvalidEffort = await coordinator.TestDraftAsync(invalidEffortDraft, TestContext.Current.CancellationToken);
        testInvalidEffort.Success.ShouldBeFalse();
        testInvalidEffort.Error.ShouldNotBeNull();
        testInvalidEffort.Error.Code.ShouldBe(ErrorCode.InvalidArgument);
        factoryCalls.ShouldBe(1);

        var commitInvalidEffort = await coordinator.CommitAsync(MakeAddPlan(invalidEffortProfile), TestContext.Current.CancellationToken);
        commitInvalidEffort.Status.ShouldBe(SettingsEditStatus.Uncommitted);
        settingsService.Current.Translation.Providers.Any(p => p.Id == invalidEffortProfile.Id).ShouldBeFalse();

        // 5. Forbidden Auth Header -> List 拒绝，Test/Commit 拒绝，工厂不调用
        factoryCalls = 0;
        var forbiddenHeaderProfile = new TranslationProviderProfile
        {
            Id = "tp-000000000035",
            DisplayName = "Forbidden Header",
            Kind = TranslationProviderKind.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o",
            OpenAIOptions = new OpenAICompatibleOptions
            {
                ExtraHeaders = new Dictionary<string, string> { ["Authorization"] = "fake-token" }
            }
        };
        var forbiddenHeaderDraft = new TranslationProviderDraft(forbiddenHeaderProfile, null, creds);

        var listEx = await Should.ThrowAsync<IrisException>(async () =>
        {
            await coordinator.ListDraftModelsAsync(forbiddenHeaderDraft, TestContext.Current.CancellationToken);
        });
        listEx.Code.ShouldBe(ErrorCode.InvalidArgument);
        factoryCalls.ShouldBe(0);

        var testForbiddenHeader = await coordinator.TestDraftAsync(forbiddenHeaderDraft, TestContext.Current.CancellationToken);
        testForbiddenHeader.Success.ShouldBeFalse();
        testForbiddenHeader.Error.ShouldNotBeNull();
        testForbiddenHeader.Error.Code.ShouldBe(ErrorCode.InvalidArgument);
        factoryCalls.ShouldBe(0);

        var commitForbiddenHeader = await coordinator.CommitAsync(MakeAddPlan(forbiddenHeaderProfile), TestContext.Current.CancellationToken);
        commitForbiddenHeader.Status.ShouldBe(SettingsEditStatus.Uncommitted);
        settingsService.Current.Translation.Providers.Any(p => p.Id == forbiddenHeaderProfile.Id).ShouldBeFalse();

        // 6. Invalid Endpoint -> List 拒绝，Test/Commit 拒绝，工厂不调用
        factoryCalls = 0;
        var invalidEndpointProfile = new TranslationProviderProfile
        {
            Id = "tp-000000000036",
            DisplayName = "Invalid Endpoint",
            Kind = TranslationProviderKind.OpenAICompatible,
            BaseUrl = "file:///probe",
            Model = "gpt-4o",
            OpenAIOptions = new OpenAICompatibleOptions()
        };
        var invalidEndpointDraft = new TranslationProviderDraft(invalidEndpointProfile, null, creds);

        var listEndpointEx = await Should.ThrowAsync<IrisException>(async () =>
        {
            await coordinator.ListDraftModelsAsync(invalidEndpointDraft, TestContext.Current.CancellationToken);
        });
        listEndpointEx.Code.ShouldBe(ErrorCode.InvalidArgument);
        factoryCalls.ShouldBe(0);

        var testInvalidEndpoint = await coordinator.TestDraftAsync(invalidEndpointDraft, TestContext.Current.CancellationToken);
        testInvalidEndpoint.Success.ShouldBeFalse();
        testInvalidEndpoint.Error.ShouldNotBeNull();
        testInvalidEndpoint.Error.Code.ShouldBe(ErrorCode.InvalidArgument);
        factoryCalls.ShouldBe(0);

        var commitInvalidEndpoint = await coordinator.CommitAsync(MakeAddPlan(invalidEndpointProfile), TestContext.Current.CancellationToken);
        commitInvalidEndpoint.Status.ShouldBe(SettingsEditStatus.Uncommitted);
        settingsService.Current.Translation.Providers.Any(p => p.Id == invalidEndpointProfile.Id).ShouldBeFalse();
    }
}
