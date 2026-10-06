using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Iris.App.Tests.TestSupport;
using Iris.Core.Common;
using Iris.Core.DependencyInjection;
using Iris.Core.Presentation;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Iris.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Translation;

public sealed class TranslationOrchestratorIntegrationTests
{
    [Fact]
    public async Task Orchestrator_EndToEnd_CacheAndSqliteHistoryIntegration_MatchesSpecifications()
    {
        await using var fixture = new IntegrationFixture();
        await fixture.InitializeAsync(TestContext.Current.CancellationToken);

        // ---------------------------------------------------------------------------------
        // 1. 一次多服务请求完成，事件、Cache 与 HistorySummary/GetAsync 详情均一致，历史结果按卡片顺序
        // ---------------------------------------------------------------------------------
        var request1 = new TranslationRequest
        {
            RequestId = Guid.NewGuid(),
            Text = "Integration translation test",
            Origin = TextOrigin.Input,
            RecordHistory = true
        };

        var events1 = new List<TranslationEvent>();
        await foreach (var evt in fixture.Orchestrator.TranslateAsync(request1, TestContext.Current.CancellationToken))
        {
            events1.Add(evt);
        }

        // 校验事件流基础结构
        events1.First().ShouldBeOfType<TranslationStarted>();
        events1.Last().ShouldBeOfType<TranslationFinished>();

        var started1 = (TranslationStarted)events1.First();
        started1.Providers.Count.ShouldBe(2);
        started1.Providers[0].Id.ShouldBe("fake-p1");
        started1.Providers[1].Id.ShouldBe("fake-p2");

        var completed1 = events1.OfType<ProviderCompleted>().OrderBy(c => c.ProviderId, StringComparer.Ordinal).ToList();
        completed1.Count.ShouldBe(2);
        completed1[0].ProviderId.ShouldBe("fake-p1");
        completed1[0].FromCache.ShouldBeFalse();
        completed1[0].Text.ShouldBe("Translated by fake-p1: Integration translation test");

        completed1[1].ProviderId.ShouldBe("fake-p2");
        completed1[1].FromCache.ShouldBeFalse();
        completed1[1].Text.ShouldBe("Translated by fake-p2: Integration translation test");

        var finished1 = (TranslationFinished)events1.Last();
        finished1.SucceededCount.ShouldBe(2);
        finished1.FailedCount.ShouldBe(0);

        // 校验真实 SQLite 历史（Summary 与 GetAsync 详情）
        var paged1 = await fixture.History.QueryAsync(new HistoryQuery(), TestContext.Current.CancellationToken);
        paged1.TotalCount.ShouldBe(1);
        paged1.Items.Count.ShouldBe(1);

        var summary1 = paged1.Items[0];
        summary1.SourcePreview.ShouldBe("Integration translation test");
        summary1.ResultCount.ShouldBe(2);
        summary1.HasTruncatedResults.ShouldBeFalse();

        var detail1 = await fixture.History.GetAsync(summary1.Id, TestContext.Current.CancellationToken);
        detail1.ShouldNotBeNull();
        detail1.SourceText.ShouldBe("Integration translation test");
        detail1.Results.Count.ShouldBe(2);

        // 历史结果严格按卡片顺序排列
        detail1.Results[0].ProviderId.ShouldBe("fake-p1");
        detail1.Results[0].ProviderName.ShouldBe("Fake Provider 1");
        detail1.Results[0].Model.ShouldBe("gpt-model-1");
        detail1.Results[0].Text.ShouldBe("Translated by fake-p1: Integration translation test");

        detail1.Results[1].ProviderId.ShouldBe("fake-p2");
        detail1.Results[1].ProviderName.ShouldBe("Fake Provider 2");
        detail1.Results[1].Model.ShouldBe("gpt-model-2");
        detail1.Results[1].Text.ShouldBe("Translated by fake-p2: Integration translation test");

        // ---------------------------------------------------------------------------------
        // 2. 同样请求再次命中缓存，Provider 调用次数不增加，历史按已验收规则去重
        // ---------------------------------------------------------------------------------
        var p1InvocationsAfterStep1 = fixture.ProviderFactory.CreatedProviders["fake-p1"].InvocationCount;
        var p2InvocationsAfterStep1 = fixture.ProviderFactory.CreatedProviders["fake-p2"].InvocationCount;
        p1InvocationsAfterStep1.ShouldBe(1);
        p2InvocationsAfterStep1.ShouldBe(1);

        var request2 = new TranslationRequest
        {
            RequestId = Guid.NewGuid(),
            Text = "Integration translation test",
            Origin = TextOrigin.Input,
            RecordHistory = true
        };

        var events2 = new List<TranslationEvent>();
        await foreach (var evt in fixture.Orchestrator.TranslateAsync(request2, TestContext.Current.CancellationToken))
        {
            events2.Add(evt);
        }

        // Provider 调用次数未增加（直接命中缓存）
        fixture.ProviderFactory.CreatedProviders["fake-p1"].InvocationCount.ShouldBe(p1InvocationsAfterStep1);
        fixture.ProviderFactory.CreatedProviders["fake-p2"].InvocationCount.ShouldBe(p2InvocationsAfterStep1);

        // 两个 Completed 事件均标记为 FromCache
        var completed2 = events2.OfType<ProviderCompleted>().OrderBy(c => c.ProviderId, StringComparer.Ordinal).ToList();
        completed2.Count.ShouldBe(2);
        completed2[0].FromCache.ShouldBeTrue();
        completed2[0].Text.ShouldBe("Translated by fake-p1: Integration translation test");
        completed2[1].FromCache.ShouldBeTrue();
        completed2[1].Text.ShouldBe("Translated by fake-p2: Integration translation test");

        // 历史经已验收规则去重，总条数仍为 1 条
        var paged2 = await fixture.History.QueryAsync(new HistoryQuery(), TestContext.Current.CancellationToken);
        paged2.TotalCount.ShouldBe(1);
        paged2.Items[0].Id.ShouldBe(summary1.Id);

        // ---------------------------------------------------------------------------------
        // 3. 单卡 BypassCache、RecordHistory=false 只调用指定项，不改变历史
        // ---------------------------------------------------------------------------------
        var request3 = new TranslationRequest
        {
            RequestId = Guid.NewGuid(),
            Text = "Integration translation test",
            Origin = TextOrigin.Input,
            ProviderIds = ["fake-p1"],
            BypassCache = true,
            RecordHistory = false
        };

        var events3 = new List<TranslationEvent>();
        await foreach (var evt in fixture.Orchestrator.TranslateAsync(request3, TestContext.Current.CancellationToken))
        {
            events3.Add(evt);
        }

        var started3 = events3.OfType<TranslationStarted>().Single();
        started3.Providers.Count.ShouldBe(1);
        started3.Providers[0].Id.ShouldBe("fake-p1");

        // 仅调用了指定的 fake-p1，fake-p2 未调用
        fixture.ProviderFactory.CreatedProviders["fake-p1"].InvocationCount.ShouldBe(p1InvocationsAfterStep1 + 1);
        fixture.ProviderFactory.CreatedProviders["fake-p2"].InvocationCount.ShouldBe(p2InvocationsAfterStep1);

        var completed3 = events3.OfType<ProviderCompleted>().Single();
        completed3.ProviderId.ShouldBe("fake-p1");
        completed3.FromCache.ShouldBeFalse();
        completed3.Text.ShouldBe("Translated by fake-p1: Integration translation test");

        // 历史条数和内容未被改变，依然保留原始 1 条且包含 2 个结果
        var paged3 = await fixture.History.QueryAsync(new HistoryQuery(), TestContext.Current.CancellationToken);
        paged3.TotalCount.ShouldBe(1);
        paged3.Items[0].Id.ShouldBe(summary1.Id);

        var detail3 = await fixture.History.GetAsync(summary1.Id, TestContext.Current.CancellationToken);
        detail3.ShouldNotBeNull();
        detail3.Results.Count.ShouldBe(2);
        detail3.Results[0].ProviderId.ShouldBe("fake-p1");
        detail3.Results[1].ProviderId.ShouldBe("fake-p2");
    }

    #region Test Fixture and Fakes

    private sealed class IntegrationFixture : IAsyncDisposable
    {
        public TempDirectory TempDir { get; } = new();
        public ServiceProvider ServiceProvider { get; private set; } = null!;
        public InMemorySecretStore SecretStore { get; } = new();
        public IntegrationTestProviderFactory ProviderFactory { get; } = new();

        public ITranslationOrchestrator Orchestrator => ServiceProvider.GetRequiredService<ITranslationOrchestrator>();
        public ITranslationCache Cache => ServiceProvider.GetRequiredService<ITranslationCache>();
        public ITranslationHistoryRepository History => ServiceProvider.GetRequiredService<ITranslationHistoryRepository>();
        public ISettingsService SettingsService => ServiceProvider.GetRequiredService<ISettingsService>();

        public async Task InitializeAsync(CancellationToken ct)
        {
            var paths = new FakeAppPaths(TempDir.Path);
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddSingleton<IAppPaths>(paths);
            services.AddIrisInfrastructure();
            services.AddIrisCore();
            // Translation-only fixture: satisfy OCR's external image dependency during DI validation.
            services.AddSingleton(NSubstitute.Substitute.For<Iris.Core.Imaging.IImagePreprocessor>());

            // 注册测试用 SecretStore 与 ProviderFactory（无 HTTP）
            services.AddSingleton<ISecretStore>(SecretStore);
            services.AddSingleton<ITranslationProviderFactory>(ProviderFactory);
            services.AddSingleton(Substitute.For<ISettingsWindowPresenter>());
            services.AddSingleton(Substitute.For<ITranslateWindowPresenter>());
            services.AddSingleton(Substitute.For<IScreenshotService>());
            services.AddSingleton(Substitute.For<IScreenshotOcrFeedback>());
            services.AddSingleton(Substitute.For<Iris.Core.Platform.IClipboardService>());
            services.AddSingleton(Substitute.For<Iris.Core.Platform.ISelectionTextReader>());
            services.AddSingleton(Substitute.For<Iris.Core.Platform.IDesktopContext>());

            ServiceProvider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });

            // 初始化真实 SQLite 历史库
            var dbInit = ServiceProvider.GetRequiredService<IDatabaseInitializer>();
            await dbInit.InitializeAsync(ct);

            // 加载真实 Settings 并配置两个服务
            await SettingsService.LoadAsync(ct);
            await SettingsService.UpdateAsync(s => s with
            {
                Translation = s.Translation with
                {
                    Providers =
                    [
                        new TranslationProviderProfile
                        {
                            Id = "fake-p1",
                            DisplayName = "Fake Provider 1",
                            Kind = TranslationProviderKind.OpenAICompatible,
                            BaseUrl = "https://fake.local/v1",
                            Model = "gpt-model-1",
                            Order = 1,
                            Enabled = true,
                        },
                        new TranslationProviderProfile
                        {
                            Id = "fake-p2",
                            DisplayName = "Fake Provider 2",
                            Kind = TranslationProviderKind.OpenAICompatible,
                            BaseUrl = "https://fake.local/v1",
                            Model = "gpt-model-2",
                            Order = 2,
                            Enabled = true,
                        }
                    ],
                    CacheEnabled = true,
                },
                History = s.History with
                {
                    Enabled = true,
                }
            }, ct);

            // 设置假凭据
            SecretStore.Set(SecretKeys.ApiKey("fake-p1"), "key-1");
            SecretStore.Set(SecretKeys.ApiKey("fake-p2"), "key-2");
        }

        public async ValueTask DisposeAsync()
        {
            if (ServiceProvider is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else
            {
                ServiceProvider?.Dispose();
            }

            TempDir.Dispose();
        }
    }

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly ConcurrentDictionary<string, string> _secrets = new(StringComparer.Ordinal);

        public event EventHandler<string>? Changed;

        public string? Get(string key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            return _secrets.GetValueOrDefault(key);
        }

        public void Set(string key, string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(value);

            if (value.Length == 0)
            {
                Remove(key);
                return;
            }

            _secrets[key] = value;
            Changed?.Invoke(this, key);
        }

        public bool Remove(string key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            var removed = _secrets.TryRemove(key, out _);
            if (removed)
            {
                Changed?.Invoke(this, key);
            }
            return removed;
        }
    }

    private sealed class IntegrationTestTranslationProvider(ProviderDescriptor descriptor) : ITranslationProvider
    {
        private int _invocationCount;

        public ProviderDescriptor Descriptor { get; } = descriptor;
        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public async IAsyncEnumerable<TranslationChunk> TranslateStreamAsync(
            ProviderTranslateRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _invocationCount);
            await Task.Yield();
            yield return new TranslationChunk($"Translated by {Descriptor.Id}: ");
            yield return new TranslationChunk(request.Text, new TokenUsage(10, 20), TranslationFinishReason.Completed);
        }

        public Task<ProviderTestResult> TestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderTestResult(true, TimeSpan.FromMilliseconds(50), "Test"));
    }

    private sealed class IntegrationTestProviderFactory(TranslationProviderKind kind = TranslationProviderKind.OpenAICompatible) : ITranslationProviderFactory
    {
        public TranslationProviderKind Kind => kind;
        public ConcurrentDictionary<string, IntegrationTestTranslationProvider> CreatedProviders { get; } = new(StringComparer.Ordinal);

        public ITranslationProvider Create(TranslationProviderProfile profile, ProviderCredentials credentials)
        {
            var descriptor = new ProviderDescriptor(
                profile.Id,
                profile.DisplayName,
                Kind.ToString(),
                profile.Model,
                IsConfigured: !string.IsNullOrWhiteSpace(credentials.ApiKey));

            var provider = new IntegrationTestTranslationProvider(descriptor);
            CreatedProviders[profile.Id] = provider;
            return provider;
        }
    }

    #endregion
}
