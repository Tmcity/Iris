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
    private static (TranslateWindowPresenter Presenter, ITranslationOrchestrator Orchestrator, ISettingsService SettingsService) CreateTestPresenter(
        IUiDispatcher? uiDispatcher = null,
        INativeWindowPlacementAdapter? placementAdapter = null,
        IDesktopContext? desktopContext = null)
    {
        var dispatcher = uiDispatcher ?? new WpfUiDispatcher();
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(new AppSettings());

        var orchestrator = Substitute.For<ITranslationOrchestrator>();
        var clipboard = Substitute.For<IClipboardService>();
        var settingsPresenter = Substitute.For<ISettingsWindowPresenter>();
        var desktop = desktopContext ?? Substitute.For<IDesktopContext>();
        if (desktopContext == null)
        {
            desktop.GetMonitors().Returns([
                new MonitorInfo(@"\\.\DISPLAY1", new Iris.Core.Imaging.PixelRect(0, 0, 1920, 1080), new Iris.Core.Imaging.PixelRect(0, 0, 1920, 1040), 1.0, true)
            ]);
            desktop.GetCursorPosition().Returns(new Iris.Core.Imaging.PixelPoint(500, 500));
        }

        var textPreprocessor = Substitute.For<ITextPreprocessor>();
        textPreprocessor.Prepare(Arg.Any<string>(), Arg.Any<TextOrigin>())
            .Returns(ci => ci.Arg<string>()?.Trim() ?? string.Empty);

        var services = new ServiceCollection();
        services.AddSingleton(settingsService);
        services.AddSingleton(orchestrator);
        services.AddSingleton(clipboard);
        services.AddSingleton(settingsPresenter);
        services.AddSingleton(desktop);
        services.AddSingleton(textPreprocessor);
        services.AddSingleton<IUiDispatcher>(dispatcher);

        services.AddScoped<TranslateWindowViewModel>(sp => new TranslateWindowViewModel(
            settingsService,
            orchestrator,
            clipboard,
            settingsPresenter,
            dispatcher,
            NullLogger<TranslateWindowViewModel>.Instance,
            textPreprocessor));

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
            desktop,
            NullLogger<TranslateWindowPresenter>.Instance,
            placementAdapter);

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

                var op = presenter.TryBeginOperation(TestContext.Current.CancellationToken);
                await presenter.TryShowAsync(op!, new TranslateWindowRequest { Text = "Long Running Work", AutoTranslate = true });
                op!.Dispose();

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

                var op = presenter.TryBeginOperation(TestContext.Current.CancellationToken);
                await presenter.TryShowAsync(op!, new TranslateWindowRequest { Text = "Work", AutoTranslate = true });
                op!.Dispose();

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

    [Fact]
    public async Task TryShowAsync_WhenPlacementFails_RevokesOperationAndCancelsWork()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var mockAdapter = Substitute.For<INativeWindowPlacementAdapter>();
            mockAdapter.SetWindowBounds(Arg.Any<nint>(), Arg.Any<Iris.Core.Imaging.PixelRect>(), Arg.Any<bool>(), Arg.Any<bool>()).Returns(false);

            var (presenter, _, _) = CreateTestPresenter(placementAdapter: mockAdapter);
            using var op = presenter.TryBeginOperation(TestContext.Current.CancellationToken);
            op.ShouldNotBeNull();

            var shown = await presenter.TryShowAsync(op!, new TranslateWindowRequest
            {
                Text = "Placement Fail Test",
                AutoTranslate = true
            });

            shown.ShouldBeFalse("当底层适配器定位失败时，TryShowAsync 必须返回 false");
            var internalOp = (TranslateWindowOperation)op!;
            internalOp.CancellationToken.IsCancellationRequested.ShouldBeTrue("定位失败后必须取消该操作");
        });
    }

    [Fact]
    public async Task TryShowAsync_WhenSupersededDuringPreparation_DoesNotApplyToVmAndReturnsFalse()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var desktopContext = Substitute.For<IDesktopContext>();
            var (presenter, _, _) = CreateTestPresenter(desktopContext: desktopContext);

            ITranslateWindowOperation? opB = null;
            int getMonitorsCallCount = 0;

            // 在旧 A 的 GetMonitors 准备边界接纳意图 B
            desktopContext.GetMonitors().Returns(_ =>
            {
                if (Interlocked.Increment(ref getMonitorsCallCount) == 1)
                {
                    opB = presenter.TryBeginOperation(TestContext.Current.CancellationToken);
                }
                return [
                    new MonitorInfo(@"\\.\DISPLAY1", new Iris.Core.Imaging.PixelRect(0, 0, 1920, 1080), new Iris.Core.Imaging.PixelRect(0, 0, 1920, 1040), 1.0, true)
                ];
            });
            desktopContext.GetCursorPosition().Returns(new Iris.Core.Imaging.PixelPoint(500, 500));

            try
            {
                using var opA = presenter.TryBeginOperation(TestContext.Current.CancellationToken);
                opA.ShouldNotBeNull();

                var shownA = await presenter.TryShowAsync(opA!, new TranslateWindowRequest
                {
                    Text = "obsolete-show-A",
                    AutoTranslate = false
                });

                shownA.ShouldBeFalse("被新意图取代的旧操作在提交 UI 状态前必须失效并返回 false");
                presenter.ViewModel!.InputText.ShouldNotBe("obsolete-show-A", "已失效的旧发布不得将文本写入 ViewModel");

                // 意图 B 正常发布
                opB.ShouldNotBeNull();
                using (opB)
                {
                    var shownB = await presenter.TryShowAsync(opB!, new TranslateWindowRequest
                    {
                        Text = "active-show-B",
                        AutoTranslate = false
                    });

                    shownB.ShouldBeTrue("最新意图 B 必须成功发布");
                    presenter.ViewModel.InputText.ShouldBe("active-show-B");
                    presenter.IsVisible.ShouldBeTrue();
                }
            }
            finally
            {
                await presenter.ShutdownAsync();
            }
        });
    }

    [Fact]
    public async Task TryShowAsync_WhenCallerDisposedDuringPreparation_AbortsAndReturnsFalse()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var desktopContext = Substitute.For<IDesktopContext>();
            var (presenter, _, _) = CreateTestPresenter(desktopContext: desktopContext);

            ITranslateWindowOperation? capturedOp = null;
            int getMonitorsCallCount = 0;

            desktopContext.GetMonitors().Returns(_ =>
            {
                if (Interlocked.Increment(ref getMonitorsCallCount) == 1)
                {
                    capturedOp?.Dispose();
                }
                return [
                    new MonitorInfo(@"\\.\DISPLAY1", new Iris.Core.Imaging.PixelRect(0, 0, 1920, 1080), new Iris.Core.Imaging.PixelRect(0, 0, 1920, 1040), 1.0, true)
                ];
            });
            desktopContext.GetCursorPosition().Returns(new Iris.Core.Imaging.PixelPoint(500, 500));

            try
            {
                var op = presenter.TryBeginOperation(TestContext.Current.CancellationToken);
                op.ShouldNotBeNull();
                capturedOp = op;

                var shown = await presenter.TryShowAsync(op!, new TranslateWindowRequest
                {
                    Text = "disposed-during-prep",
                    AutoTranslate = false
                });

                shown.ShouldBeFalse("调用方在准备期 Dispose 时，TryShowAsync 必须中止并返回 false");
                presenter.IsVisible.ShouldBeFalse("未成功发布且无新意图接管时，窗口可见性必须为 false");
                presenter.ViewModel.ShouldNotBeNull();
                presenter.ViewModel!.InputText.ShouldNotBe("disposed-during-prep");
            }
            finally
            {
                await presenter.ShutdownAsync();
            }
        });
    }

    [Fact]
    public async Task TryShowAsync_WhenUpdateFailsOnAlreadyVisibleWindow_PreservesWindowAndSnapshotMatchesActual()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var mockAdapter = Substitute.For<INativeWindowPlacementAdapter>();
            mockAdapter.SetWindowBounds(Arg.Any<nint>(), Arg.Any<Iris.Core.Imaging.PixelRect>(), Arg.Any<bool>(), Arg.Any<bool>())
                .Returns(true);

            var (presenter, _, _) = CreateTestPresenter(placementAdapter: mockAdapter);

            try
            {
                // 1. 首次成功发布
                using (var op1 = presenter.TryBeginOperation(TestContext.Current.CancellationToken))
                {
                    op1.ShouldNotBeNull();
                    var shown1 = await presenter.TryShowAsync(op1!, new TranslateWindowRequest { Text = "First Initial" });
                    shown1.ShouldBeTrue();
                    presenter.IsVisible.ShouldBeTrue();
                    presenter.Window!.IsVisible.ShouldBeTrue();
                }

                // 2. 原生适配器在后续更新时失败
                mockAdapter.SetWindowBounds(Arg.Any<nint>(), Arg.Any<Iris.Core.Imaging.PixelRect>(), Arg.Any<bool>(), Arg.Any<bool>())
                    .Returns(false);

                using (var op2 = presenter.TryBeginOperation(TestContext.Current.CancellationToken))
                {
                    op2.ShouldNotBeNull();
                    var shown2 = await presenter.TryShowAsync(op2!, new TranslateWindowRequest { Text = "Second Update Failed" });
                    shown2.ShouldBeFalse("底层定位适配器返回 false 时发布必须返回 false");

                    // 核心验证：已有可见窗口必须保留存活，IsVisible 快照必须与实际窗口完全一致
                    presenter.Window!.IsVisible.ShouldBeTrue("更新失败时已存在的窗口不得被误隐藏");
                    presenter.IsVisible.ShouldBeTrue("Presenter.IsVisible 快照必须反映实际窗口存活状态");
                }

                // 3. 再次成功发布，恢复正常更新
                mockAdapter.SetWindowBounds(Arg.Any<nint>(), Arg.Any<Iris.Core.Imaging.PixelRect>(), Arg.Any<bool>(), Arg.Any<bool>())
                    .Returns(true);

                using (var op3 = presenter.TryBeginOperation(TestContext.Current.CancellationToken))
                {
                    op3.ShouldNotBeNull();
                    var shown3 = await presenter.TryShowAsync(op3!, new TranslateWindowRequest { Text = "Third Success" });
                    shown3.ShouldBeTrue();
                    presenter.IsVisible.ShouldBeTrue();
                    presenter.ViewModel!.InputText.ShouldBe("Third Success");
                }
            }
            finally
            {
                await presenter.ShutdownAsync();
            }
        });
    }

    [Fact]
    public async Task OperationFinalizeRetire_WhenCallbackBlocked_RetainsOwnershipUntilCallbackExits()
    {
        var (presenter, _, _) = CreateTestPresenter();
        using var callerCts = new CancellationTokenSource();
        var op = presenter.TryBeginOperation(callerCts.Token);
        op.ShouldNotBeNull();
        presenter.ActiveOperationsCount.ShouldBe(1);

        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var internalOp = (TranslateWindowOperation)op!;
        using var reg = internalOp.CancellationToken.Register(() =>
        {
            callbackEntered.TrySetResult();
            callbackRelease.Task.GetAwaiter().GetResult();
        });

        try
        {
            // 取消 caller token 触发操作退役
            callerCts.Cancel();
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // Caller Dispose 操作
            op.Dispose();

            // 验证：在取消回调退出之前，活跃名额未归还（仍为 1）
            presenter.ActiveOperationsCount.ShouldBe(1, "在取消回调完全退出前，操作不得从活跃集合移出");

            // 放行回调
            callbackRelease.TrySetResult();

            // 轮询等待退役完成
            for (int i = 0; i < 50 && presenter.ActiveOperationsCount > 0; i++)
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }

            presenter.ActiveOperationsCount.ShouldBe(0, "取消回调安全退出后，名额必须归还");
        }
        finally
        {
            callbackRelease.TrySetResult();
            await presenter.ShutdownAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task TryShowAsync_WhenHiddenDuringPreparation_AbortsAndWindowIsNotVisible()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var desktop = Substitute.For<IDesktopContext>();
            TranslateWindowPresenter? presenterRef = null;
            Task? hideTask = null;
            desktop.GetMonitors().Returns(ci =>
            {
                if (presenterRef != null && hideTask == null)
                {
                    hideTask = presenterRef.HideAsync();
                }
                return [
                    new MonitorInfo(@"\\.\DISPLAY1", new Iris.Core.Imaging.PixelRect(0, 0, 1920, 1080), new Iris.Core.Imaging.PixelRect(0, 0, 1920, 1040), 1.0, true)
                ];
            });
            desktop.GetCursorPosition().Returns(new Iris.Core.Imaging.PixelPoint(500, 500));

            var (presenter, _, _) = CreateTestPresenter(desktopContext: desktop);
            presenterRef = presenter;

            ITranslateWindowOperation? op = presenter.TryBeginOperation(CancellationToken.None);
            op.ShouldNotBeNull();

            try
            {
                var accepted = await presenter.TryShowAsync(op!, new TranslateWindowRequest { Text = "HiddenDuringPrep", AutoTranslate = false });
                if (hideTask != null)
                {
                    await hideTask;
                }

                accepted.ShouldBeFalse("准备期隐藏后发布必须被拒绝");
                presenter.IsVisible.ShouldBeFalse();
                (presenter.Window?.IsVisible ?? false).ShouldBeFalse("隐藏后不得重新展示窗口");
            }
            finally
            {
                op?.Dispose();
                if (hideTask != null)
                {
                    await hideTask;
                }
                await presenter.ShutdownAsync(TestContext.Current.CancellationToken);
            }
        });
    }

    [Fact]
    public async Task TryShowAsync_WhenReenteredDuringPropertyChanged_NewerOperationWins()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var (presenter, _, _) = CreateTestPresenter();
            ITranslateWindowOperation? warmOp = null;
            ITranslateWindowOperation? a = null;
            ITranslateWindowOperation? b = null;
            Task<bool>? publishB = null;
            bool reentered = false;

            try
            {
                // 预热展示器以创建窗口与 ViewModel
                warmOp = presenter.TryBeginOperation(CancellationToken.None);
                await presenter.TryShowAsync(warmOp!, new TranslateWindowRequest { Text = "Warmup", AutoTranslate = false });

                var vm = presenter.ViewModel;
                vm.ShouldNotBeNull();

                vm!.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(TranslateWindowViewModel.InputText) && !reentered)
                    {
                        reentered = true;
                        b = presenter.TryBeginOperation(CancellationToken.None);
                        publishB = presenter.TryShowAsync(b!, new TranslateWindowRequest { Text = "NewerIntent", AutoTranslate = false });
                    }
                };

                a = presenter.TryBeginOperation(CancellationToken.None);
                a.ShouldNotBeNull();

                var acceptedA = await presenter.TryShowAsync(a!, new TranslateWindowRequest { Text = "OldIntent", AutoTranslate = false });
                var acceptedB = publishB != null && await publishB;

                acceptedA.ShouldBeFalse("被新意图重入取代的旧操作 A 必须返回 false");
                acceptedB.ShouldBeTrue("新意图 B 必须成功发布并接管窗口");
                vm.InputText.ShouldBe("NewerIntent");
                ReferenceEquals(vm.ActiveOperation, b).ShouldBeTrue("活跃会话必须为新意图 B");
                b!.CancellationToken.IsCancellationRequested.ShouldBeFalse("新意图 B 不得被旧 A 的后续流程取消");
                ((TranslateWindowOperation)b).HasSessionOwnership.ShouldBeTrue();
                ((TranslateWindowOperation)a!).HasSessionOwnership.ShouldBeFalse();
            }
            finally
            {
                warmOp?.Dispose();
                a?.Dispose();
                b?.Dispose();
                await presenter.ShutdownAsync(TestContext.Current.CancellationToken);
            }
        });
    }

    [Fact]
    public async Task TryShowAsync_WhenHiddenDuringPropertyChanged_AbortsCleanly()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var (presenter, _, _) = CreateTestPresenter();
            ITranslateWindowOperation? warmOp = null;
            ITranslateWindowOperation? a = null;
            Task? hideTask = null;
            bool reentered = false;

            try
            {
                warmOp = presenter.TryBeginOperation(CancellationToken.None);
                await presenter.TryShowAsync(warmOp!, new TranslateWindowRequest { Text = "Warmup", AutoTranslate = false });

                var vm = presenter.ViewModel;
                vm.ShouldNotBeNull();

                vm!.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(TranslateWindowViewModel.InputText) && !reentered)
                    {
                        reentered = true;
                        hideTask = presenter.HideAsync();
                    }
                };

                a = presenter.TryBeginOperation(CancellationToken.None);
                var accepted = await presenter.TryShowAsync(a!, new TranslateWindowRequest { Text = "HideDuringPropChanged", AutoTranslate = false });
                if (hideTask != null)
                {
                    await hideTask;
                }

                accepted.ShouldBeFalse();
                presenter.IsVisible.ShouldBeFalse();
                (presenter.Window?.IsVisible ?? false).ShouldBeFalse();
                vm.ActiveOperation.ShouldBeNull();
            }
            finally
            {
                warmOp?.Dispose();
                a?.Dispose();
                if (hideTask != null)
                {
                    await hideTask;
                }
                await presenter.ShutdownAsync(TestContext.Current.CancellationToken);
            }
        });
    }

    [Fact]
    public async Task TryShowAsync_NormalPublication_SnapshotMatchesActualWindowProperties()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var (presenter, _, _) = CreateTestPresenter();
            ITranslateWindowOperation? op = presenter.TryBeginOperation(CancellationToken.None);
            op.ShouldNotBeNull();

            try
            {
                var accepted = await presenter.TryShowAsync(op!, new TranslateWindowRequest { Text = "Normal", FocusInput = true, AutoTranslate = false });
                accepted.ShouldBeTrue();
                presenter.Window.ShouldNotBeNull();
                presenter.IsVisible.ShouldBe(presenter.Window!.IsVisible);
                presenter.IsActive.ShouldBe(presenter.Window!.IsActive);
            }
            finally
            {
                op?.Dispose();
                await presenter.ShutdownAsync(TestContext.Current.CancellationToken);
            }
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
