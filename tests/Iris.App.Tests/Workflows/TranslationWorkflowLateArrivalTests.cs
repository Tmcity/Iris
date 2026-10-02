using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Tests.TestSupport;
using Iris.App.Translation;
using Iris.Core.Actions;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Languages;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Iris.Core.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Workflows;

public sealed class TranslationWorkflowLateArrivalTests
{
    private sealed class GatedClipboardService : IClipboardService
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string?> Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IgnoreCancellationOnGateWait { get; set; }
        public int CopyCalls;

        public async Task<string?> GetTextAsync(CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();

            if (IgnoreCancellationOnGateWait)
            {
                return await Gate.Task;
            }

            return await Gate.Task.WaitAsync(cancellationToken);
        }

        public Task SetTextAsync(string text, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CopyCalls);
            return Task.CompletedTask;
        }

        public Task SetImageAsync(ImageData image, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public uint GetSequenceNumber() => 1;
    }

    private static async IAsyncEnumerable<TranslationEvent> EmptyTranslationEvents([EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        yield break;
    }

    private static (
        TranslateWindowPresenter Presenter,
        GatedClipboardService Clipboard,
        ActionDispatcher Dispatcher,
        InputTranslateHandler InputHandler,
        ClipboardTranslateHandler ClipboardHandler,
        TranslateTextHandler TextHandler,
        ServiceProvider ServiceProvider) CreateSut()
    {
        var uiDispatcher = new WpfUiDispatcher();
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(new AppSettings
        {
            TranslateWindow = new TranslateWindowSettings
            {
                HideOnDeactivate = false
            }
        });

        var orchestrator = Substitute.For<ITranslationOrchestrator>();
        orchestrator.TranslateAsync(Arg.Any<TranslationRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => EmptyTranslationEvents());

        var clipboard = new GatedClipboardService();
        var settingsPresenter = Substitute.For<ISettingsWindowPresenter>();

        var desktop = Substitute.For<IDesktopContext>();
        desktop.GetMonitors().Returns([
            new MonitorInfo(@"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 1.0, true)
        ]);
        desktop.GetCursorPosition().Returns(new PixelPoint(500, 500));

        var textPreprocessor = Substitute.For<ITextPreprocessor>();
        textPreprocessor.Prepare(Arg.Any<string>(), Arg.Any<TextOrigin>())
            .Returns(ci => ci.Arg<string>() ?? string.Empty);

        var services = new ServiceCollection();
        services.AddSingleton(settingsService);
        services.AddSingleton(orchestrator);
        services.AddSingleton<IClipboardService>(clipboard);
        services.AddSingleton(settingsPresenter);
        services.AddSingleton(desktop);
        services.AddSingleton(textPreprocessor);
        services.AddSingleton<IUiDispatcher>(uiDispatcher);

        services.AddScoped<TranslateWindowViewModel>(sp => new TranslateWindowViewModel(
            settingsService,
            orchestrator,
            clipboard,
            settingsPresenter,
            uiDispatcher,
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
            uiDispatcher,
            settingsService,
            desktop,
            NullLogger<TranslateWindowPresenter>.Instance);

        var inputHandler = new InputTranslateHandler(presenter);
        var clipboardHandler = new ClipboardTranslateHandler(presenter, clipboard, NullLogger<ClipboardTranslateHandler>.Instance);
        var textHandler = new TranslateTextHandler(presenter);

        var handlers = new IActionHandler[] { inputHandler, clipboardHandler, textHandler };
        var notifications = Substitute.For<INotificationService>();
        var dispatcher = new ActionDispatcher(handlers, uiDispatcher, notifications, NullLogger<ActionDispatcher>.Instance);

        return (presenter, clipboard, dispatcher, inputHandler, clipboardHandler, textHandler, provider);
    }

    [Fact]
    public async Task CooperativeClipboardRead_WhenCanceledDuringWait_ExitsPromptlyWithOperationCanceledException()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var (presenter, clipboard, _, _, clipboardHandler, _, provider) = CreateSut();
            clipboard.IgnoreCancellationOnGateWait = false;
            Task? clipTask = null;
            try
            {
                clipTask = clipboardHandler.ExecuteAsync(new ActionContext(ActionTrigger.TrayMenu), CancellationToken.None);
                await clipboard.ReadStarted.Task;

                await presenter.HideAsync();

                // 合作式取消下，隐藏触发取消令牌，clipTask 迅速安静退出
                await clipTask;
                clipTask.IsCompleted.ShouldBeTrue();
                presenter.IsVisible.ShouldBeFalse();
            }
            finally
            {
                clipboard.Gate.TrySetResult(null);
                if (clipboard.Gate.Task.IsFaulted)
                {
                    _ = clipboard.Gate.Task.Exception;
                }
                if (clipTask != null)
                {
                    try { await clipTask; } catch { }
                }
                await presenter.ShutdownAsync(TestContext.Current.CancellationToken);
                provider.Dispose();
            }
        });
    }

    [Fact]
    public async Task LateClipboardRead_WhenSupersededByNewInput_DoesNotOverwriteNewText_AndQuietlyExits()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var (presenter, clipboard, _, _, clipboardHandler, _, provider) = CreateSut();
            clipboard.IgnoreCancellationOnGateWait = true;
            Task? clipTask = null;
            try
            {
                // 1. 发起剪贴板翻译，挂起在剪贴板读取闸门
                clipTask = clipboardHandler.ExecuteAsync(new ActionContext(ActionTrigger.TrayMenu), CancellationToken.None);
                await clipboard.ReadStarted.Task;

                presenter.ActiveOperationsCount.ShouldBe(1);

                // 2. 新意图：输入翻译发起并发布新正文
                using (var op2 = presenter.TryBeginOperation(CancellationToken.None))
                {
                    op2.ShouldNotBeNull();
                    var showAccepted = await presenter.TryShowAsync(op2, new TranslateWindowRequest
                    {
                        Text = "New User Intent Text",
                        Origin = TextOrigin.Input,
                        FocusInput = true,
                        AutoTranslate = false
                    });
                    showAccepted.ShouldBeTrue();
                }

                // 断言新意图发布完成后，旧读取仍未返回（正在在途挂起）
                clipTask.IsCompleted.ShouldBeFalse();

                presenter.IsVisible.ShouldBeTrue();
                presenter.ViewModel.ShouldNotBeNull();
                presenter.ViewModel!.InputText.ShouldBe("New User Intent Text");

                var copiesBefore = clipboard.CopyCalls;

                // 3. 此时放行旧剪贴板迟到读取
                clipboard.Gate.TrySetResult("Old Stale Clipboard Text");

                // 4. 等待旧操作完全结束
                await clipTask;

                // 5. 验证新正文未被旧剪贴板覆盖，窗口依然为新意图文本，无错误，无额外复制
                presenter.ViewModel.InputText.ShouldBe("New User Intent Text");
                presenter.ViewModel.Error.ShouldBeNull();
                presenter.IsVisible.ShouldBeTrue();
                clipboard.CopyCalls.ShouldBe(copiesBefore);
            }
            finally
            {
                clipboard.Gate.TrySetResult(null);
                if (clipboard.Gate.Task.IsFaulted)
                {
                    _ = clipboard.Gate.Task.Exception;
                }
                if (clipTask != null)
                {
                    try { await clipTask; } catch { }
                }
                await presenter.ShutdownAsync(TestContext.Current.CancellationToken);
                provider.Dispose();
            }
        });
    }

    [Fact]
    public async Task LateClipboardRead_WhenWindowHidden_DoesNotReopenWindow_AndQuietlyExits()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var (presenter, clipboard, _, _, clipboardHandler, _, provider) = CreateSut();
            clipboard.IgnoreCancellationOnGateWait = true;
            Task? clipTask = null;
            try
            {
                // 1. 发起剪贴板翻译，挂起在读取
                clipTask = clipboardHandler.ExecuteAsync(new ActionContext(ActionTrigger.TrayMenu), CancellationToken.None);
                await clipboard.ReadStarted.Task;

                // 2. 用户/系统隐藏窗口
                await presenter.HideAsync();
                presenter.IsVisible.ShouldBeFalse();

                // 断言隐藏后旧读取仍在挂起，并未提前退出
                clipTask.IsCompleted.ShouldBeFalse();

                var copiesBefore = clipboard.CopyCalls;

                // 3. 放行迟到读取
                clipboard.Gate.TrySetResult("Late Arriving Text");

                // 4. 等待旧操作退出
                await clipTask;

                // 5. 窗口不得被重新打开，无额外复制
                presenter.IsVisible.ShouldBeFalse();
                clipboard.CopyCalls.ShouldBe(copiesBefore);
            }
            finally
            {
                clipboard.Gate.TrySetResult(null);
                if (clipboard.Gate.Task.IsFaulted)
                {
                    _ = clipboard.Gate.Task.Exception;
                }
                if (clipTask != null)
                {
                    try { await clipTask; } catch { }
                }
                await presenter.ShutdownAsync(TestContext.Current.CancellationToken);
                provider.Dispose();
            }
        });
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("whitespace")]
    [InlineData("iris_exception")]
    [InlineData("unknown_exception")]
    public async Task LateClipboardRead_WhenFailsOrEmptyAfterHide_DoesNotReopenWindowWithError(string failureMode)
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var (presenter, clipboard, _, _, clipboardHandler, _, provider) = CreateSut();
            clipboard.IgnoreCancellationOnGateWait = true;
            Task? clipTask = null;
            try
            {
                // 1. 发起剪贴板翻译，挂起
                clipTask = clipboardHandler.ExecuteAsync(new ActionContext(ActionTrigger.TrayMenu), CancellationToken.None);
                await clipboard.ReadStarted.Task;

                // 2. 隐藏
                await presenter.HideAsync();
                presenter.IsVisible.ShouldBeFalse();

                // 断言隐藏后旧读取仍在挂起
                clipTask.IsCompleted.ShouldBeFalse();

                var copiesBefore = clipboard.CopyCalls;

                // 3. 模拟剪贴板返回空值或抛出异常
                switch (failureMode)
                {
                    case "empty":
                        clipboard.Gate.TrySetResult(null);
                        break;
                    case "whitespace":
                        clipboard.Gate.TrySetResult("   \r\n   ");
                        break;
                    case "iris_exception":
                        clipboard.Gate.TrySetException(new IrisException(ErrorCode.ClipboardBusy, "Clipboard busy"));
                        break;
                    case "unknown_exception":
                        clipboard.Gate.TrySetException(new InvalidOperationException("Unknown failure"));
                        break;
                }

                // 4. 等待旧操作退出（内部 catch 并发布错误，但 TryShow 返回 false 静默丢弃）
                await clipTask;

                // 5. 窗口保持隐藏且无额外复制，ViewModel 中无残留错误
                presenter.IsVisible.ShouldBeFalse();
                clipboard.CopyCalls.ShouldBe(copiesBefore);
                if (presenter.ViewModel != null)
                {
                    presenter.ViewModel.Error.ShouldBeNull();
                }
            }
            finally
            {
                clipboard.Gate.TrySetResult(null);
                if (clipboard.Gate.Task.IsFaulted)
                {
                    _ = clipboard.Gate.Task.Exception;
                }
                if (clipTask != null)
                {
                    try { await clipTask; } catch { }
                }
                await presenter.ShutdownAsync(TestContext.Current.CancellationToken);
                provider.Dispose();
            }
        });
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("whitespace")]
    [InlineData("iris_exception")]
    [InlineData("unknown_exception")]
    public async Task LateClipboardRead_WhenFailsOrEmptyAfterNewInput_DoesNotOverwriteNewText_OrShowError(string failureMode)
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var (presenter, clipboard, _, _, clipboardHandler, _, provider) = CreateSut();
            clipboard.IgnoreCancellationOnGateWait = true;
            Task? clipTask = null;
            try
            {
                clipTask = clipboardHandler.ExecuteAsync(new ActionContext(ActionTrigger.TrayMenu), CancellationToken.None);
                await clipboard.ReadStarted.Task;

                using (var op2 = presenter.TryBeginOperation(CancellationToken.None))
                {
                    op2.ShouldNotBeNull();
                    var showAccepted = await presenter.TryShowAsync(op2, new TranslateWindowRequest
                    {
                        Text = "New User Intent Text",
                        Origin = TextOrigin.Input,
                        FocusInput = true,
                        AutoTranslate = false
                    });
                    showAccepted.ShouldBeTrue();
                }

                clipTask.IsCompleted.ShouldBeFalse();
                presenter.ViewModel.ShouldNotBeNull();
                presenter.ViewModel!.InputText.ShouldBe("New User Intent Text");

                var copiesBefore = clipboard.CopyCalls;

                switch (failureMode)
                {
                    case "empty":
                        clipboard.Gate.TrySetResult(null);
                        break;
                    case "whitespace":
                        clipboard.Gate.TrySetResult("   \r\n   ");
                        break;
                    case "iris_exception":
                        clipboard.Gate.TrySetException(new IrisException(ErrorCode.ClipboardBusy, "Clipboard busy"));
                        break;
                    case "unknown_exception":
                        clipboard.Gate.TrySetException(new InvalidOperationException("Unknown failure"));
                        break;
                }

                await clipTask;

                presenter.ViewModel.InputText.ShouldBe("New User Intent Text");
                presenter.ViewModel.Error.ShouldBeNull();
                presenter.IsVisible.ShouldBeTrue();
                clipboard.CopyCalls.ShouldBe(copiesBefore);
            }
            finally
            {
                clipboard.Gate.TrySetResult(null);
                if (clipboard.Gate.Task.IsFaulted)
                {
                    _ = clipboard.Gate.Task.Exception;
                }
                if (clipTask != null)
                {
                    try { await clipTask; } catch { }
                }
                await presenter.ShutdownAsync(TestContext.Current.CancellationToken);
                provider.Dispose();
            }
        });
    }

    [Fact]
    public async Task Dispatcher_ClipboardTranslate_IgnoresDuplicateWhileReading_AllowsSubsequentDifferentAction()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var (presenter, clipboard, dispatcher, _, _, _, provider) = CreateSut();
            clipboard.IgnoreCancellationOnGateWait = true;
            Task? task1 = null;
            Task? inputTask = null;
            try
            {
                // 1. 通过 Dispatcher 启动剪贴板翻译，在读取处挂起
                task1 = dispatcher.DispatchAsync(IrisAction.ClipboardTranslate, new ActionContext(ActionTrigger.TrayMenu));
                await clipboard.ReadStarted.Task;

                // 2. 在同一动作未退出前，再次触发 ClipboardTranslate，应被 Dispatcher 忽略防重入
                var task2 = dispatcher.DispatchAsync(IrisAction.ClipboardTranslate, new ActionContext(ActionTrigger.TrayMenu));
                task2.IsCompleted.ShouldBeTrue("防重入调用应立即同步结束");

                // 3. 同时触发不同动作 InputTranslate，应被接纳执行
                inputTask = dispatcher.DispatchAsync(IrisAction.InputTranslate, new ActionContext(ActionTrigger.TrayMenu, Payload: "From Input"));
                await inputTask;

                // 断言新输入完成后，旧任务仍未返回
                task1.IsCompleted.ShouldBeFalse();

                presenter.ViewModel.ShouldNotBeNull();
                presenter.ViewModel!.InputText.ShouldBe("From Input");

                var copiesBefore = clipboard.CopyCalls;

                // 4. 释放剪贴板
                clipboard.Gate.TrySetResult("From Stale Clipboard");
                await task1;

                // 5. 正文依然保留 Input，无多余复制
                presenter.ViewModel.InputText.ShouldBe("From Input");
                presenter.ViewModel.Error.ShouldBeNull();
                clipboard.CopyCalls.ShouldBe(copiesBefore);
            }
            finally
            {
                clipboard.Gate.TrySetResult(null);
                if (clipboard.Gate.Task.IsFaulted)
                {
                    _ = clipboard.Gate.Task.Exception;
                }
                if (task1 != null)
                {
                    try { await task1; } catch { }
                }
                if (inputTask != null)
                {
                    try { await inputTask; } catch { }
                }
                await presenter.ShutdownAsync(TestContext.Current.CancellationToken);
                provider.Dispose();
            }
        });
    }

    [Fact]
    public async Task PresenterShutdown_AwaitsHangingClipboardRead_AndExitsCleanlyUponRelease()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var (presenter, clipboard, _, _, clipboardHandler, _, provider) = CreateSut();
            clipboard.IgnoreCancellationOnGateWait = true;

            Task? clipTask = null;
            Task? shutdownTask = null;
            try
            {
                clipTask = clipboardHandler.ExecuteAsync(new ActionContext(ActionTrigger.TrayMenu), CancellationToken.None);
                await clipboard.ReadStarted.Task;

                presenter.ActiveOperationsCount.ShouldBeGreaterThan(0);

                // 触发关闭，由于前置读取尚未结束，关闭屏障应处于等待中
                using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                shutdownTask = presenter.ShutdownAsync(shutdownCts.Token);

                shutdownTask.IsCompleted.ShouldBeFalse("有挂起的前置操作时，关闭屏障必须等待其退出");

                // 放行读取
                clipboard.Gate.TrySetResult("Final text");

                await clipTask;
                await shutdownTask;

                presenter.ActiveOperationsCount.ShouldBe(0);
            }
            finally
            {
                clipboard.Gate.TrySetResult(null);
                if (clipboard.Gate.Task.IsFaulted)
                {
                    _ = clipboard.Gate.Task.Exception;
                }
                if (clipTask != null)
                {
                    try { await clipTask; } catch { }
                }
                if (shutdownTask != null)
                {
                    try { await shutdownTask; } catch { }
                }
                provider.Dispose();
            }
        });
    }
}
