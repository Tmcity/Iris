using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.App.Tests.TestSupport;
using Iris.App.Translation;
using Iris.Core.Actions;
using Iris.Core.Common;
using Iris.Core.DependencyInjection;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Iris.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Workflows;

public sealed class TranslationEndToEndTests
{
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "IrisE2E_" + Guid.NewGuid().ToString("N"));

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

    private sealed class FakeAppPaths(string dataDirectory) : IAppPaths
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
        private readonly ConcurrentDictionary<string, string> _store = new(StringComparer.Ordinal);

        public string? Get(string key)
        {
            _store.TryGetValue(key, out var val);
            return val;
        }

        public void Set(string key, string value)
        {
            _store[key] = value;
            Changed?.Invoke(this, key);
        }

        public bool Remove(string key)
        {
            var res = _store.TryRemove(key, out _);
            if (res)
            {
                Changed?.Invoke(this, key);
            }
            return res;
        }

        public event EventHandler<string>? Changed;
    }

    private sealed class FakeTranslationProvider : ITranslationProvider
    {
        public ProviderDescriptor Descriptor { get; }
        public TranslationProviderProfile Profile { get; }

        public FakeTranslationProvider(TranslationProviderProfile profile, ProviderCredentials credentials)
        {
            Profile = profile;
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
            yield return new TranslationChunk($"[{Profile.DisplayName}] {request.Text}", FinishReason: TranslationFinishReason.Completed);
        }

        public Task<ProviderTestResult> TestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderTestResult(true, TimeSpan.FromMilliseconds(20), "OK"));
    }

    private sealed class FakeTranslationProviderFactory : ITranslationProviderFactory
    {
        public TranslationProviderKind Kind => TranslationProviderKind.OpenAICompatible;

        public ITranslationProvider Create(TranslationProviderProfile profile, ProviderCredentials credentials)
        {
            return new FakeTranslationProvider(profile, credentials);
        }
    }

    [Fact]
    public async Task SettingsSaveToTranslationExecution_EndToEnd_FlowWorksAndRetryRemainsAvailable()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            using var tempDir = new TempDirectory();
            var paths = new FakeAppPaths(tempDir.Path);
            var secretStore = new MemorySecretStore();
            var autoStartService = Substitute.For<IAutoStartService>();
            var uiDispatcher = new WpfUiDispatcher();
            var clipboard = Substitute.For<IClipboardService>();
            var settingsPresenter = Substitute.For<ISettingsWindowPresenter>();
            var desktop = Substitute.For<IDesktopContext>();
            desktop.GetMonitors().Returns([
                new MonitorInfo(@"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 1.0, true)
            ]);
            desktop.GetCursorPosition().Returns(new PixelPoint(500, 500));

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IAppPaths>(paths);
            services.AddSingleton<ISecretStore>(secretStore);
            services.AddSingleton<IAutoStartService>(autoStartService);
            services.AddSingleton<IUiDispatcher>(uiDispatcher);
            services.AddSingleton(clipboard);
            services.AddSingleton(settingsPresenter);
            services.AddSingleton(desktop);
            services.AddSingleton<INotificationService>(Substitute.For<INotificationService>());
            services.AddSingleton<ISettingsApplyReceiptManager, SettingsApplyReceiptManager>();
            services.AddSingleton<ThemeService>();

            // 注册 Fake Provider 工厂
            services.AddSingleton<ITranslationProviderFactory>(new FakeTranslationProviderFactory());

            // 注册基础设施与核心服务
            services.AddIrisInfrastructure();
            services.AddIrisCore();
            services.AddSingleton(Substitute.For<IScreenshotService>());
            services.AddSingleton(Substitute.For<IScreenshotOcrFeedback>());
            services.AddSingleton(Substitute.For<Iris.Core.Ocr.IOcrService>());
            services.AddSingleton(Substitute.For<ISelectionTextReader>());

            // 注册设置层 Scoped 服务
            services.AddScoped<GeneralSettingsSaveCoordinator>();
            services.AddScoped<SettingsViewModel>();

            // 注册翻译窗口组件
            services.AddScoped<TranslateWindowViewModel>();
            services.AddTransient<TranslateWindow>(sp => new TranslateWindow
            {
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000,
                Opacity = 0
            });
            services.AddSingleton<TranslateWindowPresenter>();
            services.AddSingleton<ITranslateWindowPresenter>(sp => sp.GetRequiredService<TranslateWindowPresenter>());

            // 注册动作分发器
            services.AddSingleton<ActionDispatcher>();
            services.AddSingleton<IActionDispatcher>(sp => sp.GetRequiredService<ActionDispatcher>());

            using var rootProvider = services.BuildServiceProvider();

            var settingsService = rootProvider.GetRequiredService<ISettingsService>();
            await settingsService.LoadAsync();
            await settingsService.UpdateAsync(s => s with
            {
                TranslateWindow = s.TranslateWindow with
                {
                    HideOnDeactivate = false
                }
            }, TestContext.Current.CancellationToken);

            // 1. 真实设置 ViewModel 保存配置
            using (var scope = rootProvider.CreateScope())
            {
                var settingsVm = scope.ServiceProvider.GetRequiredService<SettingsViewModel>();
                var preset = SettingsViewModel.AvailablePresets[0];

                settingsVm.AddProfileFromPreset(preset);
                var profile1 = settingsVm.Profiles.Last();
                profile1.DisplayName = "Provider Alpha";
                profile1.Model = "gpt-4o";
                profile1.CredentialEditKind = CredentialEditKind.Replace;
                profile1.SetReplacementApiKey("sk-test-key-alpha");

                settingsVm.AddProfileFromPreset(preset);
                var profile2 = settingsVm.Profiles.Last();
                profile2.DisplayName = "Provider Beta";
                profile2.Model = "deepseek-v3";
                profile2.CredentialEditKind = CredentialEditKind.Replace;
                profile2.SetReplacementApiKey("sk-test-key-beta");

                await settingsVm.ExecuteSaveAsync();
                settingsVm.IsDirty.ShouldBeFalse();
            }

            settingsService.Current.Translation.Providers.Count.ShouldBe(2);

            var dispatcher = rootProvider.GetRequiredService<IActionDispatcher>();
            var presenter = rootProvider.GetRequiredService<TranslateWindowPresenter>();

            try
            {
                // 2. 实际 Dispatcher 触发输入翻译
                await dispatcher.DispatchAsync(
                    IrisAction.InputTranslate,
                    new ActionContext(ActionTrigger.TrayMenu, Payload: "Hello Iris World"));

                presenter.IsVisible.ShouldBeTrue();
                var vm = presenter.ViewModel;
                vm.ShouldNotBeNull();
                vm!.InputText.ShouldBe("Hello Iris World");

                // 等待后台流式翻译完成渲染
                for (var i = 0; i < 50 && (vm.Cards.Count < 2 || vm.IsTranslating); i++)
                {
                    await Task.Delay(50);
                }

                vm.Cards.Count.ShouldBe(2);
                vm.Cards.Any(c => c.Text.Contains("[Provider Alpha] Hello Iris World")).ShouldBeTrue();
                vm.Cards.Any(c => c.Text.Contains("[Provider Beta] Hello Iris World")).ShouldBeTrue();

                // 3. 验证 caller 租约已归还后，窗口内的独立重试与翻译依然完全可用
                vm.ActiveOperation.ShouldNotBeNull();
                vm.ActiveOperation!.IsCallerDisposed.ShouldBeTrue();
                presenter.ActiveOperationsCount.ShouldBe(1);

                var cardToRetry = vm.Cards[0];
                cardToRetry.RetryCommand.CanExecute(null).ShouldBeTrue();
                cardToRetry.RetryCommand.Execute(null);

                for (var i = 0; i < 50 && (string.IsNullOrWhiteSpace(cardToRetry.Text) || vm.IsTranslating); i++)
                {
                    await Task.Delay(50);
                }

                cardToRetry.Text.ShouldNotBeNullOrWhiteSpace();

                // 再次手动触发输入框翻译
                vm.InputText = "Second Sentence";
                vm.TranslateCommand.CanExecute(null).ShouldBeTrue();
                vm.TranslateCommand.Execute(null);

                for (var i = 0; i < 50 && (!vm.Cards.All(c => c.Text.Contains("Second Sentence")) || vm.IsTranslating); i++)
                {
                    await Task.Delay(50);
                }

                vm.Cards.Any(c => c.Text.Contains("[Provider Alpha] Second Sentence")).ShouldBeTrue();
                vm.Cards.Any(c => c.Text.Contains("[Provider Beta] Second Sentence")).ShouldBeTrue();
            }
            finally
            {
                await presenter.ShutdownAsync(TestContext.Current.CancellationToken);
            }
        });
    }
}
