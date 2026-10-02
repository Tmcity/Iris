using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Tests.TestSupport;
using Iris.App.Translation;
using Iris.Core.Common;
using Iris.Core.Languages;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Translation;

public sealed class TranslateWindowPresenterTests
{
    private static (TranslateWindowPresenter Presenter, ITranslationOrchestrator Orchestrator, ISettingsService SettingsService) CreateTestPresenter(IUiDispatcher? uiDispatcher = null)
    {
        var dispatcher = uiDispatcher ?? new WpfUiDispatcher();
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(new AppSettings());

        var orchestrator = Substitute.For<ITranslationOrchestrator>();
        var clipboard = Substitute.For<IClipboardService>();
        var settingsPresenter = Substitute.For<ISettingsWindowPresenter>();
        var desktopContext = Substitute.For<IDesktopContext>();
        desktopContext.GetMonitors().Returns([
            new MonitorInfo(@"\\.\DISPLAY1", new Iris.Core.Imaging.PixelRect(0, 0, 1920, 1080), new Iris.Core.Imaging.PixelRect(0, 0, 1920, 1040), 1.0, true)
        ]);
        desktopContext.GetCursorPosition().Returns(new Iris.Core.Imaging.PixelPoint(500, 500));

        var services = new ServiceCollection();
        services.AddSingleton(settingsService);
        services.AddSingleton(orchestrator);
        services.AddSingleton(clipboard);
        services.AddSingleton(settingsPresenter);
        services.AddSingleton(desktopContext);
        services.AddSingleton<IUiDispatcher>(dispatcher);

        services.AddScoped<TranslateWindowViewModel>(sp => new TranslateWindowViewModel(
            settingsService,
            orchestrator,
            clipboard,
            settingsPresenter,
            dispatcher,
            NullLogger<TranslateWindowViewModel>.Instance));

        services.AddTransient<TranslateWindow>(sp => new TranslateWindow
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            Opacity = 0
        });

        var provider = services.BuildServiceProvider();

        var presenter = new TranslateWindowPresenter(
            provider,
            dispatcher,
            settingsService,
            desktopContext,
            NullLogger<TranslateWindowPresenter>.Instance);

        return (presenter, orchestrator, settingsService);
    }

    [Fact]
    public void PresenterConstruction_DoesNotCreateWindowOrPerformIO()
    {
        var (presenter, _, _) = CreateTestPresenter();

        presenter.IsVisible.ShouldBeFalse();
        presenter.IsActive.ShouldBeFalse();
        presenter.Window.ShouldBeNull("构造期不得按需提前创建 WPF 窗口");
        presenter.ViewModel.ShouldBeNull("构造期不得提前创建 ViewModel 作用域");
    }

    [Fact]
    public async Task ShutdownAsync_WaitsForActiveWork_BeforeCompleting()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var (presenter, orchestrator, _) = CreateTestPresenter();

            var workGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                var blockingEnumerable = new BlockingAsyncEnumerable(workGate);
                orchestrator.TranslateAsync(Arg.Any<TranslationRequest>(), Arg.Any<CancellationToken>())
                    .Returns(blockingEnumerable);

                using var op = presenter.TryBeginOperation(TestContext.Current.CancellationToken);
                await presenter.TryShowAsync(op!, new TranslateWindowRequest { Text = "Long Running Work", AutoTranslate = true });

                // 启动关闭流程
                var shutdownTask = presenter.ShutdownAsync(TestContext.Current.CancellationToken);

                // 当 workGate 尚未放行时，ShutdownAsync 不得提前报告完成
                shutdownTask.IsCompleted.ShouldBeFalse();

                // 放行后台异步枚举
                workGate.TrySetResult();

                // 此时退出屏障成功等待并完成
                await shutdownTask;
                shutdownTask.IsCompletedSuccessfully.ShouldBeTrue();
            }
            finally
            {
                workGate.TrySetResult();
            }
        });
    }

    [Fact]
    public async Task ShutdownAsync_ConcurrentCalls_AwaitSameBarrier()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var (presenter, orchestrator, _) = CreateTestPresenter();

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                orchestrator.TranslateAsync(Arg.Any<TranslationRequest>(), Arg.Any<CancellationToken>())
                    .Returns(new BlockingAsyncEnumerable(gate));

                using var op = presenter.TryBeginOperation(TestContext.Current.CancellationToken);
                await presenter.TryShowAsync(op!, new TranslateWindowRequest { Text = "Work", AutoTranslate = true });

                var shutdownTask1 = presenter.ShutdownAsync(TestContext.Current.CancellationToken);
                var shutdownTask2 = presenter.ShutdownAsync(TestContext.Current.CancellationToken);

                shutdownTask1.ShouldBeSameAs(shutdownTask2, "多个并发 ShutdownAsync 必须等待同一个底层屏障");

                gate.TrySetResult();
                await shutdownTask1;
            }
            finally
            {
                gate.TrySetResult();
            }
        });
    }

    [Fact]
    public async Task HideAsync_InvalidatesOldOperationRights()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var (presenter, _, _) = CreateTestPresenter();

            using var op1 = presenter.TryBeginOperation(TestContext.Current.CancellationToken);
            await presenter.TryShowAsync(op1!, new TranslateWindowRequest { Text = "Sample Text", AutoTranslate = false });

            presenter.IsVisible.ShouldBeTrue();

            // 隐藏窗口
            await presenter.HideAsync();

            presenter.IsVisible.ShouldBeFalse();

            // 尝试用旧操作重新发布，必须被拒绝
            var result = await presenter.TryShowAsync(op1!, new TranslateWindowRequest { Text = "Late Overwrite", AutoTranslate = false });
            result.ShouldBeFalse();
        });
    }

    private sealed class BlockingAsyncEnumerable : IAsyncEnumerable<TranslationEvent>
    {
        private readonly TaskCompletionSource _gate;

        public BlockingAsyncEnumerable(TaskCompletionSource gate)
        {
            _gate = gate;
        }

        public IAsyncEnumerator<TranslationEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            return new BlockingAsyncEnumerator(_gate);
        }

        private sealed class BlockingAsyncEnumerator : IAsyncEnumerator<TranslationEvent>
        {
            private readonly TaskCompletionSource _gate;
            private int _index;

            public BlockingAsyncEnumerator(TaskCompletionSource gate)
            {
                _gate = gate;
            }

            public TranslationEvent Current { get; private set; } = null!;

            public async ValueTask<bool> MoveNextAsync()
            {
                if (_index == 0)
                {
                    _index++;
                    Current = new TranslationStarted(Guid.NewGuid(), new ResolvedLanguagePair("en", "zh-Hans", false), []);
                    return true;
                }

                if (_index == 1)
                {
                    _index++;
                    await _gate.Task;
                    return false;
                }

                return false;
            }

            public ValueTask DisposeAsync()
            {
                _gate.TrySetResult();
                return ValueTask.CompletedTask;
            }
        }
    }
}
