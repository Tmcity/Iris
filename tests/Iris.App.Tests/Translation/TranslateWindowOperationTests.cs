using System;
using System.Threading;
using System.Threading.Tasks;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Tests.TestSupport;
using Iris.App.Translation;
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

public sealed class TranslateWindowOperationTests
{
    private static (TranslateWindowPresenter Presenter, ITranslationOrchestrator Orchestrator, ISettingsService SettingsService) CreateTestPresenter(IUiDispatcher? uiDispatcher = null)
    {
        var dispatcher = uiDispatcher ?? new ImmediateUiDispatcher();
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
    public void TryBeginOperation_WithCanceledToken_ThrowsOperationCanceledException_AndDoesNotEvictIntent()
    {
        var (presenter, _, _) = CreateTestPresenter();

        using var validOp1 = presenter.TryBeginOperation(TestContext.Current.CancellationToken);
        validOp1.ShouldNotBeNull();

        using var canceledCts = new CancellationTokenSource();
        canceledCts.Cancel();

        Should.Throw<OperationCanceledException>(() =>
        {
            presenter.TryBeginOperation(canceledCts.Token);
        });

        // 原意图未被破坏，且操作名额仅为 1
        presenter.ActiveOperationsCount.ShouldBe(1);

        using var validOp2 = presenter.TryBeginOperation(TestContext.Current.CancellationToken);
        validOp2.ShouldNotBeNull();
        validOp2!.Id.ShouldNotBe(validOp1!.Id);
        presenter.ActiveOperationsCount.ShouldBe(2);
    }

    [Fact]
    public void TryBeginOperation_CapsAt8UnreclaimedStates_ReturnsNull()
    {
        var (presenter, _, _) = CreateTestPresenter();
        var operations = new ITranslateWindowOperation[8];

        for (int i = 0; i < 8; i++)
        {
            var op = presenter.TryBeginOperation(TestContext.Current.CancellationToken);
            op.ShouldNotBeNull();
            operations[i] = op!;
        }

        presenter.ActiveOperationsCount.ShouldBe(8);

        // 第 9 个操作因容量耗尽被拒绝
        var ninth = presenter.TryBeginOperation(TestContext.Current.CancellationToken);
        ninth.ShouldBeNull();

        // 释放 1 个未回收操作后，恢复准入
        operations[0].Dispose();
        presenter.ActiveOperationsCount.ShouldBe(7);

        var restored = presenter.TryBeginOperation(TestContext.Current.CancellationToken);
        restored.ShouldNotBeNull();
        presenter.ActiveOperationsCount.ShouldBe(8);

        for (int i = 1; i < 8; i++)
        {
            operations[i].Dispose();
        }
        restored!.Dispose();
        presenter.ActiveOperationsCount.ShouldBe(0);
    }

    [Fact]
    public async Task TryShowAsync_WithForeignOrForgedOrDisposedOperation_ThrowsOrReturnsFalse()
    {
        var (presenter1, _, _) = CreateTestPresenter();
        var (presenter2, _, _) = CreateTestPresenter();

        using var op1 = presenter1.TryBeginOperation(TestContext.Current.CancellationToken);
        using var op2 = presenter2.TryBeginOperation(TestContext.Current.CancellationToken);

        // 1. null 参数校验
        await Should.ThrowAsync<ArgumentNullException>(async () =>
        {
            await presenter1.TryShowAsync(null!, new TranslateWindowRequest { Text = "test" });
        });

        // 2. 外来 presenter 操作租约抛 ArgumentException
        await Should.ThrowAsync<ArgumentException>(async () =>
        {
            await presenter1.TryShowAsync(op2!, new TranslateWindowRequest { Text = "test" });
        });

        // 3. 伪造租约实现抛 ArgumentException
        var forged = Substitute.For<ITranslateWindowOperation>();
        forged.Id.Returns(Guid.NewGuid());
        await Should.ThrowAsync<ArgumentException>(async () =>
        {
            await presenter1.TryShowAsync(forged, new TranslateWindowRequest { Text = "test" });
        });

        // 4. Dispose 后的租约返回 false
        using var op3 = presenter1.TryBeginOperation(TestContext.Current.CancellationToken);
        op3!.Dispose();
        var result = await presenter1.TryShowAsync(op3, new TranslateWindowRequest { Text = "test" });
        result.ShouldBeFalse();
    }

    [Fact]
    public async Task TryShowAsync_ConcurrentPublishOnSameOperation_ThrowsInvalidOperationException()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var delayedDispatcher = Substitute.For<IUiDispatcher>();
            delayedDispatcher.InvokeAsync(Arg.Any<Func<Task>>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                await gate.Task;
                var callback = call.Arg<Func<Task>>();
                await callback();
            });

            var (presenter, _, _) = CreateTestPresenter(delayedDispatcher);
            using var op = presenter.TryBeginOperation(TestContext.Current.CancellationToken);

            var task1 = presenter.TryShowAsync(op!, new TranslateWindowRequest { Text = "first" });

            // 同一操作并发发布必须抛出 InvalidOperationException
            await Should.ThrowAsync<InvalidOperationException>(async () =>
            {
                await presenter.TryShowAsync(op!, new TranslateWindowRequest { Text = "concurrent" });
            });

            gate.SetResult();
            await task1;
        });
    }

    [Fact]
    public async Task PreReading_Suspension_OlderRequestReturnsAfterNewerOrHide_ReturnsFalseAndNoOverwrite()
    {
        var (presenter, _, _) = CreateTestPresenter();

        // 模拟前置读取阶段 1：获取租约 1 后发生异步耗时操作（如剪贴板/OCR 挂起）
        using var op1 = presenter.TryBeginOperation(TestContext.Current.CancellationToken);
        op1.ShouldNotBeNull();

        // 模拟用户在读取完成前提交了新意图（或隐藏窗口）
        using var op2 = presenter.TryBeginOperation(TestContext.Current.CancellationToken);
        op2.ShouldNotBeNull();

        // 前置读取 1 迟到返回，尝试发布
        var accepted1 = await presenter.TryShowAsync(op1!, new TranslateWindowRequest
        {
            Text = "迟到的旧前置文本",
            AutoTranslate = true
        });

        accepted1.ShouldBeFalse("迟到的旧操作发布资格已失效，必须被拒绝");
    }

    [Fact]
    public void CallerToken_ContinuesToCancelWork_EvenAfterHandlerDispose()
    {
        var (presenter, _, _) = CreateTestPresenter();

        using var callerCts = new CancellationTokenSource();
        var op = presenter.TryBeginOperation(callerCts.Token);
        op.ShouldNotBeNull();

        var internalOp = (TranslateWindowOperation)op!;

        // 模拟 ViewModel 已接管工作所有权
        internalOp.BeginTryShow();
        internalOp.EndTryShow(transferredWorkOwnership: true);
        internalOp.HasWorkOwnership.ShouldBeTrue();

        // Handler 正常退出执行 Dispose（归还调用方持有）
        op.Dispose();
        internalOp.IsCallerDisposed.ShouldBeTrue();

        // 由于后台工作仍在运行，操作状态尚未被注销
        internalOp.IsRetired.ShouldBeFalse();
        internalOp.CancellationToken.IsCancellationRequested.ShouldBeFalse();

        // 调用方令牌触发取消
        callerCts.Cancel();

        // 操作内部令牌成功被调用方令牌取消！
        internalOp.CancellationToken.IsCancellationRequested.ShouldBeTrue();

        // 工作收尾释放
        internalOp.EndWorkOwnership();
        internalOp.IsRetired.ShouldBeTrue();
    }
}
