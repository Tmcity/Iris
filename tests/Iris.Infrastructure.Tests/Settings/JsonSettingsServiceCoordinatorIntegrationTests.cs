using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Common;
using Iris.Core.DependencyInjection;
using Iris.Core.Errors;
using Iris.Core.Presentation;
using Iris.Core.Security;
using Iris.Core.Serialization;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Iris.Infrastructure.Settings;
using Iris.Infrastructure.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.Infrastructure.Tests.Settings;

public sealed class JsonSettingsServiceCoordinatorIntegrationTests
{
    private sealed class MemorySecretStore : ISecretStore
    {
        public ConcurrentDictionary<string, string> Storage { get; } = new(StringComparer.Ordinal);
        public event EventHandler<string>? Changed;

        public string? Get(string key)
        {
            Storage.TryGetValue(key, out var val);
            return val;
        }

        public void Set(string key, string value)
        {
            Storage[key] = value;
            Changed?.Invoke(this, key);
        }

        public bool Remove(string key)
        {
            var removed = Storage.TryRemove(key, out _);
            if (removed)
            {
                Changed?.Invoke(this, key);
            }
            return removed;
        }
    }

    private sealed class NoOpTranslationProviderRegistry : ITranslationProviderRegistry
    {
        public Task<ITranslationProviderSnapshot> AcquireSnapshotAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ITranslationProviderSnapshot> CreateTransientAsync(TranslationProviderProfile profile, ProviderCredentials credentials, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

#pragma warning disable CS0067
        public event EventHandler? Changed;
#pragma warning restore CS0067

        public void Dispose() { }
    }

    private static JsonSettingsService CreateRealSettingsService(TempDirectory dir)
    {
        var paths = new FakeAppPaths(dir.Path);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        return new JsonSettingsService(paths, Array.Empty<ISettingsMigration>(), time, NullLogger<JsonSettingsService>.Instance);
    }

    private static (ISettingsEditCoordinator Coordinator, ServiceProvider Provider) CreateCoordinator(ISettingsService settingsService, ISecretStore secretStore)
    {
        var services = new ServiceCollection();
        services.AddSingleton(settingsService);
        services.AddSingleton(secretStore);
        services.AddSingleton<ITranslationProviderRegistry>(new NoOpTranslationProviderRegistry());
        services.AddSingleton<ITranslationHistoryRepository>(Substitute.For<ITranslationHistoryRepository>());
        services.AddSingleton<ISettingsWindowPresenter>(Substitute.For<ISettingsWindowPresenter>());
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddIrisCore();
        // JSON/translation integration supplies no OCR factories; OCR is an external boundary here.
        services.AddSingleton(Substitute.For<Iris.Core.Ocr.IOcrProviderRegistry>());

        var sp = services.BuildServiceProvider();
        var coordinator = sp.GetRequiredService<ISettingsEditCoordinator>();
        return (coordinator, sp);
    }

    [Fact]
    public async Task Haiku_DefaultOptionsChecked_RejectsWhenEffortNotNull_AcceptsExplicitNull_PersistsNullEffortToJson()
    {
        using var dir = new TempDirectory();
        using var settingsService = CreateRealSettingsService(dir);
        await settingsService.LoadAsync(TestContext.Current.CancellationToken);

        var secretStore = new MemorySecretStore();
        var (coordinator, sp) = CreateCoordinator(settingsService, secretStore);
        using (sp)
        {
            var initialFileBytes = await File.ReadAllBytesAsync(new FakeAppPaths(dir.Path).SettingsFile, TestContext.Current.CancellationToken);

            // 1. AnthropicOptions == null: 被拒绝为 InvalidArgument，settings 文件完全未改变
            const string haikuId = "tp-0000000000a1";
            var invalidHaikuPlan = new SettingsEditPlan(
                General: new GeneralSettingsPatch(),
                TranslationChanges: new[]
                {
                    new TranslationProfileChange(
                        TranslationProfileChangeKind.Add,
                        BaseProfile: null,
                        TargetProfile: new TranslationProviderProfile
                        {
                            Id = haikuId,
                            DisplayName = "Claude Haiku",
                            Kind = TranslationProviderKind.Anthropic,
                            Model = "claude-3-5-haiku-20241022",
                            AnthropicOptions = null, // 默认补齐 low，但 Haiku 不支持 Effort
                            Enabled = true
                        },
                        Credentials: new ProviderCredentialEdit(CredentialEditKind.Replace, new ProviderCredentials("sk-ant-test", null)))
                },
                TranslationOrder: new TranslationOrderChange(
                    BaseIds: Array.Empty<string>(),
                    TargetIds: new[] { haikuId }));

            var res1 = await coordinator.CommitAsync(invalidHaikuPlan, TestContext.Current.CancellationToken);
            res1.Status.ShouldBe(SettingsEditStatus.Uncommitted);
            res1.Error.ShouldNotBeNull();
            res1.Error.Code.ShouldBe(ErrorCode.InvalidArgument);

            var fileBytesAfterReject = await File.ReadAllBytesAsync(new FakeAppPaths(dir.Path).SettingsFile, TestContext.Current.CancellationToken);
            fileBytesAfterReject.ShouldBe(initialFileBytes);

            // 2. 显式指定 Effort = null: 提交成功并由 JsonSettingsService 写入磁盘
            var validHaikuPlan = new SettingsEditPlan(
                General: new GeneralSettingsPatch(),
                TranslationChanges: new[]
                {
                    new TranslationProfileChange(
                        TranslationProfileChangeKind.Add,
                        BaseProfile: null,
                        TargetProfile: new TranslationProviderProfile
                        {
                            Id = haikuId,
                            DisplayName = "Claude Haiku",
                            Kind = TranslationProviderKind.Anthropic,
                            Model = "claude-3-5-haiku-20241022",
                            AnthropicOptions = new AnthropicOptions { Effort = null },
                            Enabled = true
                        },
                        Credentials: new ProviderCredentialEdit(CredentialEditKind.Replace, new ProviderCredentials("sk-ant-test", null)))
                },
                TranslationOrder: new TranslationOrderChange(
                    BaseIds: Array.Empty<string>(),
                    TargetIds: new[] { haikuId }));

            var res2 = await coordinator.CommitAsync(validHaikuPlan, TestContext.Current.CancellationToken);
            res2.Status.ShouldBe(SettingsEditStatus.Applied);

            // 验证经过真实 SettingsNormalizer 之后，最终配置中的 Effort 保持为 null
            var savedProfile = settingsService.Current.Translation.Providers.FirstOrDefault(p => p.Id == haikuId);
            savedProfile.ShouldNotBeNull();
            savedProfile.AnthropicOptions.ShouldNotBeNull();
            savedProfile.AnthropicOptions.Effort.ShouldBeNull();

            // 检查落盘 JSON 文件（由于 WhenWritingNull 规则，null 不会被写入，因此磁盘上绝无 "low"）
            var fileContent = await File.ReadAllTextAsync(new FakeAppPaths(dir.Path).SettingsFile, TestContext.Current.CancellationToken);
            fileContent.Contains("\"low\"").ShouldBeFalse();
        }
    }

    [Fact]
    public async Task InvalidEnum_Rejected_SettingsFileNotModified()
    {
        using var dir = new TempDirectory();
        using var settingsService = CreateRealSettingsService(dir);
        await settingsService.LoadAsync(TestContext.Current.CancellationToken);

        var secretStore = new MemorySecretStore();
        var (coordinator, sp) = CreateCoordinator(settingsService, secretStore);
        using (sp)
        {
            var initialFileBytes = await File.ReadAllBytesAsync(new FakeAppPaths(dir.Path).SettingsFile, TestContext.Current.CancellationToken);

            const string newId = "tp-0000000000a2";
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
                            DisplayName = "Invalid Enum Provider",
                            Kind = TranslationProviderKind.OpenAICompatible,
                            BaseUrl = "https://api.openai.com/v1",
                            Model = "gpt-4o",
                            Enabled = true
                        },
                        Credentials: new ProviderCredentialEdit((CredentialEditKind)999))
                },
                TranslationOrder: new TranslationOrderChange(
                    BaseIds: Array.Empty<string>(),
                    TargetIds: new[] { newId }));

            var result = await coordinator.CommitAsync(plan, TestContext.Current.CancellationToken);
            result.Status.ShouldBe(SettingsEditStatus.Uncommitted);
            result.Error.ShouldNotBeNull();
            result.Error.Code.ShouldBe(ErrorCode.InvalidArgument);

            var fileBytesAfter = await File.ReadAllBytesAsync(new FakeAppPaths(dir.Path).SettingsFile, TestContext.Current.CancellationToken);
            fileBytesAfter.ShouldBe(initialFileBytes);
        }
    }

    [Fact]
    public async Task LeadingSlashChatCompletionsPath_Applied_PersistedCorrectly()
    {
        using var dir = new TempDirectory();
        using var settingsService = CreateRealSettingsService(dir);
        await settingsService.LoadAsync(TestContext.Current.CancellationToken);

        var secretStore = new MemorySecretStore();
        var (coordinator, sp) = CreateCoordinator(settingsService, secretStore);
        using (sp)
        {
            const string newId = "tp-0000000000a3";
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
                            DisplayName = "Leading Slash OpenAI",
                            Kind = TranslationProviderKind.OpenAICompatible,
                            BaseUrl = "https://api.openai.com/v1",
                            Model = "gpt-4o",
                            OpenAIOptions = new OpenAICompatibleOptions
                            {
                                ChatCompletionsPath = "/v1/chat/completions"
                            },
                            Enabled = true
                        },
                        Credentials: new ProviderCredentialEdit(CredentialEditKind.Replace, new ProviderCredentials("sk-test-key", null)))
                },
                TranslationOrder: new TranslationOrderChange(
                    BaseIds: Array.Empty<string>(),
                    TargetIds: new[] { newId }));

            var result = await coordinator.CommitAsync(plan, TestContext.Current.CancellationToken);
            result.Status.ShouldBe(SettingsEditStatus.Applied);

            var fileContent = await File.ReadAllTextAsync(new FakeAppPaths(dir.Path).SettingsFile, TestContext.Current.CancellationToken);
            var loaded = JsonSerializer.Deserialize<AppSettings>(fileContent, IrisJson.Options);
            loaded.ShouldNotBeNull();
            var saved = loaded.Translation.Providers.FirstOrDefault(p => p.Id == newId);
            saved.ShouldNotBeNull();
            saved.OpenAIOptions.ShouldNotBeNull();
            saved.OpenAIOptions.ChatCompletionsPath.ShouldBe("/v1/chat/completions");
        }
    }

    [Fact]
    public async Task PreserveInactiveOptions_OpenAI_Renamed_RetainsAnthropicOptionsInSavedFile()
    {
        using var dir = new TempDirectory();
        using var settingsService = CreateRealSettingsService(dir);
        await settingsService.LoadAsync(TestContext.Current.CancellationToken);

        var secretStore = new MemorySecretStore();
        var (coordinator, sp) = CreateCoordinator(settingsService, secretStore);
        using (sp)
        {
            const string id = "tp-0000000000b1";
            var initialProfile = new TranslationProviderProfile
            {
                Id = id,
                DisplayName = "Original OpenAI",
                Kind = TranslationProviderKind.OpenAICompatible,
                BaseUrl = "https://api.openai.com/v1",
                Model = "gpt-4o",
                OpenAIOptions = new OpenAICompatibleOptions(),
                AnthropicOptions = new AnthropicOptions { Effort = "high", RefusalFallback = false },
                Enabled = true
            };

            // 1. 新增初始服务
            var addPlan = new SettingsEditPlan(
                General: new GeneralSettingsPatch(),
                TranslationChanges: new[]
                {
                    new TranslationProfileChange(
                        TranslationProfileChangeKind.Add,
                        BaseProfile: null,
                        TargetProfile: initialProfile,
                        Credentials: new ProviderCredentialEdit(CredentialEditKind.Replace, new ProviderCredentials("sk-test", null)))
                },
                TranslationOrder: new TranslationOrderChange(
                    BaseIds: Array.Empty<string>(),
                    TargetIds: new[] { id }));

            var addResult = await coordinator.CommitAsync(addPlan, TestContext.Current.CancellationToken);
            addResult.Status.ShouldBe(SettingsEditStatus.Applied);

            // 2. 仅修改显示名称，保留既有 Options
            var savedInitial = settingsService.Current.Translation.Providers.First(p => p.Id == id);
            var targetProfile = savedInitial with { DisplayName = "Renamed OpenAI" };
            var updatePlan = new SettingsEditPlan(
                General: new GeneralSettingsPatch(),
                TranslationChanges: new[]
                {
                    new TranslationProfileChange(
                        TranslationProfileChangeKind.Update,
                        BaseProfile: savedInitial,
                        TargetProfile: targetProfile,
                        Credentials: new ProviderCredentialEdit(CredentialEditKind.Keep))
                });

            var updateResult = await coordinator.CommitAsync(updatePlan, TestContext.Current.CancellationToken);
            updateResult.Status.ShouldBe(SettingsEditStatus.Applied);

            // 3. 从实际磁盘文件重新读取，断言 AnthropicOptions 未被删除
            var fileContent = await File.ReadAllTextAsync(new FakeAppPaths(dir.Path).SettingsFile, TestContext.Current.CancellationToken);
            var loaded = JsonSerializer.Deserialize<AppSettings>(fileContent, IrisJson.Options);
            loaded.ShouldNotBeNull();
            var saved = loaded.Translation.Providers.FirstOrDefault(p => p.Id == id);
            saved.ShouldNotBeNull();
            saved.DisplayName.ShouldBe("Renamed OpenAI");
            saved.AnthropicOptions.ShouldNotBeNull();
            saved.AnthropicOptions.Effort.ShouldBe("high");
            saved.AnthropicOptions.RefusalFallback.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task PreserveInactiveOptions_Anthropic_Renamed_RetainsOpenAIOptionsInSavedFile()
    {
        using var dir = new TempDirectory();
        using var settingsService = CreateRealSettingsService(dir);
        await settingsService.LoadAsync(TestContext.Current.CancellationToken);

        var secretStore = new MemorySecretStore();
        var (coordinator, sp) = CreateCoordinator(settingsService, secretStore);
        using (sp)
        {
            const string id = "tp-0000000000b2";
            var initialProfile = new TranslationProviderProfile
            {
                Id = id,
                DisplayName = "Original Anthropic",
                Kind = TranslationProviderKind.Anthropic,
                BaseUrl = "https://api.anthropic.com",
                Model = "claude-3-5-sonnet-20241022",
                AnthropicOptions = new AnthropicOptions { Effort = "medium" },
                OpenAIOptions = new OpenAICompatibleOptions { ChatCompletionsPath = "/v1/custom/chat" },
                Enabled = true
            };

            // 1. 新增初始服务
            var addPlan = new SettingsEditPlan(
                General: new GeneralSettingsPatch(),
                TranslationChanges: new[]
                {
                    new TranslationProfileChange(
                        TranslationProfileChangeKind.Add,
                        BaseProfile: null,
                        TargetProfile: initialProfile,
                        Credentials: new ProviderCredentialEdit(CredentialEditKind.Replace, new ProviderCredentials("sk-ant", null)))
                },
                TranslationOrder: new TranslationOrderChange(
                    BaseIds: Array.Empty<string>(),
                    TargetIds: new[] { id }));

            var addResult = await coordinator.CommitAsync(addPlan, TestContext.Current.CancellationToken);
            addResult.Status.ShouldBe(SettingsEditStatus.Applied);

            // 2. 仅修改显示名称，保留既有 Options
            var savedInitial = settingsService.Current.Translation.Providers.First(p => p.Id == id);
            var targetProfile = savedInitial with { DisplayName = "Renamed Anthropic" };
            var updatePlan = new SettingsEditPlan(
                General: new GeneralSettingsPatch(),
                TranslationChanges: new[]
                {
                    new TranslationProfileChange(
                        TranslationProfileChangeKind.Update,
                        BaseProfile: savedInitial,
                        TargetProfile: targetProfile,
                        Credentials: new ProviderCredentialEdit(CredentialEditKind.Keep))
                });

            var updateResult = await coordinator.CommitAsync(updatePlan, TestContext.Current.CancellationToken);
            updateResult.Status.ShouldBe(SettingsEditStatus.Applied);

            // 3. 从实际磁盘文件重新读取，断言 OpenAIOptions 未被删除
            var fileContent = await File.ReadAllTextAsync(new FakeAppPaths(dir.Path).SettingsFile, TestContext.Current.CancellationToken);
            var loaded = JsonSerializer.Deserialize<AppSettings>(fileContent, IrisJson.Options);
            loaded.ShouldNotBeNull();
            var saved = loaded.Translation.Providers.FirstOrDefault(p => p.Id == id);
            saved.ShouldNotBeNull();
            saved.DisplayName.ShouldBe("Renamed Anthropic");
            saved.OpenAIOptions.ShouldNotBeNull();
            saved.OpenAIOptions.ChatCompletionsPath.ShouldBe("/v1/custom/chat");
        }
    }
}
