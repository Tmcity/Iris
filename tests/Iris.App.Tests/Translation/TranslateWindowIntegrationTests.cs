using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.App.Tests.TestSupport;
using Iris.App.Translation;
using Iris.Core.Common;
using Iris.Core.DependencyInjection;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Iris.Core.Presentation;
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

namespace Iris.App.Tests.Translation;

[Collection("AppShutdownerSharedState")]
public sealed class TranslateWindowIntegrationTests
{
    #region Controlled Test Doubles

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "IrisIntegrationTests_" + Guid.NewGuid().ToString("N"));

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

        public event EventHandler<string>? Changed;
    }

    private sealed class FakeAutoStartService : IAutoStartService
    {
        public bool IsEnabled() => false;
        public void SetEnabled(bool enabled) { }
    }

    private sealed class TestTranslationProviderFactory : ITranslationProviderFactory
    {
        public TranslationProviderKind Kind => TranslationProviderKind.OpenAICompatible;

        public Func<TranslationProviderProfile, ProviderTranslateRequest, CancellationToken, IAsyncEnumerable<TranslationChunk>>? StreamHandler { get; set; }

        public ITranslationProvider Create(TranslationProviderProfile profile, ProviderCredentials credentials)
        {
            return new TestTranslationProvider(profile, credentials, this);
        }
    }

    private sealed class TestTranslationProvider : ITranslationProvider, IModelCatalogSource
    {
        private readonly TranslationProviderProfile _profile;
        private readonly ProviderCredentials _credentials;
        private readonly TestTranslationProviderFactory _factory;

        public TestTranslationProvider(
            TranslationProviderProfile profile,
            ProviderCredentials credentials,
            TestTranslationProviderFactory factory)
        {
            _profile = profile;
            _credentials = credentials;
            _factory = factory;
            Descriptor = new ProviderDescriptor(
                profile.Id,
                profile.DisplayName,
                profile.Kind.ToString(),
                profile.Model,
                IsConfigured: !string.IsNullOrWhiteSpace(credentials.ApiKey));
        }

        public ProviderDescriptor Descriptor { get; }

        public async IAsyncEnumerable<TranslationChunk> TranslateStreamAsync(
            ProviderTranslateRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var handler = _factory.StreamHandler;
            if (handler != null)
            {
                await foreach (var chunk in handler(_profile, request, cancellationToken))
                {
                    yield return chunk;
                }
            }
            else
            {
                yield return new TranslationChunk($"[Chunk1 from {_profile.DisplayName}] ");
                yield return new TranslationChunk($"[Chunk2 from {_profile.DisplayName}]", FinishReason: TranslationFinishReason.Completed);
            }
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

    #endregion

    private static (
        IServiceProvider ServiceProvider,
        TestTranslationProviderFactory ProviderFactory,
        IClipboardService Clipboard,
        TempDirectory TempDir)
        CreateRealCompositionEnvironment()
    {
        var tempDir = new TempDirectory();
        var paths = new TestAppPaths(tempDir.Path);
        var secretStore = new MemorySecretStore();
        var autoStart = new FakeAutoStartService();
        var clipboard = Substitute.For<IClipboardService>();
        var desktopContext = Substitute.For<IDesktopContext>();
        desktopContext.GetMonitors().Returns([
            new MonitorInfo(@"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 1.0, true)
        ]);
        desktopContext.GetCursorPosition().Returns(new PixelPoint(500, 500));

        var testFactory = new TestTranslationProviderFactory();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAppPaths>(paths);
        services.AddSingleton<ISecretStore>(secretStore);
        services.AddSingleton<IAutoStartService>(autoStart);
        services.AddSingleton(clipboard);
        services.AddSingleton(desktopContext);
        services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        services.AddSingleton<ThemeService>();

        // 基础设施与核心层服务（真实实现）
        services.AddIrisInfrastructure();
        services.AddIrisCore();
            services.AddSingleton(Substitute.For<ISelectionTextReader>());

        // 注册测试工厂以响应 OpenAICompatible 类型的创建
        services.AddSingleton<ITranslationProviderFactory>(testFactory);

        // 设置窗口相关服务（真实实现）
        services.AddSingleton<ISettingsApplyReceiptManager, SettingsApplyReceiptManager>();
        services.AddSingleton<SettingsWindowPresenter>();
        services.AddSingleton<ISettingsWindowPresenter>(sp => sp.GetRequiredService<SettingsWindowPresenter>());
        services.AddSingleton<ISettingsShutdownCoordinator>(sp => sp.GetRequiredService<SettingsWindowPresenter>());
        services.AddScoped<GeneralSettingsSaveCoordinator>();
        services.AddScoped<SettingsViewModel>();
        services.AddTransient<SettingsWindow>(sp => new SettingsWindow
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            Opacity = 0
        });

        // 翻译窗口相关服务（真实展示器、协调器、VM 与窗体）
        services.AddSingleton<TranslateWindowPresenter>();
        services.AddSingleton<ITranslateWindowPresenter>(sp => sp.GetRequiredService<TranslateWindowPresenter>());
        services.AddSingleton<ITranslateWindowShutdownCoordinator>(sp => sp.GetRequiredService<TranslateWindowPresenter>());
        services.AddScoped<TranslateWindowViewModel>();
        services.AddTransient<TranslateWindow>(sp => new TranslateWindow
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            Opacity = 0
        });

        var rootProvider = services.BuildServiceProvider();

        // 初始化设置服务
        var settingsService = rootProvider.GetRequiredService<ISettingsService>();
        settingsService.LoadAsync().GetAwaiter().GetResult();

        return (rootProvider, testFactory, clipboard, tempDir);
    }

    /// <summary>
    /// 真实组合根的自有清理：先等待自有 Presenter 真实退出，成功后才释放根容器与临时目录。
    /// 调用前必须已放行自有闸门并归还 caller/snapshot 租约（否则退出屏障会持续等待）。
    /// 退出失败会被记录并传播，且不释放仍可能被活跃窗口/工作引用的依赖。
    /// </summary>
    private static async Task CleanupCompositionAsync(
        OwnedCleanup cleanup,
        TranslateWindowPresenter presenter,
        IServiceProvider sp,
        TempDirectory tempDir)
    {
        bool exited = await cleanup.StepAsync(
            "TranslateWindowPresenter.ShutdownAsync",
            () => presenter.ShutdownAsync(CancellationToken.None));
        if (!exited)
        {
            cleanup.Skip("ServiceProvider.DisposeAsync", "自有 Presenter 未完成退出");
            cleanup.Skip("TempDirectory.Dispose", "根容器未释放");
            return;
        }

        bool disposed = await cleanup.StepAsync("ServiceProvider.DisposeAsync", async () =>
        {
            if (sp is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else if (sp is IDisposable disposable)
            {
                disposable.Dispose();
            }
        });
        if (!disposed)
        {
            cleanup.Skip("TempDirectory.Dispose", "根容器释放失败");
            return;
        }

        cleanup.Step("TempDirectory.Dispose", tempDir.Dispose);
    }

    /// <summary>
    /// Host 的自有清理：等待已发起的真实停止任务，或在主体提前失败时由清理发起 AppBootstrapper.StopAsync；
    /// 只有停止成功完成后才 Dispose Host 与临时目录。调用前须已放行自有闸门并归还 caller 租约。
    /// </summary>
    private static async Task CleanupHostAsync(
        OwnedCleanup cleanup,
        Microsoft.Extensions.Hosting.IHost host,
        Task? pendingStopTask,
        TempDirectory tempDir)
    {
        bool stopped = await cleanup.StepAsync("AppBootstrapper.StopAsync", () =>
            pendingStopTask ?? host.Services.GetRequiredService<AppBootstrapper>().StopAsync());
        if (!stopped)
        {
            cleanup.Skip("Host.Dispose", "AppBootstrapper 未完成停止");
            cleanup.Skip("TempDirectory.Dispose", "Host 未释放");
            return;
        }

        if (!cleanup.Step("Host.Dispose", host.Dispose))
        {
            cleanup.Skip("TempDirectory.Dispose", "Host 释放失败");
            return;
        }

        cleanup.Step("TempDirectory.Dispose", tempDir.Dispose);
    }

    private static void CancelEveryClose(object? sender, System.ComponentModel.CancelEventArgs e) => e.Cancel = true;

    private static void ReplaceSingleton<T>(IServiceCollection services, T instance) where T : class
    {
        foreach (var d in services.Where(d => d.ServiceType == typeof(T)).ToList())
        {
            services.Remove(d);
        }
        services.AddSingleton(instance);
    }

    /// <summary>
    /// 构建隔离的真实 Host：Provider 工厂、凭据、开机启动、剪贴板、桌面上下文均替换为测试替身（不触达用户 Run/剪贴板/网络）；
    /// 可选替换编排器。Build 本身失败时释放临时目录后重抛。
    /// </summary>
    private static Microsoft.Extensions.Hosting.IHost BuildTestHost(
        TempDirectory tempDir,
        IClipboardService clipboard,
        ITranslationOrchestrator? orchestrator = null,
        Action<IServiceCollection>? extraOverrides = null)
    {
        try
        {
            var desktopContext = Substitute.For<IDesktopContext>();
            desktopContext.GetMonitors().Returns([
                new MonitorInfo(@"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 1.0, true)
            ]);
            desktopContext.GetCursorPosition().Returns(new PixelPoint(500, 500));

            return AppHostBuilder.Build(
                args: [],
                paths: new TestAppPaths(tempDir.Path),
                levelSwitch: new Serilog.Core.LoggingLevelSwitch(),
                startTimestamp: System.Diagnostics.Stopwatch.GetTimestamp(),
                configureTestOverrides: services =>
                {
                    ReplaceSingleton<ITranslationProviderFactory>(services, new TestTranslationProviderFactory());
                    ReplaceSingleton<ISecretStore>(services, new MemorySecretStore());
                    ReplaceSingleton<IAutoStartService>(services, new FakeAutoStartService());
                    ReplaceSingleton(services, clipboard);
                    ReplaceSingleton(services, desktopContext);
                    if (orchestrator != null)
                    {
                        ReplaceSingleton(services, orchestrator);
                    }

                    foreach (var d in services.Where(d => d.ServiceType == typeof(TranslateWindow)).ToList())
                    {
                        services.Remove(d);
                    }
                    services.AddTransient<TranslateWindow>(sp => new TranslateWindow
                    {
                        ShowActivated = false,
                        ShowInTaskbar = false,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -10000,
                        Top = -10000,
                        Opacity = 0
                    });

                    extraOverrides?.Invoke(services);
                });
        }
        catch
        {
            tempDir.Dispose();
            throw;
        }
    }

    [Fact]
    public async Task RealCompositionRoot_SaveSettings_TriggersRegistry_PresenterTranslates_OrderedCards_SingleRetry_AndShutdown()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var (sp, factory, clipboard, tempDir) = CreateRealCompositionEnvironment();
            var settingsService = sp.GetRequiredService<ISettingsService>();
            var registry = sp.GetRequiredService<ITranslationProviderRegistry>();
            var presenter = sp.GetRequiredService<TranslateWindowPresenter>();

            ITranslationProviderSnapshot? snapshot = null;
            ITranslateWindowOperation? op = null;
            var p0FinishGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var p1HoldGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var autoCopiedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Exception? primary = null;

            try
            {
                // 1. 使用真实 SettingsViewModel 配置并保存两个服务
                using (var settingsScope = sp.CreateScope())
                {
                    var settingsVm = settingsScope.ServiceProvider.GetRequiredService<SettingsViewModel>();

                    var deepseekPreset = SettingsViewModel.AvailablePresets.First(p => p.DisplayName.Contains("DeepSeek", StringComparison.OrdinalIgnoreCase));
                    settingsVm.AddProfileFromPreset(deepseekPreset);
                    var p1Draft = settingsVm.SelectedProfile!;
                    p1Draft.Model = "deepseek-chat";
                    p1Draft.CredentialEditKind = CredentialEditKind.Replace;
                    p1Draft.SetReplacementApiKey("sk-deepseek-test-key");

                    var openaiPreset = SettingsViewModel.AvailablePresets.First(p => p.DisplayName.Contains("OpenAI", StringComparison.OrdinalIgnoreCase));
                    settingsVm.AddProfileFromPreset(openaiPreset);
                    var p2Draft = settingsVm.SelectedProfile!;
                    p2Draft.Model = "gpt-4o";
                    p2Draft.CredentialEditKind = CredentialEditKind.Replace;
                    p2Draft.SetReplacementApiKey("sk-openai-test-key");

                    await settingsVm.ExecuteSaveAsync();
                }

                // 启用首个结果自动复制，并明确隔离失焦隐藏行为（非失焦测试）
                await settingsService.UpdateAsync(s => s with
                {
                    Translation = s.Translation with
                    {
                        AutoCopy = AutoCopyMode.FirstResult
                    },
                    TranslateWindow = s.TranslateWindow with
                    {
                        HideOnDeactivate = false
                    }
                }, TestContext.Current.CancellationToken);

                // 2. 验证设置已持久化，且真实 Registry 已同步刷新拥有这两个服务
                snapshot = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);
                snapshot.Providers.Count.ShouldBeGreaterThanOrEqualTo(2);
                var enabledProviders = snapshot.Providers.Where(p => p.Profile.Enabled).ToList();
                enabledProviders.Count.ShouldBe(2);

                string firstProviderId = enabledProviders[0].Profile.Id;
                string secondProviderId = enabledProviders[1].Profile.Id;

                // 拦截剪贴板自动复制以记录首次复制的内容
                clipboard.SetTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(call =>
                    {
                        string text = call.Arg<string>();
                        autoCopiedTcs.TrySetResult(text);
                        return Task.CompletedTask;
                    });

                // 3. 配置工厂流式输出：受控输出顺序
                // 首个服务产生完整输出触发自动复制；第二个服务在此期间等待闸门，确保完成顺序受控
                factory.StreamHandler = (profile, req, ct) =>
                {
                    if (profile.Id == firstProviderId)
                    {
                        static async IAsyncEnumerable<TranslationChunk> Stream1(string displayName, TaskCompletionSource<bool> finishGate)
                        {
                            yield return new TranslationChunk($"[{displayName} Part 1] ");
                            yield return new TranslationChunk($"[{displayName} Part 2]", FinishReason: TranslationFinishReason.Completed);
                            finishGate.TrySetResult(true);
                        }
                        return Stream1(profile.DisplayName, p0FinishGate);
                    }
                    else
                    {
                        static async IAsyncEnumerable<TranslationChunk> Stream2(
                            string displayName,
                            TaskCompletionSource<bool> holdGate,
                            [EnumeratorCancellation] CancellationToken token)
                        {
                            yield return new TranslationChunk($"[{displayName} Part 1] ");
                            await holdGate.Task.WaitAsync(token);
                            yield return new TranslationChunk($"[{displayName} Part 2]", FinishReason: TranslationFinishReason.Completed);
                        }
                        return Stream2(profile.DisplayName, p1HoldGate, ct);
                    }
                };

                // 4. 展示器启动翻译：通过真实 TranslationOrchestrator 并发流式输出
                op = presenter.TryBeginOperation(TestContext.Current.CancellationToken);
                op.ShouldNotBeNull();

                var shown = await presenter.TryShowAsync(op!, new TranslateWindowRequest
                {
                    Text = "Hello world",
                    AutoTranslate = true
                });
                shown.ShouldBeTrue();
                op.Dispose();
                op = null;

                var vm = presenter.ViewModel;
                vm.ShouldNotBeNull();

                // 等待首个服务率先完成并实际触发自动复制
                await p0FinishGate.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                var copiedText = await autoCopiedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                copiedText.ShouldContain($"[{enabledProviders[0].Profile.DisplayName} Part 1]");
                await clipboard.Received(1).SetTextAsync(
                    Arg.Is<string>(s => s.Contains($"[{enabledProviders[0].Profile.DisplayName} Part 1]")),
                    Arg.Any<CancellationToken>());

                // 5. 放行第二个服务完成
                p1HoldGate.TrySetResult(true);
                await vm!.WaitForActiveWorkAsync();

                // 验证卡片数量与顺序与服务排序一致，且两张卡片均已顺利完成
                vm.Cards.Count.ShouldBe(2);
                vm.Cards[0].ProviderId.ShouldBe(firstProviderId);
                vm.Cards[1].ProviderId.ShouldBe(secondProviderId);

                vm.Cards[0].State.ShouldBe(CardState.Completed);
                vm.Cards[1].State.ShouldBe(CardState.Completed);

                vm.Cards[0].Text.ShouldContain($"[{enabledProviders[0].Profile.DisplayName} Part 1]");
                vm.Cards[1].Text.ShouldContain($"[{enabledProviders[1].Profile.DisplayName} Part 1]");

                // 6. 验证第二个服务完成后未发生重复自动复制（复制调用总数仍为 1）
                await clipboard.Received(1).SetTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

                // 7. 测试单卡重试：仅重试第一张卡片
                factory.StreamHandler = (profile, req, ct) =>
                {
                    static async IAsyncEnumerable<TranslationChunk> Stream(string displayName)
                    {
                        yield return new TranslationChunk($"[Retried {displayName}]", FinishReason: TranslationFinishReason.Completed);
                    }
                    return Stream(profile.DisplayName);
                };

                vm.Cards[0].RetryCommand.Execute(null);
                await vm.WaitForActiveWorkAsync();

                vm.Cards[0].State.ShouldBe(CardState.Completed);
                vm.Cards[0].Text.ShouldBe($"[Retried {enabledProviders[0].Profile.DisplayName}]");

                // 第二张卡片的内容保持不变
                vm.Cards[1].Text.ShouldContain($"[{enabledProviders[1].Profile.DisplayName} Part 1]");

                // 8. 退出收尾：Presenter 正常关闭
                await presenter.ShutdownAsync(TestContext.Current.CancellationToken);
                presenter.Window.ShouldBeNull();
                presenter.ViewModel.ShouldBeNull();
            }
            catch (Exception ex)
            {
                primary = ex;
            }

            var cleanup = new OwnedCleanup();
            cleanup.Step("放行 p1HoldGate", () => p1HoldGate.TrySetResult(true));
            cleanup.Step("归还 caller 租约", () => op?.Dispose());
            cleanup.Step("归还快照租约", () => snapshot?.Dispose());
            await CleanupCompositionAsync(cleanup, presenter, sp, tempDir);
            cleanup.ThrowIfFailed(primary);
        });
    }

    [Fact]
    public async Task RealCompositionRoot_PartialProviderFailure_DisplaysErrorOnFailingCard_HealthyCardCompletes()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var (sp, factory, _, tempDir) = CreateRealCompositionEnvironment();
            var settingsService = sp.GetRequiredService<ISettingsService>();
            var registry = sp.GetRequiredService<ITranslationProviderRegistry>();
            var presenter = sp.GetRequiredService<TranslateWindowPresenter>();

            ITranslationProviderSnapshot? snapshot = null;
            ITranslateWindowOperation? op = null;
            Exception? primary = null;

            try
            {
                // 显式隔离失焦行为
                await settingsService.UpdateAsync(s => s with
                {
                    TranslateWindow = s.TranslateWindow with { HideOnDeactivate = false }
                }, TestContext.Current.CancellationToken);

                // 1. 配置两个服务
                using (var settingsScope = sp.CreateScope())
                {
                    var settingsVm = settingsScope.ServiceProvider.GetRequiredService<SettingsViewModel>();
                    var deepseekPreset = SettingsViewModel.AvailablePresets.First(p => p.DisplayName.Contains("DeepSeek", StringComparison.OrdinalIgnoreCase));
                    settingsVm.AddProfileFromPreset(deepseekPreset);
                    var p1Draft = settingsVm.SelectedProfile!;
                    p1Draft.Model = "deepseek-chat";
                    p1Draft.CredentialEditKind = CredentialEditKind.Replace;
                    p1Draft.SetReplacementApiKey("test-key-1");

                    var openaiPreset = SettingsViewModel.AvailablePresets.First(p => p.DisplayName.Contains("OpenAI", StringComparison.OrdinalIgnoreCase));
                    settingsVm.AddProfileFromPreset(openaiPreset);
                    var p2Draft = settingsVm.SelectedProfile!;
                    p2Draft.Model = "gpt-4o";
                    p2Draft.CredentialEditKind = CredentialEditKind.Replace;
                    p2Draft.SetReplacementApiKey("test-key-2");

                    await settingsVm.ExecuteSaveAsync();
                }

                snapshot = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);
                var enabledProviders = snapshot.Providers.Where(p => p.Profile.Enabled).ToList();
                enabledProviders.Count.ShouldBe(2);

                string failingId = enabledProviders[0].Profile.Id;
                string healthyId = enabledProviders[1].Profile.Id;

                // 2. 第一个服务抛出 IrisException (模拟网络错误)，第二个服务正常返回
                factory.StreamHandler = (profile, req, ct) =>
                {
                    if (profile.Id == failingId)
                    {
                        throw new IrisException(ErrorCode.NetworkUnavailable, "Simulated network failure for provider 1", providerId: profile.Id);
                    }

                    static async IAsyncEnumerable<TranslationChunk> Stream()
                    {
                        yield return new TranslationChunk("Healthy output", FinishReason: TranslationFinishReason.Completed);
                    }
                    return Stream();
                };

                op = presenter.TryBeginOperation(TestContext.Current.CancellationToken);
                op.ShouldNotBeNull();

                await presenter.TryShowAsync(op!, new TranslateWindowRequest
                {
                    Text = "Hello world",
                    AutoTranslate = true
                });
                op.Dispose();
                op = null;

                var vm = presenter.ViewModel;
                vm.ShouldNotBeNull();
                await vm!.WaitForActiveWorkAsync();

                // 3. 验证卡片状态：卡片 1 失败，卡片 2 成功
                vm.Cards.Count.ShouldBe(2);
                vm.Cards[0].ProviderId.ShouldBe(failingId);
                vm.Cards[0].State.ShouldBe(CardState.Failed);
                vm.Cards[0].ErrorMessage.ShouldNotBeNull();

                vm.Cards[1].ProviderId.ShouldBe(healthyId);
                vm.Cards[1].State.ShouldBe(CardState.Completed);
                vm.Cards[1].Text.ShouldBe("Healthy output");

                await presenter.ShutdownAsync(TestContext.Current.CancellationToken);
                presenter.Window.ShouldBeNull();
                presenter.ViewModel.ShouldBeNull();
            }
            catch (Exception ex)
            {
                primary = ex;
            }

            var cleanup = new OwnedCleanup();
            cleanup.Step("归还 caller 租约", () => op?.Dispose());
            cleanup.Step("归还快照租约", () => snapshot?.Dispose());
            await CleanupCompositionAsync(cleanup, presenter, sp, tempDir);
            cleanup.ThrowIfFailed(primary);
        });
    }

    [Fact]
    public async Task RealCompositionRoot_AppHostBuilder_ResolvesSameInstance_AndAppBootstrapperStopAsync_DrivesTranslateWindowShutdown()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var tempDir = new TempDirectory();
            var clipboard = Substitute.For<IClipboardService>();
            var host = BuildTestHost(tempDir, clipboard);

            var originalShutdown = AppBootstrapper.AppShutdowner;
            AppBootstrapper.AppShutdowner = _ => { };

            ITranslateWindowOperation? op = null;
            Task? stopTask = null;
            Exception? primary = null;
            var cleanup = new OwnedCleanup();

            try
            {
                try
                {
                    var sp = host.Services;

                    // 1. 验证同实例解析：TranslateWindowPresenter, ITranslateWindowPresenter, ITranslateWindowShutdownCoordinator
                    var presenter = sp.GetRequiredService<TranslateWindowPresenter>();
                    var iPresenter = sp.GetRequiredService<ITranslateWindowPresenter>();
                    var shutdownCoordinator = sp.GetRequiredService<ITranslateWindowShutdownCoordinator>();

                    ReferenceEquals(presenter, iPresenter).ShouldBeTrue();
                    ReferenceEquals(presenter, shutdownCoordinator).ShouldBeTrue();

                    // 2. 初始化设置并展示窗口（显式隔离失焦隐藏）
                    var settingsService = sp.GetRequiredService<ISettingsService>();
                    await settingsService.LoadAsync();
                    await settingsService.UpdateAsync(s => s with
                    {
                        TranslateWindow = s.TranslateWindow with { HideOnDeactivate = false }
                    }, TestContext.Current.CancellationToken);

                    op = presenter.TryBeginOperation(TestContext.Current.CancellationToken);
                    op.ShouldNotBeNull();

                    var shown = await presenter.TryShowAsync(op!, new TranslateWindowRequest
                    {
                        Text = "Hello HostBuilder",
                        AutoTranslate = false
                    });
                    op.Dispose();
                    op = null;
                    shown.ShouldBeTrue();

                    presenter.Window.ShouldNotBeNull();
                    presenter.ViewModel.ShouldNotBeNull();

                    // 3. 解析 AppBootstrapper 并调用 StopAsync()
                    var bootstrapper = sp.GetRequiredService<AppBootstrapper>();
                    stopTask = bootstrapper.StopAsync();
                    await stopTask;

                    // 4. 验证 Bootstrapper.StopAsync 驱动了 ITranslateWindowShutdownCoordinator.ShutdownAsync 收尾
                    presenter.Window.ShouldBeNull();
                    presenter.ViewModel.ShouldBeNull();
                }
                catch (Exception ex)
                {
                    primary = ex;
                }

                // 任何分支：先归还租约，再等待/发起真实停止，停止成功后才释放 Host
                cleanup.Step("归还 caller 租约", () => op?.Dispose());
                await CleanupHostAsync(cleanup, host, stopTask, tempDir);
            }
            finally
            {
                AppBootstrapper.AppShutdowner = originalShutdown;
            }

            cleanup.ThrowIfFailed(primary);
        });
    }

    [Fact]
    public async Task AppBootstrapper_StopAsync_WithInFlightConsumerAndCopy_GracefullyShutsDown()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var tempDir = new TempDirectory();
            var orchestrator = Substitute.For<ITranslationOrchestrator>();
            var clipboard = Substitute.For<IClipboardService>();

            var inFlightStreamGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var inFlightCopyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            async IAsyncEnumerable<TranslationEvent> HangingTranslationStream()
            {
                var p1 = new ProviderDescriptor("p1", "P1", "Kind", null, true);
                yield return new TranslationStarted(Guid.NewGuid(), new Iris.Core.Languages.ResolvedLanguagePair("en", "zh-Hans", false), [p1]);
                yield return new ProviderStarted(Guid.NewGuid(), "p1");
                await inFlightStreamGate.Task;
                yield return new ProviderCompleted(Guid.NewGuid(), "p1", "Done", TimeSpan.Zero, false, false, null);
            }

            orchestrator.TranslateAsync(Arg.Any<TranslationRequest>(), Arg.Any<CancellationToken>())
                .Returns(HangingTranslationStream());

            clipboard.SetTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    await inFlightCopyGate.Task;
                });

            var host = BuildTestHost(tempDir, clipboard, orchestrator);

            var originalShutdown = AppBootstrapper.AppShutdowner;
            AppBootstrapper.AppShutdowner = _ => { };

            ITranslateWindowOperation? op = null;
            Task? stopTask = null;
            Exception? primary = null;
            var cleanup = new OwnedCleanup();

            try
            {
                try
                {
                    var sp = host.Services;
                    var settingsService = sp.GetRequiredService<ISettingsService>();
                    await settingsService.LoadAsync();
                    await settingsService.UpdateAsync(s => s with
                    {
                        TranslateWindow = s.TranslateWindow with { HideOnDeactivate = false }
                    }, TestContext.Current.CancellationToken);

                    var presenter = sp.GetRequiredService<TranslateWindowPresenter>();
                    op = presenter.TryBeginOperation(TestContext.Current.CancellationToken);
                    op.ShouldNotBeNull();

                    await presenter.TryShowAsync(op!, new TranslateWindowRequest
                    {
                        Text = "Testing in-flight shutdown",
                        AutoTranslate = true
                    });
                    op.Dispose();
                    op = null;

                    // 启动关闭任务
                    var bootstrapper = sp.GetRequiredService<AppBootstrapper>();
                    stopTask = bootstrapper.StopAsync();

                    // 放行在途闸门
                    inFlightStreamGate.TrySetResult();
                    inFlightCopyGate.TrySetResult();

                    await stopTask;

                    // 验证窗口与 ViewModel 均已释放
                    presenter.Window.ShouldBeNull();
                    presenter.ViewModel.ShouldBeNull();
                }
                catch (Exception ex)
                {
                    primary = ex;
                }

                // 任何分支：先放行自有闸门与租约，再等待已发起的停止（或由清理发起），停止成功后才释放 Host
                cleanup.Step("放行在途流/复制闸门", () =>
                {
                    inFlightStreamGate.TrySetResult();
                    inFlightCopyGate.TrySetResult();
                });
                cleanup.Step("归还 caller 租约", () => op?.Dispose());
                await CleanupHostAsync(cleanup, host, stopTask, tempDir);
            }
            finally
            {
                AppBootstrapper.AppShutdowner = originalShutdown;
            }

            cleanup.ThrowIfFailed(primary);
        });
    }

    private sealed class DisposalOrderProbe(Func<string> captureState) : IDisposable
    {
        public string? StateAtDispose { get; private set; }

        public void Dispose() => StateAtDispose ??= captureState();
    }

    [Fact]
    public async Task HostCleanup_EarlyFailureBeforeStopAsync_CleanupStartsRealStop_AndDisposesHostOnlyAfterWorkExited()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var tempDir = new TempDirectory();
            var orchestrator = Substitute.For<ITranslationOrchestrator>();
            var clipboard = Substitute.For<IClipboardService>();

            var streamEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var streamGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int workExited = 0;

            async IAsyncEnumerable<TranslationEvent> PendingStream([EnumeratorCancellation] CancellationToken token)
            {
                try
                {
                    var p1 = new ProviderDescriptor("p1", "P1", "Kind", null, true);
                    yield return new TranslationStarted(Guid.NewGuid(), new Iris.Core.Languages.ResolvedLanguagePair("en", "zh-Hans", false), [p1]);
                    yield return new ProviderStarted(Guid.NewGuid(), "p1");
                    streamEntered.TrySetResult();
                    // 工作保持挂起，直到真实停止取消该操作（最外层另有安全放行）
                    await streamGate.Task.WaitAsync(token);
                    yield return new ProviderCompleted(Guid.NewGuid(), "p1", "Done", TimeSpan.Zero, false, false, null);
                }
                finally
                {
                    Interlocked.Exchange(ref workExited, 1);
                }
            }

            orchestrator.TranslateAsync(Arg.Any<TranslationRequest>(), Arg.Any<CancellationToken>())
                .Returns(call => PendingStream(call.Arg<CancellationToken>()));

            var shutdownCodes = new List<int>();
            TranslateWindowPresenter? presenter = null;

            // 容器工厂创建的探针由 Host.Dispose 释放，记录释放瞬间的工作/停止状态
            var probe = new DisposalOrderProbe(() =>
                $"workExited={Volatile.Read(ref workExited) == 1} " +
                $"viewModelReleased={presenter?.ViewModel == null} " +
                $"windowReleased={presenter?.Window == null} " +
                $"stopCompleted={shutdownCodes.Contains(0)}");

            var host = BuildTestHost(tempDir, clipboard, orchestrator, services => services.AddSingleton(_ => probe));

            var originalShutdown = AppBootstrapper.AppShutdowner;
            AppBootstrapper.AppShutdowner = code => shutdownCodes.Add(code);

            ITranslateWindowOperation? op = null;
            Exception? primary = null;
            var cleanup = new OwnedCleanup();

            try
            {
                try
                {
                    var sp = host.Services;
                    sp.GetRequiredService<DisposalOrderProbe>().ShouldBeSameAs(probe);
                    presenter = sp.GetRequiredService<TranslateWindowPresenter>();

                    var settingsService = sp.GetRequiredService<ISettingsService>();
                    await settingsService.LoadAsync();
                    await settingsService.UpdateAsync(s => s with
                    {
                        TranslateWindow = s.TranslateWindow with { HideOnDeactivate = false }
                    }, TestContext.Current.CancellationToken);

                    op = presenter.TryBeginOperation(TestContext.Current.CancellationToken);
                    op.ShouldNotBeNull();
                    var shown = await presenter.TryShowAsync(op!, new TranslateWindowRequest
                    {
                        Text = "Early failure before StopAsync",
                        AutoTranslate = true
                    });
                    op.Dispose();
                    op = null;
                    shown.ShouldBeTrue();

                    await streamEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                    Volatile.Read(ref workExited).ShouldBe(0, "失败前工作必须仍在挂起");

                    // 在正常 StopAsync 行之前故意失败
                    throw new InvalidOperationException("SIMULATED_EARLY_FAILURE_BEFORE_STOP");
                }
                catch (Exception ex)
                {
                    primary = ex;
                }

                // 与正式用例相同的真实清理路径：未发起过停止，由清理发起 AppBootstrapper.StopAsync
                cleanup.Step("归还 caller 租约", () => op?.Dispose());
                await CleanupHostAsync(cleanup, host, pendingStopTask: null, tempDir);
            }
            finally
            {
                streamGate.TrySetResult();
                AppBootstrapper.AppShutdowner = originalShutdown;
            }

            // 只有主体失败：原样传播主体异常，且清理无错误、无跳过
            var thrown = Should.Throw<InvalidOperationException>(() => cleanup.ThrowIfFailed(primary));
            thrown.Message.ShouldBe("SIMULATED_EARLY_FAILURE_BEFORE_STOP");
            cleanup.Errors.ShouldBeEmpty();
            cleanup.Skipped.ShouldBeEmpty();

            shutdownCodes.ShouldBe([0], "主体未到达 StopAsync 行，清理必须自行发起并完成真实停止");
            presenter.ShouldNotBeNull();
            presenter!.Window.ShouldBeNull();
            probe.StateAtDispose.ShouldBe("workExited=True viewModelReleased=True windowReleased=True stopCompleted=True");
            Directory.Exists(tempDir.Path).ShouldBeFalse();
        });
    }

    [Fact]
    public async Task HostCleanup_StopFailsBecauseOwnedWindowRefusesClose_IsReported_HostKept_ThenRecoveredByRetry()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var tempDir = new TempDirectory();
            var clipboard = Substitute.For<IClipboardService>();
            var host = BuildTestHost(tempDir, clipboard);

            var shutdownCodes = new List<int>();
            var originalShutdown = AppBootstrapper.AppShutdowner;
            AppBootstrapper.AppShutdowner = code => shutdownCodes.Add(code);

            TranslateWindowPresenter? presenter = null;
            TranslateWindow? ownedWindow = null;
            ITranslateWindowOperation? op = null;
            Exception? primary = null;
            var recovery = new OwnedCleanup();

            try
            {
                try
                {
                    var sp = host.Services;
                    presenter = sp.GetRequiredService<TranslateWindowPresenter>();
                    var settingsService = sp.GetRequiredService<ISettingsService>();
                    await settingsService.LoadAsync();
                    await settingsService.UpdateAsync(s => s with
                    {
                        TranslateWindow = s.TranslateWindow with { HideOnDeactivate = false }
                    }, TestContext.Current.CancellationToken);

                    op = presenter.TryBeginOperation(TestContext.Current.CancellationToken);
                    op.ShouldNotBeNull();
                    var shown = await presenter.TryShowAsync(op!, new TranslateWindowRequest
                    {
                        Text = "Owned window refuses close",
                        AutoTranslate = false
                    });
                    op.Dispose();
                    op = null;
                    shown.ShouldBeTrue();

                    // 仅让本测试自己创建的窗口拒绝关闭
                    ownedWindow = presenter.Window;
                    ownedWindow.ShouldNotBeNull();
                    ownedWindow!.Closing += CancelEveryClose;

                    // 走正式用例使用的同一清理路径：真实 AppBootstrapper.StopAsync → Presenter.ShutdownAsync 遇到关闭失败
                    var faulted = new OwnedCleanup();
                    await CleanupHostAsync(faulted, host, pendingStopTask: null, tempDir);

                    faulted.Errors.Count.ShouldBe(1);
                    var step = faulted.Errors[0].ShouldBeOfType<OwnedCleanupStepException>();
                    step.StepName.ShouldBe("AppBootstrapper.StopAsync");
                    step.InnerException.ShouldBeOfType<InvalidOperationException>();
                    faulted.Skipped.ShouldContain(s => s.StartsWith("Host.Dispose", StringComparison.Ordinal));
                    faulted.Skipped.ShouldContain(s => s.StartsWith("TempDirectory.Dispose", StringComparison.Ordinal));

                    // 仅清理失败也必须使测试失败，不能当作清理完成
                    var onlyCleanup = Should.Throw<AggregateException>(() => faulted.ThrowIfFailed(null));
                    onlyCleanup.InnerExceptions.ShouldHaveSingleItem().ShouldBeSameAs(step);

                    // 停止未完成：窗口仍存活，Host 未被释放，未发出退出码
                    ownedWindow.IsVisible.ShouldBeTrue();
                    presenter.Window.ShouldBeSameAs(ownedWindow);
                    Should.NotThrow(() => host.Services.GetRequiredService<ISettingsService>());
                    Directory.Exists(tempDir.Path).ShouldBeTrue();
                    shutdownCodes.ShouldBeEmpty();
                }
                catch (Exception ex)
                {
                    primary = ex;
                }
            }
            finally
            {
                // 最外层：移除拒绝处理器后重试真实停止，完整完成自身清理，再恢复共享退出委托
                try
                {
                    if (ownedWindow != null)
                    {
                        ownedWindow.Closing -= CancelEveryClose;
                    }
                    recovery.Step("归还 caller 租约", () => op?.Dispose());
                    await CleanupHostAsync(recovery, host, pendingStopTask: null, tempDir);
                }
                finally
                {
                    AppBootstrapper.AppShutdowner = originalShutdown;
                }
            }

            recovery.ThrowIfFailed(primary);

            shutdownCodes.ShouldBe([0]);
            presenter.ShouldNotBeNull();
            presenter!.Window.ShouldBeNull();
            ownedWindow!.IsVisible.ShouldBeFalse();
            Directory.Exists(tempDir.Path).ShouldBeFalse();
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Presenter_WindowDeactivation_BranchComparison_TrueCancelsAndHides_FalsePreservesAndCompletes(bool hideOnDeactivate)
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var (sp, factory, _, tempDir) = CreateRealCompositionEnvironment();
            var settingsService = sp.GetRequiredService<ISettingsService>();
            var registry = sp.GetRequiredService<ITranslationProviderRegistry>();
            var presenter = sp.GetRequiredService<TranslateWindowPresenter>();

            ITranslationProviderSnapshot? snapshot = null;
            ITranslateWindowOperation? op = null;
            var p0Completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var p1StreamingStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var p1CompleteGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Exception? primary = null;

            try
            {
                // 1. 配置两个服务与 HideOnDeactivate 开关
                using (var settingsScope = sp.CreateScope())
                {
                    var settingsVm = settingsScope.ServiceProvider.GetRequiredService<SettingsViewModel>();

                    var deepseekPreset = SettingsViewModel.AvailablePresets.First(p => p.DisplayName.Contains("DeepSeek", StringComparison.OrdinalIgnoreCase));
                    settingsVm.AddProfileFromPreset(deepseekPreset);
                    var p1Draft = settingsVm.SelectedProfile!;
                    p1Draft.Model = "deepseek-chat";
                    p1Draft.CredentialEditKind = CredentialEditKind.Replace;
                    p1Draft.SetReplacementApiKey("test-key-p1");

                    var openaiPreset = SettingsViewModel.AvailablePresets.First(p => p.DisplayName.Contains("OpenAI", StringComparison.OrdinalIgnoreCase));
                    settingsVm.AddProfileFromPreset(openaiPreset);
                    var p2Draft = settingsVm.SelectedProfile!;
                    p2Draft.Model = "gpt-4o";
                    p2Draft.CredentialEditKind = CredentialEditKind.Replace;
                    p2Draft.SetReplacementApiKey("test-key-p2");

                    await settingsVm.ExecuteSaveAsync();
                }

                await settingsService.UpdateAsync(s => s with
                {
                    TranslateWindow = s.TranslateWindow with { HideOnDeactivate = hideOnDeactivate }
                }, TestContext.Current.CancellationToken);

                snapshot = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);
                var enabledProviders = snapshot.Providers.Where(p => p.Profile.Enabled).ToList();
                enabledProviders.Count.ShouldBe(2);

                string firstId = enabledProviders[0].Profile.Id;
                string secondId = enabledProviders[1].Profile.Id;

                // 2. 配置流式输出：首卡立刻完成；次卡在 streaming 时被闸门阻塞
                factory.StreamHandler = (profile, req, ct) =>
                {
                    if (profile.Id == firstId)
                    {
                        static async IAsyncEnumerable<TranslationChunk> Stream1(TaskCompletionSource<bool> tcs)
                        {
                            yield return new TranslationChunk("[P0 First Chunk]");
                            yield return new TranslationChunk("[P0 Completed]", FinishReason: TranslationFinishReason.Completed);
                            tcs.TrySetResult(true);
                        }
                        return Stream1(p0Completed);
                    }
                    else
                    {
                        static async IAsyncEnumerable<TranslationChunk> Stream2(
                            TaskCompletionSource<bool> startedTcs,
                            TaskCompletionSource<bool> gate,
                            [EnumeratorCancellation] CancellationToken token)
                        {
                            yield return new TranslationChunk("[P1 Streaming Part 1] ");
                            startedTcs.TrySetResult(true);
                            await gate.Task.WaitAsync(token);
                            yield return new TranslationChunk("[P1 Completed Part 2]", FinishReason: TranslationFinishReason.Completed);
                        }
                        return Stream2(p1StreamingStarted, p1CompleteGate, ct);
                    }
                };

                op = presenter.TryBeginOperation(TestContext.Current.CancellationToken);
                op.ShouldNotBeNull();

                var shown = await presenter.TryShowAsync(op!, new TranslateWindowRequest
                {
                    Text = "Test deactivation",
                    AutoTranslate = true
                });
                shown.ShouldBeTrue();
                op.Dispose();
                op = null;

                var vm = presenter.ViewModel;
                vm.ShouldNotBeNull();

                // 等待首卡完成，次卡处于流式传输中
                await p0Completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                await p1StreamingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

                for (var i = 0; i < 50 && (vm.Cards.Count < 2 || vm.Cards[0].State != CardState.Completed || vm.Cards[1].State != CardState.Streaming); i++)
                {
                    await Task.Delay(20);
                }

                vm.Cards[0].State.ShouldBe(CardState.Completed);
                vm.Cards[1].State.ShouldBe(CardState.Streaming);

                // 3. 触发失焦处理（通过代码调用受控入口，不使用物理输入）
                await presenter.OnWindowDeactivated();

                if (hideOnDeactivate)
                {
                    // true 分支：窗口应当隐藏，次卡应当被取消
                    presenter.Window.ShouldNotBeNull();
                    presenter.Window!.IsVisible.ShouldBeFalse("HideOnDeactivate=true 时窗口应当隐藏");
                    p1CompleteGate.TrySetResult(true);
                    await vm.WaitForActiveWorkAsync();

                    vm.Cards[1].State.ShouldBe(CardState.Canceled, "HideOnDeactivate=true 时进行中的次卡应当被取消");
                }
                else
                {
                    // false 分支：窗口应当保持显示，放行次卡闸门后次卡应当正常完成
                    presenter.Window.ShouldNotBeNull();
                    presenter.Window!.IsVisible.ShouldBeTrue("HideOnDeactivate=false 时窗口应当保持显示");
                    p1CompleteGate.TrySetResult(true);
                    await vm.WaitForActiveWorkAsync();

                    vm.Cards[1].State.ShouldBe(CardState.Completed, "HideOnDeactivate=false 时次卡应当正常完成");
                }
            }
            catch (Exception ex)
            {
                primary = ex;
            }

            var cleanup = new OwnedCleanup();
            cleanup.Step("放行 p1CompleteGate", () => p1CompleteGate.TrySetResult(true));
            cleanup.Step("归还 caller 租约", () => op?.Dispose());
            cleanup.Step("归还快照租约", () => snapshot?.Dispose());
            await CleanupCompositionAsync(cleanup, presenter, sp, tempDir);
            cleanup.ThrowIfFailed(primary);
        });
    }

    [Fact]
    public async Task RealCompositionRoot_MidWayFailure_CleanlyTearsDownResources_WithoutAffectingOnlookerWindow()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            // 1. 创建共享 STA 上的独立旁观窗口（本测试自有）
            var onlookerWindow = new Wpf.Ui.Controls.FluentWindow
            {
                Title = "TranslateWindowIntegration_OnlookerWindow",
                Width = 100,
                Height = 100,
                ShowActivated = false
            };

            try
            {
                onlookerWindow.Show();
                onlookerWindow.IsVisible.ShouldBeTrue("旁观窗口应当已正常显示");

                var (sp, factory, _, tempDir) = CreateRealCompositionEnvironment();
                var settingsService = sp.GetRequiredService<ISettingsService>();
                var registry = sp.GetRequiredService<ITranslationProviderRegistry>();
                var presenter = sp.GetRequiredService<TranslateWindowPresenter>();

                ITranslationProviderSnapshot? snapshot = null;
                ITranslateWindowOperation? op = null;
                var streamEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var streamHoldGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                int workExited = 0;
                Exception? primary = null;

                try
                {
                    await settingsService.UpdateAsync(s => s with
                    {
                        TranslateWindow = s.TranslateWindow with { HideOnDeactivate = false }
                    }, TestContext.Current.CancellationToken);

                    using (var settingsScope = sp.CreateScope())
                    {
                        var settingsVm = settingsScope.ServiceProvider.GetRequiredService<SettingsViewModel>();
                        var deepseekPreset = SettingsViewModel.AvailablePresets.First(p => p.DisplayName.Contains("DeepSeek", StringComparison.OrdinalIgnoreCase));
                        settingsVm.AddProfileFromPreset(deepseekPreset);
                        var p1Draft = settingsVm.SelectedProfile!;
                        p1Draft.Model = "deepseek-chat";
                        p1Draft.CredentialEditKind = CredentialEditKind.Replace;
                        p1Draft.SetReplacementApiKey("test-key-p1");
                        await settingsVm.ExecuteSaveAsync();
                    }

                    snapshot = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);

                    factory.StreamHandler = (profile, req, ct) =>
                    {
                        async IAsyncEnumerable<TranslationChunk> Stream([EnumeratorCancellation] CancellationToken token = default)
                        {
                            try
                            {
                                yield return new TranslationChunk("[Part 1]");
                                streamEntered.TrySetResult(true);
                                await streamHoldGate.Task.WaitAsync(token);
                                yield return new TranslationChunk("[Part 2]", FinishReason: TranslationFinishReason.Completed);
                            }
                            finally
                            {
                                Interlocked.Exchange(ref workExited, 1);
                            }
                        }
                        return Stream(ct);
                    };

                    op = presenter.TryBeginOperation(TestContext.Current.CancellationToken);
                    op.ShouldNotBeNull();

                    var shown = await presenter.TryShowAsync(op!, new TranslateWindowRequest
                    {
                        Text = "Mid-way failure test",
                        AutoTranslate = true
                    });
                    shown.ShouldBeTrue();

                    // 确认流式工作已进入且窗口已显示；此时故意保留 caller 租约不归还
                    await streamEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                    presenter.Window.ShouldNotBeNull();
                    presenter.Window!.IsVisible.ShouldBeTrue();
                    Volatile.Read(ref workExited).ShouldBe(0);

                    // 故意模拟中途失败
                    throw new InvalidOperationException("SIMULATED_MIDWAY_FAILURE");
                }
                catch (Exception ex)
                {
                    primary = ex;
                }

                // 与正式用例相同的真实清理路径
                var cleanup = new OwnedCleanup();
                cleanup.Step("放行 streamHoldGate", () => streamHoldGate.TrySetResult(true));
                cleanup.Step("归还 caller 租约", () => op?.Dispose());
                cleanup.Step("归还快照租约", () => snapshot?.Dispose());
                await CleanupCompositionAsync(cleanup, presenter, sp, tempDir);

                // 只有主体失败：原样传播主体异常；清理本身无错误、无跳过
                var thrown = Should.Throw<InvalidOperationException>(() => cleanup.ThrowIfFailed(primary));
                thrown.Message.ShouldBe("SIMULATED_MIDWAY_FAILURE");
                cleanup.Errors.ShouldBeEmpty();
                cleanup.Skipped.ShouldBeEmpty();

                // 自有工作、租约、窗口均已退出；旁观窗口未被关闭
                Volatile.Read(ref workExited).ShouldBe(1, "自有流式工作必须已退出");
                presenter.ActiveOperationsCount.ShouldBe(0);
                presenter.Window.ShouldBeNull("自身窗口必须已完成关闭并置空");
                presenter.ViewModel.ShouldBeNull("自身 ViewModel 必须已完成释放并置空");
                Directory.Exists(tempDir.Path).ShouldBeFalse();
                onlookerWindow.IsVisible.ShouldBeTrue("自有清理绝不得关闭共享 STA 上的其它测试窗口");
            }
            finally
            {
                onlookerWindow.Close();
            }
        });
    }

    [Fact]
    public async Task CompositionCleanup_OwnedWindowRefusesClose_FailureIsPropagated_ContainerKept_ThenRetriedAndOnlookerSurvives()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var onlookerWindow = new Wpf.Ui.Controls.FluentWindow
            {
                Title = "TranslateWindowIntegration_CloseFault_Onlooker",
                Width = 100,
                Height = 100,
                ShowActivated = false
            };

            try
            {
                onlookerWindow.Show();

                var (sp, _, _, tempDir) = CreateRealCompositionEnvironment();
                var settingsService = sp.GetRequiredService<ISettingsService>();
                var presenter = sp.GetRequiredService<TranslateWindowPresenter>();

                TranslateWindow? ownedWindow = null;
                ITranslateWindowOperation? op = null;
                Exception? primary = null;
                var recovery = new OwnedCleanup();

                try
                {
                    try
                    {
                        await settingsService.UpdateAsync(s => s with
                        {
                            TranslateWindow = s.TranslateWindow with { HideOnDeactivate = false }
                        }, TestContext.Current.CancellationToken);

                        op = presenter.TryBeginOperation(TestContext.Current.CancellationToken);
                        op.ShouldNotBeNull();
                        var shown = await presenter.TryShowAsync(op!, new TranslateWindowRequest
                        {
                            Text = "Owned window refuses close",
                            AutoTranslate = false
                        });
                        op.Dispose();
                        op = null;
                        shown.ShouldBeTrue();

                        // 仅让本测试自己创建的翻译窗口拒绝关闭
                        ownedWindow = presenter.Window;
                        ownedWindow.ShouldNotBeNull();
                        ownedWindow!.Closing += CancelEveryClose;

                        // 走正式用例使用的同一清理路径：真实 Presenter.ShutdownAsync 遇到关闭失败
                        var faulted = new OwnedCleanup();
                        await CleanupCompositionAsync(faulted, presenter, sp, tempDir);

                        faulted.Errors.Count.ShouldBe(1);
                        var step = faulted.Errors[0].ShouldBeOfType<OwnedCleanupStepException>();
                        step.StepName.ShouldBe("TranslateWindowPresenter.ShutdownAsync");
                        step.InnerException.ShouldBeOfType<InvalidOperationException>();
                        faulted.Skipped.ShouldContain(s => s.StartsWith("ServiceProvider.DisposeAsync", StringComparison.Ordinal));
                        faulted.Skipped.ShouldContain(s => s.StartsWith("TempDirectory.Dispose", StringComparison.Ordinal));

                        // 仅清理失败：必须失败并保留清理错误
                        var onlyCleanup = Should.Throw<AggregateException>(() => faulted.ThrowIfFailed(null));
                        onlyCleanup.InnerExceptions.ShouldHaveSingleItem().ShouldBeSameAs(step);

                        // 主体与清理都失败：两者都可观察（主体在前）
                        var bodyFailure = new InvalidOperationException("SIMULATED_BODY_FAILURE");
                        var both = Should.Throw<AggregateException>(() => faulted.ThrowIfFailed(bodyFailure));
                        both.InnerExceptions.Count.ShouldBe(2);
                        both.InnerExceptions[0].ShouldBeSameAs(bodyFailure);
                        both.InnerExceptions[1].ShouldBeSameAs(step);

                        // 关闭失败不是清理完成：窗口仍存活，容器与临时目录未被释放
                        ownedWindow.IsVisible.ShouldBeTrue();
                        presenter.Window.ShouldBeSameAs(ownedWindow);
                        Should.NotThrow(() => sp.GetRequiredService<ISettingsService>());
                        Directory.Exists(tempDir.Path).ShouldBeTrue();
                    }
                    catch (Exception ex)
                    {
                        primary = ex;
                    }
                }
                finally
                {
                    // 最外层：移除拒绝处理器，重试并完整完成自身清理
                    if (ownedWindow != null)
                    {
                        ownedWindow.Closing -= CancelEveryClose;
                    }
                    recovery.Step("归还 caller 租约", () => op?.Dispose());
                    await CleanupCompositionAsync(recovery, presenter, sp, tempDir);
                }

                recovery.ThrowIfFailed(primary);

                presenter.Window.ShouldBeNull();
                presenter.ViewModel.ShouldBeNull();
                ownedWindow!.IsVisible.ShouldBeFalse();
                Directory.Exists(tempDir.Path).ShouldBeFalse();
                onlookerWindow.IsVisible.ShouldBeTrue("自有清理与重试不得关闭旁观窗口");
            }
            finally
            {
                onlookerWindow.Close();
            }
        });
    }
}
