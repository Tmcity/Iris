using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.App.Tests.TestSupport;
using Iris.Core.Common;
using Iris.Core.DependencyInjection;
using Iris.Core.Errors;
using Iris.Core.Platform;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Iris.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Settings;

public sealed class SettingsTranslationSaveIntegrationTests
{
    #region Controlled Test Doubles

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "IrisAppTests_" + Guid.NewGuid().ToString("N"));

        public TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private sealed class TestAppPaths(string dataDirectory) : IAppPaths
    {
        public bool IsPortable => false;
        public string DataDirectory { get; } = dataDirectory;
        public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
        public string SecretsFile => Path.Combine(DataDirectory, "secrets.dat");
        public string DatabaseFile => Path.Combine(DataDirectory, "history.db");
        public string LogDirectory => Path.Combine(DataDirectory, "logs");
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        public ConcurrentDictionary<string, string> Storage { get; } = new(StringComparer.Ordinal);
        public List<string> SetCalls { get; } = [];
        public List<string> RemoveCalls { get; } = [];

        public string? Get(string key)
        {
            Storage.TryGetValue(key, out var val);
            return val;
        }

        public void Set(string key, string value)
        {
            SetCalls.Add(key);
            Storage[key] = value;
            Changed?.Invoke(this, key);
        }

        public bool Remove(string key)
        {
            RemoveCalls.Add(key);
            var removed = Storage.TryRemove(key, out _);
            if (removed)
            {
                Changed?.Invoke(this, key);
            }
            return removed;
        }

        public event EventHandler<string>? Changed;
    }

    private sealed class FakeTranslationProvider : ITranslationProvider, IModelCatalogSource
    {
        public ProviderDescriptor Descriptor { get; }
        public TranslationProviderProfile Profile { get; }
        public ProviderCredentials Credentials { get; }

        public FakeTranslationProvider(TranslationProviderProfile profile, ProviderCredentials credentials)
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
            yield return new TranslationChunk("translated text");
        }

        public Task<ProviderTestResult> TestAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new ProviderTestResult(true, TimeSpan.FromMilliseconds(50), "Hello sample"));
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<string>>(["model-alpha", "model-beta"]);
        }
    }

    private sealed class CountingSettingsService : ISettingsService
    {
        public bool IsReadOnly => false;
        private readonly ISettingsService _inner;
        public int UpdateCount { get; private set; }

        public CountingSettingsService(ISettingsService inner)
        {
            _inner = inner;
        }

        public AppSettings Current => _inner.Current;

        public event EventHandler<SettingsChangedEventArgs>? Changed
        {
            add => _inner.Changed += value;
            remove => _inner.Changed -= value;
        }

        public Task LoadAsync(CancellationToken cancellationToken = default) => _inner.LoadAsync(cancellationToken);

        public async Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default)
        {
            UpdateCount++;
            await _inner.UpdateAsync(mutate, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class FakeAutoStartService : IAutoStartService
    {
        public bool Enabled { get; set; }
        public int SetEnabledCalls { get; private set; }
        public bool ThrowOnSet { get; set; }

        public bool IsEnabled() => Enabled;

        public void SetEnabled(bool enabled)
        {
            SetEnabledCalls++;
            if (ThrowOnSet)
            {
                throw new InvalidOperationException("Simulated auto start error");
            }
            Enabled = enabled;
        }
    }

    #endregion

    private static (
        IServiceProvider ServiceProvider,
        CountingSettingsService SettingsService,
        MemorySecretStore SecretStore,
        FakeAutoStartService AutoStart,
        ISettingsEditCoordinator CoreCoordinator,
        TempDirectory TempDir)
        CreateIntegrationEnvironment(AppSettings? initialSettings = null)
    {
        var tempDir = new TempDirectory();
        var paths = new TestAppPaths(tempDir.Path);
        var secretStore = new MemorySecretStore();
        var autoStart = new FakeAutoStartService();
        var uiDispatcher = new ImmediateUiDispatcher();
        var receiptManager = new SettingsApplyReceiptManager();

        var registry = Substitute.For<ITranslationProviderRegistry>();
        registry.CreateTransientAsync(Arg.Any<TranslationProviderProfile>(), Arg.Any<ProviderCredentials>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var profile = callInfo.Arg<TranslationProviderProfile>();
                var creds = callInfo.Arg<ProviderCredentials>();
                var fakeProvider = new FakeTranslationProvider(profile, creds);
                var snapshot = Substitute.For<ITranslationProviderSnapshot>();
                snapshot.Providers.Returns([new TranslationProviderRegistration(profile, fakeProvider, Guid.NewGuid())]);
                return Task.FromResult(snapshot);
            });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAppPaths>(paths);
        services.AddSingleton<ISecretStore>(secretStore);
        services.AddSingleton<IAutoStartService>(autoStart);
        services.AddSingleton<ISettingsApplyReceiptManager>(receiptManager);
        services.AddSingleton<IUiDispatcher>(uiDispatcher);
        services.AddSingleton<ThemeService>();
        services.AddSingleton(registry);

        services.AddIrisInfrastructure();
        services.AddIrisCore();
        // This legacy UI fixture supplies no OCR factories; keep OCR as an external boundary.
        services.AddSingleton(Substitute.For<Iris.Core.Ocr.IOcrProviderRegistry>());

        // 装饰 ISettingsService 以便统计 UpdateAsync 次数
        var bootstrapSp = services.BuildServiceProvider();
        var rawSettingsService = bootstrapSp.GetRequiredService<ISettingsService>();
        rawSettingsService.LoadAsync().GetAwaiter().GetResult();
        if (initialSettings != null)
        {
            rawSettingsService.UpdateAsync(_ => initialSettings).GetAwaiter().GetResult();
        }
        var countingSettingsService = new CountingSettingsService(rawSettingsService);

        // 替换注册 countingSettingsService
        services.Remove(services.First(d => d.ServiceType == typeof(ISettingsService)));
        services.AddSingleton<ISettingsService>(countingSettingsService);

        services.AddScoped<GeneralSettingsSaveCoordinator>();
        services.AddScoped<SettingsViewModel>();
        services.AddTransient<SettingsWindow>();

        var rootProvider = services.BuildServiceProvider();
        var coreCoordinator = rootProvider.GetRequiredService<ISettingsEditCoordinator>();

        return (rootProvider, countingSettingsService, secretStore, autoStart, coreCoordinator, tempDir);
    }

    [Fact]
    public async Task UnifiedSave_GeneralAndMultipleProfilesAndKeyAndOrder_InvokesSingleUpdateAsync_AndPersistsAllChanges()
    {
        // 初始配置包含一个 Provider
        var existingProfile = new TranslationProviderProfile
        {
            Id = "provider-existing",
            Kind = TranslationProviderKind.OpenAICompatible,
            DisplayName = "Existing Provider",
            BaseUrl = "https://api.existing.com",
            Model = "existing-model",
            CredentialSetId = "cred-existing-set",
            Order = 0,
        };

        var initial = new AppSettings
        {
            General = new GeneralSettings { Theme = AppTheme.Light, LaunchAtStartup = false },
            Translation = new TranslationSettings
            {
                Providers = [existingProfile]
            }
        };

        var (provider, settingsService, secretStore, autoStart, _, tempDir) = CreateIntegrationEnvironment(initial);
        using var _ = tempDir;
        secretStore.Set(SecretKeys.CredentialSet("cred-existing-set"), JsonSerializer.Serialize(new { ApiKey = "old-key" }));

        using var scope = provider.CreateScope();
        var vm = scope.ServiceProvider.GetRequiredService<SettingsViewModel>();

        // 1. 修改常规设置
        vm.Theme = AppTheme.Dark;
        vm.LaunchAtStartup = true;

        // 2. 添加一个新服务（DeepSeek）并设置 Replace 密钥
        var deepseekPreset = SettingsViewModel.AvailablePresets.First(p => p.DisplayName.Contains("DeepSeek", StringComparison.OrdinalIgnoreCase));
        vm.AddProfileFromPreset(deepseekPreset);
        var newDraft = vm.SelectedProfile!;
        newDraft.Model = "deepseek-chat";
        newDraft.CredentialEditKind = CredentialEditKind.Replace;
        newDraft.SetReplacementApiKey("sk-deepseek-new-key-12345");

        // 3. 修改已有服务的 DisplayName (Keep 模式)
        var existingDraft = vm.Profiles.First(p => p.Id == "provider-existing");
        existingDraft.DisplayName = "Updated Existing Name";
        existingDraft.CredentialEditKind.ShouldBe(CredentialEditKind.Keep);

        // 4. 调整顺序：将新添加的移到最前
        vm.SelectedProfile = newDraft;
        vm.ExecuteMoveUp();

        var initialUpdateCount = settingsService.UpdateCount;

        // 5. 执行联合保存
        await vm.ExecuteSaveAsync();

        // 验证结果状态
        vm.IsDirty.ShouldBeFalse();
        vm.IsConflict.ShouldBeFalse();
        vm.StatusMessage.ShouldBe(UiText.SettingsStatusSaved);

        // 验证恰好发生了一次 UpdateAsync
        (settingsService.UpdateCount - initialUpdateCount).ShouldBe(1);

        // 验证当前内存与写盘设置
        var current = settingsService.Current;
        current.General.Theme.ShouldBe(AppTheme.Dark);
        current.General.LaunchAtStartup.ShouldBe(true);
        autoStart.Enabled.ShouldBeTrue();

        current.Translation.Providers.Count.ShouldBe(2);
        // 顺序验证
        current.Translation.Providers[0].Id.ShouldBe(newDraft.Id);
        current.Translation.Providers[1].Id.ShouldBe(existingDraft.Id);
        current.Translation.Providers[1].DisplayName.ShouldBe("Updated Existing Name");

        // 凭据验证：新 Profile 分配了凭据集 ID，且存储中有值
        current.Translation.Providers[0].CredentialSetId.ShouldNotBeNull();
        secretStore.Storage.ContainsKey(SecretKeys.CredentialSet(current.Translation.Providers[0].CredentialSetId!)).ShouldBeTrue();
        secretStore.Storage[SecretKeys.CredentialSet(current.Translation.Providers[0].CredentialSetId!)].ShouldContain("sk-deepseek-new-key-12345");

        // 旧 Profile 保持原凭据集 ID
        current.Translation.Providers[1].CredentialSetId.ShouldBe("cred-existing-set");

        // 安全红线验证：设置文件内容与模型配置中绝不能出现原始明文密钥
        var settingsJson = File.ReadAllText(Path.Combine(tempDir.Path, "settings.json"));
        settingsJson.ShouldNotContain("sk-deepseek-new-key-12345");
        settingsJson.ShouldNotContain("old-key");
    }

    [Fact]
    public async Task KeepReplaceClear_CredentialBehaviors_HandledCorrectly()
    {
        const string oldSetId = "cs-0123456789abcdef0123456789abcdef";
        var existingProfile = new TranslationProviderProfile
        {
            Id = "provider-to-clear",
            Kind = TranslationProviderKind.OpenAICompatible,
            DisplayName = "Clear Cred Provider",
            BaseUrl = "https://api.openai.com",
            Model = "gpt-4o",
            CredentialSetId = oldSetId,
            Order = 0,
        };

        var initial = new AppSettings
        {
            Translation = new TranslationSettings { Providers = [existingProfile] }
        };

        var (provider, settingsService, secretStore, _, _, tempDir) = CreateIntegrationEnvironment(initial);
        using var _ = tempDir;
        secretStore.Set(SecretKeys.CredentialSet(oldSetId), JsonSerializer.Serialize(new { ApiKey = "clear-target-key" }));

        using var scope = provider.CreateScope();
        var vm = scope.ServiceProvider.GetRequiredService<SettingsViewModel>();

        var draft = vm.Profiles[0];
        draft.CredentialEditKind = CredentialEditKind.Clear;

        await vm.ExecuteSaveAsync();

        // 验证已提交且分配新凭据集，旧密钥从 secret store 清理
        var current = settingsService.Current.Translation.Providers[0];
        current.CredentialSetId.ShouldNotBeNull();
        current.CredentialSetId.ShouldNotBe(oldSetId);
        secretStore.Storage.ContainsKey(SecretKeys.CredentialSet(oldSetId)).ShouldBeFalse();
    }

    [Fact]
    public void ModelDiscovery_EmptyModelAllowed_IncompleteParamsDoNotBlockListModels_ButBlockSaveAndTest()
    {
        var draft = TranslationProfileDraftViewModel.CreateFromPreset(SettingsViewModel.AvailablePresets[0]);
        draft.Model = ""; // 清空模型

        // 空模型不应阻止获取模型列表
        draft.ValidateForListModels().ShouldBeNull();

        // 但应阻止保存和测试
        draft.ValidateForTest().ShouldNotBeNull();
        draft.ValidateForTest()!.ShouldContain("模型");
        draft.ValidateForSave().ShouldNotBeNull();
        draft.ValidateForSave()!.ShouldContain("模型");

        // 未完成的生成数值（如温度输了一半为非数字）
        draft.Model = "deepseek-chat";
        draft.TemperatureText = "abc";
        draft.ValidateForListModels().ShouldBeNull("生成参数尚未完成不阻止模型发现");
        draft.ValidateForSave().ShouldNotBeNull("未完成参数阻止保存");
        draft.ValidateForTest().ShouldNotBeNull("未完成参数阻止测试");

        // 坏端点 URL
        draft.BaseUrl = "invalid-url-protocol";
        draft.ValidateForListModels().ShouldNotBeNull();
        draft.ValidateForListModels()!.ShouldContain("URL");
    }

    [Fact]
    public void HaikuSelection_ClearsEffortWithWarning()
    {
        var draft = TranslationProfileDraftViewModel.CreateFromPreset(
            SettingsViewModel.AvailablePresets.First(p => p.Kind == TranslationProviderKind.Anthropic));

        draft.Model = "claude-3-7-sonnet-20250219";
        draft.Effort = "low";
        draft.HasHaikuEffortWarning.ShouldBeFalse();

        // 切换为 Claude 3.5 Haiku
        draft.Model = "claude-3-5-haiku-20241022";

        draft.Effort.ShouldBeNull("Haiku 不支持 Effort，应自动清空");
        draft.HasHaikuEffortWarning.ShouldBeTrue("应给出 Haiku Effort 提示");
    }

    [Fact]
    public async Task ExternalConflict_PreservesLocalDraft_AndMarksIsConflict()
    {
        var existingProfile = new TranslationProviderProfile
        {
            Id = "provider-c1",
            Kind = TranslationProviderKind.OpenAICompatible,
            DisplayName = "Initial Name",
            BaseUrl = "https://api.openai.com",
            Model = "gpt-4o",
            Order = 0,
        };

        var initial = new AppSettings
        {
            Translation = new TranslationSettings { Providers = [existingProfile] }
        };

        var (provider, settingsService, _, _, _, tempDir) = CreateIntegrationEnvironment(initial);
        using var _ = tempDir;

        using var scope = provider.CreateScope();
        var vm = scope.ServiceProvider.GetRequiredService<SettingsViewModel>();

        // 本地编辑
        vm.Profiles[0].DisplayName = "Locally Modified Name";

        // 外部并发修改同一项
        await settingsService.UpdateAsync(s => s with
        {
            Translation = s.Translation with
            {
                Providers = [existingProfile with { DisplayName = "Externally Modified Name" }]
            }
        }, TestContext.Current.CancellationToken);

        await vm.ExecuteSaveAsync();

        // 冲突检测触发
        vm.IsConflict.ShouldBeTrue();
        vm.StatusMessage.ShouldBe(UiText.SettingsStatusConflict);

        // 本地草稿必须保留供用户修正或查看，不得被静默冲掉
        vm.Profiles[0].DisplayName.ShouldBe("Locally Modified Name");
    }
}
