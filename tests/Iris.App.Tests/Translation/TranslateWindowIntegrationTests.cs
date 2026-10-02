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

    [Fact]
    public async Task RealCompositionRoot_SaveSettings_TriggersRegistry_PresenterTranslates_OrderedCards_SingleRetry_AndShutdown()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var (sp, factory, clipboard, tempDir) = CreateRealCompositionEnvironment();
            using var _ = tempDir;

            var settingsService = sp.GetRequiredService<ISettingsService>();
            var registry = sp.GetRequiredService<ITranslationProviderRegistry>();
            var presenter = sp.GetRequiredService<TranslateWindowPresenter>();

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

            // 启用首个结果自动复制
            await settingsService.UpdateAsync(s => s with
            {
                Translation = s.Translation with { AutoCopy = AutoCopyMode.FirstResult }
            }, TestContext.Current.CancellationToken);

            // 2. 验证设置已持久化，且真实 Registry 已同步刷新拥有这两个服务
            var snapshot = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);
            snapshot.Providers.Count.ShouldBeGreaterThanOrEqualTo(2);
            var enabledProviders = snapshot.Providers.Where(p => p.Profile.Enabled).ToList();
            enabledProviders.Count.ShouldBe(2);

            string firstProviderId = enabledProviders[0].Profile.Id;
            string secondProviderId = enabledProviders[1].Profile.Id;

            // 3. 配置工厂流式输出：分别输出各自的译文片段
            factory.StreamHandler = (profile, req, ct) =>
            {
                static async IAsyncEnumerable<TranslationChunk> Stream(string displayName)
                {
                    yield return new TranslationChunk($"[{displayName} Part 1] ");
                    yield return new TranslationChunk($"[{displayName} Part 2]", FinishReason: TranslationFinishReason.Completed);
                }
                return Stream(profile.DisplayName);
            };

            // 4. 展示器启动翻译：通过真实 TranslationOrchestrator 并发流式输出
            var op = presenter.TryBeginOperation(TestContext.Current.CancellationToken);
            op.ShouldNotBeNull();

            var shown = await presenter.TryShowAsync(op!, new TranslateWindowRequest
            {
                Text = "Hello world",
                AutoTranslate = true
            });
            shown.ShouldBeTrue();
            op!.Dispose();

            var vm = presenter.ViewModel;
            vm.ShouldNotBeNull();

            // 等待翻译完成
            await vm!.WaitForActiveWorkAsync();

            // 5. 验证卡片数量与顺序与服务排序一致
            vm.Cards.Count.ShouldBe(2);
            vm.Cards[0].ProviderId.ShouldBe(firstProviderId);
            vm.Cards[1].ProviderId.ShouldBe(secondProviderId);

            vm.Cards[0].State.ShouldBe(CardState.Completed);
            vm.Cards[1].State.ShouldBe(CardState.Completed);

            vm.Cards[0].Text.ShouldContain($"[{enabledProviders[0].Profile.DisplayName} Part 1]");
            vm.Cards[1].Text.ShouldContain($"[{enabledProviders[1].Profile.DisplayName} Part 1]");

            // 6. 验证首个完整结果触发了自动复制，第二个未重复复制
            await clipboard.Received(1).SetTextAsync(
                Arg.Is<string>(s => s.Contains($"[{enabledProviders[0].Profile.DisplayName} Part 1]")),
                Arg.Any<CancellationToken>());

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
        });
    }

    [Fact]
    public async Task RealCompositionRoot_PartialProviderFailure_DisplaysErrorOnFailingCard_HealthyCardCompletes()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var (sp, factory, _, tempDir) = CreateRealCompositionEnvironment();
            using var _ = tempDir;

            var registry = sp.GetRequiredService<ITranslationProviderRegistry>();
            var presenter = sp.GetRequiredService<TranslateWindowPresenter>();

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

            var snapshot = await registry.AcquireSnapshotAsync(TestContext.Current.CancellationToken);
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

            var op = presenter.TryBeginOperation(TestContext.Current.CancellationToken);
            await presenter.TryShowAsync(op!, new TranslateWindowRequest
            {
                Text = "Hello world",
                AutoTranslate = true
            });
            op!.Dispose();

            var vm = presenter.ViewModel;
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
        });
    }

    [Fact]
    public async Task RealCompositionRoot_AppHostBuilder_ResolvesSameInstance_AndAppBootstrapperStopAsync_DrivesTranslateWindowShutdown()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            using var tempDir = new TempDirectory();
            var paths = new TestAppPaths(tempDir.Path);
            var levelSwitch = new Serilog.Core.LoggingLevelSwitch();
            var clipboard = Substitute.For<IClipboardService>();
            var desktopContext = Substitute.For<IDesktopContext>();
            desktopContext.GetMonitors().Returns([
                new MonitorInfo(@"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 1.0, true)
            ]);
            desktopContext.GetCursorPosition().Returns(new PixelPoint(500, 500));

            var fakeAutoStart = new FakeAutoStartService();
            var memorySecretStore = new MemorySecretStore();
            var testFactory = new TestTranslationProviderFactory();

            var host = AppHostBuilder.Build(
                args: [],
                paths: paths,
                levelSwitch: levelSwitch,
                startTimestamp: System.Diagnostics.Stopwatch.GetTimestamp(),
                configureTestOverrides: services =>
                {
                    var existingFactories = services.Where(d => d.ServiceType == typeof(ITranslationProviderFactory)).ToList();
                    foreach (var d in existingFactories)
                    {
                        services.Remove(d);
                    }
                    services.AddSingleton<ITranslationProviderFactory>(testFactory);

                    var existingSecrets = services.Where(d => d.ServiceType == typeof(ISecretStore)).ToList();
                    foreach (var d in existingSecrets)
                    {
                        services.Remove(d);
                    }
                    services.AddSingleton<ISecretStore>(memorySecretStore);

                    var existingAutoStart = services.Where(d => d.ServiceType == typeof(IAutoStartService)).ToList();
                    foreach (var d in existingAutoStart)
                    {
                        services.Remove(d);
                    }
                    services.AddSingleton<IAutoStartService>(fakeAutoStart);

                    var existingClipboard = services.Where(d => d.ServiceType == typeof(IClipboardService)).ToList();
                    foreach (var d in existingClipboard)
                    {
                        services.Remove(d);
                    }
                    services.AddSingleton(clipboard);

                    var existingDesktop = services.Where(d => d.ServiceType == typeof(IDesktopContext)).ToList();
                    foreach (var d in existingDesktop)
                    {
                        services.Remove(d);
                    }
                    services.AddSingleton(desktopContext);

                    var existingWindow = services.Where(d => d.ServiceType == typeof(TranslateWindow)).ToList();
                    foreach (var d in existingWindow)
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
                });

            var originalShutdown = AppBootstrapper.AppShutdowner;
            AppBootstrapper.AppShutdowner = _ => { };

            try
            {
                using (host)
                {
                    var sp = host.Services;

                    // 1. 验证同实例解析：TranslateWindowPresenter, ITranslateWindowPresenter, ITranslateWindowShutdownCoordinator
                    var presenter = sp.GetRequiredService<TranslateWindowPresenter>();
                    var iPresenter = sp.GetRequiredService<ITranslateWindowPresenter>();
                    var shutdownCoordinator = sp.GetRequiredService<ITranslateWindowShutdownCoordinator>();

                    ReferenceEquals(presenter, iPresenter).ShouldBeTrue();
                    ReferenceEquals(presenter, shutdownCoordinator).ShouldBeTrue();

                    // 2. 初始化设置并展示窗口
                    var settingsService = sp.GetRequiredService<ISettingsService>();
                    await settingsService.LoadAsync();

                    var op = presenter.TryBeginOperation(TestContext.Current.CancellationToken);
                    op.ShouldNotBeNull();

                    var shown = await presenter.TryShowAsync(op!, new TranslateWindowRequest
                    {
                        Text = "Hello HostBuilder",
                        AutoTranslate = false
                    });
                    shown.ShouldBeTrue();
                    op!.Dispose();

                    presenter.Window.ShouldNotBeNull();
                    presenter.ViewModel.ShouldNotBeNull();

                    // 3. 解析 AppBootstrapper 并调用 StopAsync()
                    var bootstrapper = sp.GetRequiredService<AppBootstrapper>();
                    await bootstrapper.StopAsync();

                    // 4. 验证 Bootstrapper.StopAsync 驱动了 ITranslateWindowShutdownCoordinator.ShutdownAsync 收尾
                    presenter.Window.ShouldBeNull();
                    presenter.ViewModel.ShouldBeNull();
                }
            }
            finally
            {
                AppBootstrapper.AppShutdowner = originalShutdown;
            }
        });
    }

    [Fact]
    public async Task AppBootstrapper_StopAsync_WithInFlightConsumerAndCopy_GracefullyShutsDown()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            using var tempDir = new TempDirectory();
            var appPaths = new TestAppPaths(tempDir.Path);
            var secretStore = new MemorySecretStore();

            var orchestrator = Substitute.For<ITranslationOrchestrator>();
            var clipboard = Substitute.For<IClipboardService>();
            var desktopContext = Substitute.For<IDesktopContext>();
            desktopContext.GetMonitors().Returns([
                new MonitorInfo(@"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 1.0, true)
            ]);
            desktopContext.GetCursorPosition().Returns(new PixelPoint(500, 500));

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

            var levelSwitch = new Serilog.Core.LoggingLevelSwitch();
            var host = AppHostBuilder.Build(
                args: [],
                paths: appPaths,
                levelSwitch: levelSwitch,
                startTimestamp: System.Diagnostics.Stopwatch.GetTimestamp(),
                configureTestOverrides: services =>
                {
                    var existingOrch = services.Where(d => d.ServiceType == typeof(ITranslationOrchestrator)).ToList();
                    foreach (var d in existingOrch) services.Remove(d);
                    services.AddSingleton(orchestrator);

                    var existingClipboard = services.Where(d => d.ServiceType == typeof(IClipboardService)).ToList();
                    foreach (var d in existingClipboard) services.Remove(d);
                    services.AddSingleton(clipboard);

                    var existingDesktop = services.Where(d => d.ServiceType == typeof(IDesktopContext)).ToList();
                    foreach (var d in existingDesktop) services.Remove(d);
                    services.AddSingleton(desktopContext);

                    var existingWindow = services.Where(d => d.ServiceType == typeof(TranslateWindow)).ToList();
                    foreach (var d in existingWindow) services.Remove(d);
                    services.AddTransient<TranslateWindow>(sp => new TranslateWindow
                    {
                        ShowActivated = false,
                        ShowInTaskbar = false,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -10000,
                        Top = -10000,
                        Opacity = 0
                    });
                });

            var originalShutdown = AppBootstrapper.AppShutdowner;
            AppBootstrapper.AppShutdowner = _ => { };

            try
            {
                using (host)
                {
                    var sp = host.Services;
                    var settingsService = sp.GetRequiredService<ISettingsService>();
                    await settingsService.LoadAsync();

                    var presenter = sp.GetRequiredService<TranslateWindowPresenter>();
                    var op = presenter.TryBeginOperation(TestContext.Current.CancellationToken);
                    op.ShouldNotBeNull();

                    await presenter.TryShowAsync(op!, new TranslateWindowRequest
                    {
                        Text = "Testing in-flight shutdown",
                        AutoTranslate = true
                    });
                    op!.Dispose();

                    // 启动关闭任务
                    var bootstrapper = sp.GetRequiredService<AppBootstrapper>();
                    var stopTask = bootstrapper.StopAsync();

                    // 放行在途闸门
                    inFlightStreamGate.TrySetResult();
                    inFlightCopyGate.TrySetResult();

                    await stopTask;

                    // 验证窗口与 ViewModel 均已释放
                    presenter.Window.ShouldBeNull();
                    presenter.ViewModel.ShouldBeNull();
                }
            }
            finally
            {
                inFlightStreamGate.TrySetResult();
                inFlightCopyGate.TrySetResult();
                AppBootstrapper.AppShutdowner = originalShutdown;
            }
        });
    }
}
