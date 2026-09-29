#pragma warning disable xUnit1051, CA2016

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Iris.App;
using Iris.App.Hosting;
using Iris.App.Resources;
using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Presentation;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Hosting;

public sealed class ActionDispatcherTests
{
    // =========================================================================
    // 场景 1: 空/部分注册，重复/非法/null 注册
    // =========================================================================

    [Fact]
    public void Constructor_WithNullArguments_ThrowsArgumentNullException()
    {
        var logger = new TestLogger<ActionDispatcher>();
        var notifications = new TestNotificationService();
        var uiDispatcher = new ImmediateUiDispatcher();

        Should.Throw<ArgumentNullException>(() => new ActionDispatcher(null!, uiDispatcher, notifications, logger));
        Should.Throw<ArgumentNullException>(() => new ActionDispatcher([], null!, notifications, logger));
        Should.Throw<ArgumentNullException>(() => new ActionDispatcher([], uiDispatcher, null!, logger));
        Should.Throw<ArgumentNullException>(() => new ActionDispatcher([], uiDispatcher, notifications, null!));
    }

    [Fact]
    public void Constructor_WithEmptyHandlers_Succeeds()
    {
        var logger = new TestLogger<ActionDispatcher>();
        var notifications = new TestNotificationService();
        var uiDispatcher = new ImmediateUiDispatcher();

        var dispatcher = new ActionDispatcher([], uiDispatcher, notifications, logger);
        dispatcher.ShouldNotBeNull();
    }

    [Fact]
    public async Task Constructor_WithPartialHandlers_RegistersAvailableActions_AndWarnsOnUnregistered()
    {
        var logger = new TestLogger<ActionDispatcher>();
        var notifications = new TestNotificationService();
        var uiDispatcher = new ImmediateUiDispatcher();

        var executed = false;
        var handler = new TestHandler(IrisAction.InputTranslate, (_, _) =>
        {
            executed = true;
            return Task.CompletedTask;
        });

        var dispatcher = new ActionDispatcher([handler], uiDispatcher, notifications, logger);

        // 注册的动作正常执行
        var ctx = new ActionContext(ActionTrigger.Hotkey);
        await dispatcher.DispatchAsync(IrisAction.InputTranslate, ctx);
        executed.ShouldBeTrue();
        notifications.Notifications.ShouldBeEmpty();

        // 未注册的合法动作提示功能尚未开放
        await dispatcher.DispatchAsync(IrisAction.OpenSettings, ctx);
        notifications.Notifications.Count.ShouldBe(1);
        notifications.Notifications[0].Level.ShouldBe(NotificationLevel.Warning);
        notifications.Notifications[0].Title.ShouldBe(AppResources.AppName);
        notifications.Notifications[0].Message.ShouldBe(AppResources.ActionNotAvailable);
    }

    [Fact]
    public void Constructor_WithNullHandlerInCollection_ThrowsArgumentNullException_WithoutFormattingObject()
    {
        var logger = new TestLogger<ActionDispatcher>();
        var notifications = new TestNotificationService();
        var uiDispatcher = new ImmediateUiDispatcher();

        var ex = Should.Throw<ArgumentNullException>(() =>
            new ActionDispatcher([null!], uiDispatcher, notifications, logger));

        ex.ParamName.ShouldBe("handlers");
    }

    [Fact]
    public void Constructor_WithUndefinedAction_ThrowsArgumentException_WithEnumOrIntOnly()
    {
        var logger = new TestLogger<ActionDispatcher>();
        var notifications = new TestNotificationService();
        var uiDispatcher = new ImmediateUiDispatcher();

        var undefinedHandler = new TestHandler((IrisAction)999, (_, _) => Task.CompletedTask);

        var ex = Should.Throw<ArgumentException>(() =>
            new ActionDispatcher([undefinedHandler], uiDispatcher, notifications, logger));

        ex.Message.ShouldContain("999");
        ex.Message.ShouldNotContain(undefinedHandler.ToString()!);
    }

    [Fact]
    public void Constructor_WithDuplicateActionHandlers_ThrowsArgumentException_WithoutFormattingHandlerObject()
    {
        var logger = new TestLogger<ActionDispatcher>();
        var notifications = new TestNotificationService();
        var uiDispatcher = new ImmediateUiDispatcher();

        var handler1 = new TestHandler(IrisAction.InputTranslate, (_, _) => Task.CompletedTask);
        var handler2 = new TestHandler(IrisAction.InputTranslate, (_, _) => Task.CompletedTask);

        var ex = Should.Throw<ArgumentException>(() =>
            new ActionDispatcher([handler1, handler2], uiDispatcher, notifications, logger));

        ex.Message.ShouldContain(nameof(IrisAction.InputTranslate));
        ex.Message.ShouldNotContain(handler1.ToString()!);
    }

    [Fact]
    public async Task DispatchAsync_WithNullContextOrInvalidAction_NotifiesInvalidArgumentError()
    {
        var logger = new TestLogger<ActionDispatcher>();
        var notifications = new TestNotificationService();
        var uiDispatcher = new ImmediateUiDispatcher();

        var handler = new TestHandler(IrisAction.InputTranslate, (_, _) => Task.CompletedTask);
        var dispatcher = new ActionDispatcher([handler], uiDispatcher, notifications, logger);

        // context == null
        await dispatcher.DispatchAsync(IrisAction.InputTranslate, null!);
        notifications.Notifications.Count.ShouldBe(1);
        notifications.Notifications[0].Level.ShouldBe(NotificationLevel.Error);
        notifications.Notifications[0].Title.ShouldBe(AppResources.AppName);
        notifications.Notifications[0].Message.ShouldBe(ErrorMessages.GetDefault(ErrorCode.InvalidArgument));

        // invalid action enum
        await dispatcher.DispatchAsync((IrisAction)888, new ActionContext(ActionTrigger.Hotkey));
        notifications.Notifications.Count.ShouldBe(2);
        notifications.Notifications[1].Level.ShouldBe(NotificationLevel.Error);
        notifications.Notifications[1].Message.ShouldBe(ErrorMessages.GetDefault(ErrorCode.InvalidArgument));
    }

    // =========================================================================
    // 场景 2: 同 Action 多线程触发，第一次尚未进入 UI
    // =========================================================================

    [Fact]
    public async Task DispatchAsync_ConcurrentSameAction_BeforeEnteringUi_OnlyQueuesOnce_IgnoresSecondCall()
    {
        var logger = new TestLogger<ActionDispatcher>();
        var notifications = new TestNotificationService();
        var controllableUi = new ControllableUiDispatcher();

        var queueEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceedUi = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerExecutionCount = 0;

        controllableUi.Interceptor = async (callback, ct) =>
        {
            queueEntered.TrySetResult();
            await proceedUi.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await callback();
        };

        var handler = new TestHandler(IrisAction.InputTranslate, (_, _) =>
        {
            Interlocked.Increment(ref handlerExecutionCount);
            return Task.CompletedTask;
        });

        var dispatcher = new ActionDispatcher([handler], controllableUi, notifications, logger);
        var ctx = new ActionContext(ActionTrigger.Hotkey);

        // 第一次调用：在后台触发，卡在 UI 排队前/中
        var firstCallTask = Task.Run(() => dispatcher.DispatchAsync(IrisAction.InputTranslate, ctx));
        await queueEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 第二次调用：在另一个线程触发同动作，应立即返回已完成任务，无通知，不调用第二次 Handler
        var secondCallTask = Task.Run(() => dispatcher.DispatchAsync(IrisAction.InputTranslate, ctx));
        var completed = await Task.WhenAny(secondCallTask, Task.Delay(1000));
        completed.ShouldBeSameAs(secondCallTask, "第二次调用必须立即完成");
        secondCallTask.IsCompletedSuccessfully.ShouldBeTrue();

        handlerExecutionCount.ShouldBe(0);
        notifications.Notifications.ShouldBeEmpty();

        // 放行第一次调用
        proceedUi.TrySetResult();
        await firstCallTask.WaitAsync(TimeSpan.FromSeconds(5));

        handlerExecutionCount.ShouldBe(1);
        notifications.Notifications.ShouldBeEmpty();
    }

    // =========================================================================
    // 场景 3: Handler 首次 await 后挂起
    // =========================================================================

    [Fact]
    public async Task DispatchAsync_HandlerSuspendedAfterFirstAwait_RetainsExecutionRight_UntilCompleted()
    {
        var logger = new TestLogger<ActionDispatcher>();
        var notifications = new TestNotificationService();
        var uiDispatcher = new ImmediateUiDispatcher();

        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerResume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionCount = 0;

        var handler = new TestHandler(IrisAction.InputTranslate, async (_, _) =>
        {
            Interlocked.Increment(ref executionCount);
            handlerStarted.TrySetResult();
            await handlerResume.Task.WaitAsync(TimeSpan.FromSeconds(5));
        });

        var dispatcher = new ActionDispatcher([handler], uiDispatcher, notifications, logger);
        var ctx = new ActionContext(ActionTrigger.Hotkey);

        var firstCall = dispatcher.DispatchAsync(IrisAction.InputTranslate, ctx);
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        firstCall.IsCompleted.ShouldBeFalse("首次 await 挂起期间原调用未完成");

        // 重复触发同动作应立即被忽略
        var secondCall = dispatcher.DispatchAsync(IrisAction.InputTranslate, ctx);
        secondCall.IsCompletedSuccessfully.ShouldBeTrue("执行中的同动作必须立即忽略");
        executionCount.ShouldBe(1);

        // 放行 Handler
        handlerResume.TrySetResult();
        await firstCall.WaitAsync(TimeSpan.FromSeconds(5));

        // 完成后同动作可再次执行
        var thirdCall = dispatcher.DispatchAsync(IrisAction.InputTranslate, ctx);
        await thirdCall.WaitAsync(TimeSpan.FromSeconds(5));
        executionCount.ShouldBe(2);
    }

    // =========================================================================
    // 场景 4: 截图三动作的不同组合（互斥与释放）
    // =========================================================================

    [Theory]
    [InlineData(IrisAction.ScreenshotTranslate, IrisAction.ScreenshotOcr)]
    [InlineData(IrisAction.ScreenshotTranslate, IrisAction.SilentScreenshotOcr)]
    [InlineData(IrisAction.ScreenshotOcr, IrisAction.ScreenshotTranslate)]
    [InlineData(IrisAction.ScreenshotOcr, IrisAction.SilentScreenshotOcr)]
    [InlineData(IrisAction.SilentScreenshotOcr, IrisAction.ScreenshotTranslate)]
    [InlineData(IrisAction.SilentScreenshotOcr, IrisAction.ScreenshotOcr)]
    [InlineData(IrisAction.ScreenshotTranslate, IrisAction.ScreenshotTranslate)]
    public async Task DispatchAsync_ScreenshotActions_AreMutuallyExclusive(IrisAction action1, IrisAction action2)
    {
        var logger = new TestLogger<ActionDispatcher>();
        var notifications = new TestNotificationService();
        var uiDispatcher = new ImmediateUiDispatcher();

        var action1Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var action1Resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var count1 = 0;
        var count2 = 0;

        var handler1 = new TestHandler(action1, async (_, _) =>
        {
            Interlocked.Increment(ref count1);
            action1Started.TrySetResult();
            await action1Resume.Task.WaitAsync(TimeSpan.FromSeconds(5));
        });

        TestHandler handler2;
        if (action1 == action2)
        {
            handler2 = handler1;
        }
        else
        {
            handler2 = new TestHandler(action2, (_, _) =>
            {
                Interlocked.Increment(ref count2);
                return Task.CompletedTask;
            });
        }

        var handlers = action1 == action2 ? new[] { handler1 } : new[] { handler1, handler2 };
        var dispatcher = new ActionDispatcher(handlers, uiDispatcher, notifications, logger);
        var ctx = new ActionContext(ActionTrigger.Hotkey);

        // 启动第一个截图动作并挂起
        var task1 = dispatcher.DispatchAsync(action1, ctx);
        await action1Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 尝试启动第二个截图动作（冲突项立即忽略返回）
        var task2 = dispatcher.DispatchAsync(action2, ctx);
        task2.IsCompletedSuccessfully.ShouldBeTrue("截图组冲突调用必须立即完成");
        if (action1 != action2)
        {
            count2.ShouldBe(0);
        }

        // 放行第一个动作
        action1Resume.TrySetResult();
        await task1.WaitAsync(TimeSpan.FromSeconds(5));

        // 第一个动作释放后，第二个动作可成功执行
        if (action1 != action2)
        {
            var task3 = dispatcher.DispatchAsync(action2, ctx);
            await task3.WaitAsync(TimeSpan.FromSeconds(5));
            count2.ShouldBe(1);
        }
    }

    // =========================================================================
    // 场景 5: 两个不同非截图动作，截图与非截图动作（并发无互斥）
    // =========================================================================

    [Fact]
    public async Task DispatchAsync_DifferentNonScreenshotActions_CanRunConcurrently()
    {
        var logger = new TestLogger<ActionDispatcher>();
        var notifications = new TestNotificationService();
        var uiDispatcher = new ImmediateUiDispatcher();

        var started1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var handler1 = new TestHandler(IrisAction.InputTranslate, async (_, _) =>
        {
            started1.TrySetResult();
            await resume1.Task.WaitAsync(TimeSpan.FromSeconds(5));
        });
        var handler2 = new TestHandler(IrisAction.OpenSettings, async (_, _) =>
        {
            started2.TrySetResult();
            await resume2.Task.WaitAsync(TimeSpan.FromSeconds(5));
        });

        var dispatcher = new ActionDispatcher([handler1, handler2], uiDispatcher, notifications, logger);
        var ctx = new ActionContext(ActionTrigger.Hotkey);

        var task1 = dispatcher.DispatchAsync(IrisAction.InputTranslate, ctx);
        await started1.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var task2 = dispatcher.DispatchAsync(IrisAction.OpenSettings, ctx);
        await started2.Task.WaitAsync(TimeSpan.FromSeconds(5));

        task1.IsCompleted.ShouldBeFalse();
        task2.IsCompleted.ShouldBeFalse();

        resume1.TrySetResult();
        resume2.TrySetResult();

        await Task.WhenAll(task1, task2).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DispatchAsync_ScreenshotAndNonScreenshot_CanRunConcurrently()
    {
        var logger = new TestLogger<ActionDispatcher>();
        var notifications = new TestNotificationService();
        var uiDispatcher = new ImmediateUiDispatcher();

        var started1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var handler1 = new TestHandler(IrisAction.ScreenshotTranslate, async (_, _) =>
        {
            started1.TrySetResult();
            await resume1.Task.WaitAsync(TimeSpan.FromSeconds(5));
        });
        var handler2 = new TestHandler(IrisAction.InputTranslate, async (_, _) =>
        {
            started2.TrySetResult();
            await resume2.Task.WaitAsync(TimeSpan.FromSeconds(5));
        });

        var dispatcher = new ActionDispatcher([handler1, handler2], uiDispatcher, notifications, logger);
        var ctx = new ActionContext(ActionTrigger.Hotkey);

        var task1 = dispatcher.DispatchAsync(IrisAction.ScreenshotTranslate, ctx);
        await started1.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var task2 = dispatcher.DispatchAsync(IrisAction.InputTranslate, ctx);
        await started2.Task.WaitAsync(TimeSpan.FromSeconds(5));

        task1.IsCompleted.ShouldBeFalse();
        task2.IsCompleted.ShouldBeFalse();

        resume1.TrySetResult();
        resume2.TrySetResult();

        await Task.WhenAll(task1, task2).WaitAsync(TimeSpan.FromSeconds(5));
    }

    // =========================================================================
    // 场景 6: 入口已取消 / UI 排队后取消
    // =========================================================================

    [Fact]
    public async Task DispatchAsync_EntryAlreadyCancelled_DoesNotInvokeHandlerOrNotify()
    {
        var logger = new TestLogger<ActionDispatcher>();
        var notifications = new TestNotificationService();
        var uiDispatcher = new ImmediateUiDispatcher();

        var executed = false;
        var handler = new TestHandler(IrisAction.InputTranslate, (_, _) =>
        {
            executed = true;
            return Task.CompletedTask;
        });

        var dispatcher = new ActionDispatcher([handler], uiDispatcher, notifications, logger);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await dispatcher.DispatchAsync(IrisAction.InputTranslate, new ActionContext(ActionTrigger.Hotkey), cts.Token);

        executed.ShouldBeFalse();
        notifications.Notifications.ShouldBeEmpty();

        // 之后使用有效 token 可正常执行
        await dispatcher.DispatchAsync(IrisAction.InputTranslate, new ActionContext(ActionTrigger.Hotkey), CancellationToken.None);
        executed.ShouldBeTrue();
    }

    [Fact]
    public async Task DispatchAsync_CancelledDuringUiQueue_DoesNotInvokeHandlerOrNotify_AndReleasesExecutionRight()
    {
        var logger = new TestLogger<ActionDispatcher>();
        var notifications = new TestNotificationService();
        var controllableUi = new ControllableUiDispatcher();

        using var cts = new CancellationTokenSource();
        var executed = false;

        controllableUi.Interceptor = (callback, ct) =>
        {
            // 排队后、UI 回调开始前触发取消
            cts.Cancel();
            throw new OperationCanceledException(ct);
        };

        var handler = new TestHandler(IrisAction.InputTranslate, (_, _) =>
        {
            executed = true;
            return Task.CompletedTask;
        });

        var dispatcher = new ActionDispatcher([handler], controllableUi, notifications, logger);

        await dispatcher.DispatchAsync(IrisAction.InputTranslate, new ActionContext(ActionTrigger.Hotkey), cts.Token);

        executed.ShouldBeFalse();
        notifications.Notifications.ShouldBeEmpty();

        // 原调用结束后可再次触发
        controllableUi.Interceptor = null;
        await dispatcher.DispatchAsync(IrisAction.InputTranslate, new ActionContext(ActionTrigger.Hotkey), CancellationToken.None);
        executed.ShouldBeTrue();
    }

    // =========================================================================
    // 场景 7: Handler 忽略已取消令牌并等门闩
    // =========================================================================

    [Fact]
    public async Task DispatchAsync_HandlerIgnoresCancellationAndWaitsOnLatch_WaitsUntilActualExit()
    {
        var logger = new TestLogger<ActionDispatcher>();
        var notifications = new TestNotificationService();
        var uiDispatcher = new ImmediateUiDispatcher();

        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken capturedToken = default;
        var executionCompleted = false;

        var handler = new TestHandler(IrisAction.InputTranslate, async (_, ct) =>
        {
            capturedToken = ct;
            handlerStarted.TrySetResult();
            await latch.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            executionCompleted = true;
            ct.ThrowIfCancellationRequested();
        });

        var dispatcher = new ActionDispatcher([handler], uiDispatcher, notifications, logger);
        using var cts = new CancellationTokenSource();

        var dispatchTask = dispatcher.DispatchAsync(IrisAction.InputTranslate, new ActionContext(ActionTrigger.Hotkey), cts.Token);
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 触发取消
        cts.Cancel();
        capturedToken.IsCancellationRequested.ShouldBeTrue("取消令牌必须原样传给 Handler");

        // 即使已取消，由于 Handler 在等待门闩，DispatchAsync 仍必须等待其实际退出
        dispatchTask.IsCompleted.ShouldBeFalse("尚未释放门闩前 DispatchAsync 严禁提前完成");

        // 重复调用仍被忽略
        var secondCall = dispatcher.DispatchAsync(IrisAction.InputTranslate, new ActionContext(ActionTrigger.Hotkey));
        secondCall.IsCompletedSuccessfully.ShouldBeTrue();

        // 放行门闩
        latch.TrySetResult();
        await dispatchTask.WaitAsync(TimeSpan.FromSeconds(5));

        executionCompleted.ShouldBeTrue();
        notifications.Notifications.ShouldBeEmpty("OCE 静默，不产生通知");

        // 实际退出后，同动作可再次执行
        var nextExecuted = false;
        var nextHandler = new TestHandler(IrisAction.InputTranslate, (_, _) =>
        {
            nextExecuted = true;
            return Task.CompletedTask;
        });
        var nextDispatcher = new ActionDispatcher([nextHandler], uiDispatcher, notifications, logger);
        await nextDispatcher.DispatchAsync(IrisAction.InputTranslate, new ActionContext(ActionTrigger.Hotkey));
        nextExecuted.ShouldBeTrue();
    }

    // =========================================================================
    // 场景 8: 同步/异步 IrisException、未知异常、OCE
    // =========================================================================

    [Fact]
    public async Task DispatchAsync_SynchronousIrisException_NotifiesKnownDefaultText()
    {
        var logger = new TestLogger<ActionDispatcher>();
        var notifications = new TestNotificationService();
        var uiDispatcher = new ImmediateUiDispatcher();

        var handler = new TestHandler(IrisAction.InputTranslate, (_, _) =>
            throw new IrisException(ErrorCode.Timeout, "sync timeout"));

        var dispatcher = new ActionDispatcher([handler], uiDispatcher, notifications, logger);
        await dispatcher.DispatchAsync(IrisAction.InputTranslate, new ActionContext(ActionTrigger.Hotkey));

        notifications.Notifications.Count.ShouldBe(1);
        notifications.Notifications[0].Level.ShouldBe(NotificationLevel.Error);
        notifications.Notifications[0].Title.ShouldBe(AppResources.AppName);
        notifications.Notifications[0].Message.ShouldBe(ErrorMessages.GetDefault(ErrorCode.Timeout));

        // 下一次调用能执行
        var nextExecuted = false;
        var handlerOk = new TestHandler(IrisAction.InputTranslate, (_, _) =>
        {
            nextExecuted = true;
            return Task.CompletedTask;
        });
        var dispatcherOk = new ActionDispatcher([handlerOk], uiDispatcher, notifications, logger);
        await dispatcherOk.DispatchAsync(IrisAction.InputTranslate, new ActionContext(ActionTrigger.Hotkey));
        nextExecuted.ShouldBeTrue();
    }

    [Fact]
    public async Task DispatchAsync_AsynchronousIrisException_NotifiesKnownDefaultText()
    {
        var logger = new TestLogger<ActionDispatcher>();
        var notifications = new TestNotificationService();
        var uiDispatcher = new ImmediateUiDispatcher();

        var handler = new TestHandler(IrisAction.InputTranslate, async (_, _) =>
        {
            await Task.Yield();
            throw new IrisException(ErrorCode.NetworkUnavailable, "network down");
        });

        var dispatcher = new ActionDispatcher([handler], uiDispatcher, notifications, logger);
        await dispatcher.DispatchAsync(IrisAction.InputTranslate, new ActionContext(ActionTrigger.Hotkey));

        notifications.Notifications.Count.ShouldBe(1);
        notifications.Notifications[0].Level.ShouldBe(NotificationLevel.Error);
        notifications.Notifications[0].Message.ShouldBe(ErrorMessages.GetDefault(ErrorCode.NetworkUnavailable));
    }

    [Fact]
    public async Task DispatchAsync_UnknownException_NotifiesUnknownDefaultText()
    {
        var logger = new TestLogger<ActionDispatcher>();
        var notifications = new TestNotificationService();
        var uiDispatcher = new ImmediateUiDispatcher();

        var handler = new TestHandler(IrisAction.InputTranslate, async (_, _) =>
        {
            await Task.Yield();
            throw new InvalidOperationException("Something unexpected");
        });

        var dispatcher = new ActionDispatcher([handler], uiDispatcher, notifications, logger);
        await dispatcher.DispatchAsync(IrisAction.InputTranslate, new ActionContext(ActionTrigger.Hotkey));

        notifications.Notifications.Count.ShouldBe(1);
        notifications.Notifications[0].Level.ShouldBe(NotificationLevel.Error);
        notifications.Notifications[0].Message.ShouldBe(ErrorMessages.GetDefault(ErrorCode.Unknown));
    }

    [Fact]
    public async Task DispatchAsync_OperationCanceledException_SilentlyExits()
    {
        var logger = new TestLogger<ActionDispatcher>();
        var notifications = new TestNotificationService();
        var uiDispatcher = new ImmediateUiDispatcher();

        var handler = new TestHandler(IrisAction.InputTranslate, async (_, _) =>
        {
            await Task.Yield();
            throw new OperationCanceledException();
        });

        var dispatcher = new ActionDispatcher([handler], uiDispatcher, notifications, logger);
        await dispatcher.DispatchAsync(IrisAction.InputTranslate, new ActionContext(ActionTrigger.Hotkey));

        notifications.Notifications.ShouldBeEmpty("OCE 严禁弹出错误通知");
    }

    // =========================================================================
    // 场景 9: 假 Payload/异常 Message、Detail、Data、InnerException 带假凭据
    // =========================================================================

    [Fact]
    public async Task DispatchAsync_WithFakeSecrets_NeverLeaksIntoLogsOrNotifications()
    {
        const string fakePayload = "FAKE-SECRET-PAYLOAD-ABCD-1234";
        const string fakeMessage = "FAKE-SECRET-MESSAGE-EFGH-5678";
        const string fakeDetail = "FAKE-SECRET-DETAIL-IJKL-9012";
        const string fakeInner = "FAKE-SECRET-INNER-MNOP-3456";
        const string fakeData = "FAKE-SECRET-DATA-QRST-7890";

        var logger = new TestLogger<ActionDispatcher>();
        var notifications = new TestNotificationService();
        var uiDispatcher = new ImmediateUiDispatcher();

        var handler = new TestHandler(IrisAction.InputTranslate, (_, _) =>
        {
            var inner = new InvalidOperationException(fakeInner);
            var ex = new IrisException(ErrorCode.Timeout, fakeMessage, innerException: inner, detail: fakeDetail);
            ex.Data["secret"] = fakeData;
            throw ex;
        });

        var dispatcher = new ActionDispatcher([handler], uiDispatcher, notifications, logger);
        var ctx = new ActionContext(ActionTrigger.Hotkey, Payload: fakePayload);

        await dispatcher.DispatchAsync(IrisAction.InputTranslate, ctx);

        // 校验通知
        notifications.Notifications.Count.ShouldBe(1);
        var notice = notifications.Notifications[0];
        notice.Message.ShouldBe(ErrorMessages.GetDefault(ErrorCode.Timeout));
        notice.Title.ShouldBe(AppResources.AppName);
        notice.Message.ShouldNotContain(fakePayload);
        notice.Message.ShouldNotContain(fakeMessage);
        notice.Message.ShouldNotContain(fakeDetail);
        notice.Message.ShouldNotContain(fakeInner);
        notice.Message.ShouldNotContain(fakeData);

        // 校验日志
        logger.Entries.Count.ShouldBeGreaterThan(0);
        foreach (var entry in logger.Entries)
        {
            // 严禁将 Exception 对象作为日志异常参数传入
            entry.Exception.ShouldBeNull("严禁向 ILogger 传递未脱敏的 Exception 对象");

            // 格式化文本与 state 均不得含有假凭据
            var formatted = entry.FormattedMessage;
            formatted.ShouldNotContain(fakePayload);
            formatted.ShouldNotContain(fakeMessage);
            formatted.ShouldNotContain(fakeDetail);
            formatted.ShouldNotContain(fakeInner);
            formatted.ShouldNotContain(fakeData);

            if (entry.State is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                {
                    var valStr = pair.Value?.ToString() ?? "";
                    valStr.ShouldNotContain(fakePayload);
                    valStr.ShouldNotContain(fakeMessage);
                    valStr.ShouldNotContain(fakeDetail);
                    valStr.ShouldNotContain(fakeInner);
                    valStr.ShouldNotContain(fakeData);
                }
            }
        }
    }

    // =========================================================================
    // 场景 10: 通知服务抛异常 / 调度器抛异常
    // =========================================================================

    [Fact]
    public async Task DispatchAsync_NotificationServiceThrows_DoesNotRecurse_AndReleasesExecutionRight()
    {
        var logger = new TestLogger<ActionDispatcher>();
        var notifications = new TestNotificationService
        {
            OnNotify = () => throw new InvalidOperationException("Notification system crashed")
        };
        var uiDispatcher = new ImmediateUiDispatcher();

        var handler = new TestHandler(IrisAction.InputTranslate, (_, _) =>
            throw new IrisException(ErrorCode.Timeout, "Timeout occurred"));

        var dispatcher = new ActionDispatcher([handler], uiDispatcher, notifications, logger);
        var ctx = new ActionContext(ActionTrigger.Hotkey);

        // 调用不应向外抛出 Notification 异常，不应死循环递归
        await dispatcher.DispatchAsync(IrisAction.InputTranslate, ctx);

        // 执行权被释放，后续调用可正常运行
        var nextExecuted = false;
        var nextHandler = new TestHandler(IrisAction.InputTranslate, (_, _) =>
        {
            nextExecuted = true;
            return Task.CompletedTask;
        });
        notifications.OnNotify = null;
        var nextDispatcher = new ActionDispatcher([nextHandler], uiDispatcher, notifications, logger);
        await nextDispatcher.DispatchAsync(IrisAction.InputTranslate, ctx);
        nextExecuted.ShouldBeTrue();
    }

    [Fact]
    public async Task DispatchAsync_UiDispatcherThrows_HandlesAsUnknownError_AndReleasesExecutionRight()
    {
        var logger = new TestLogger<ActionDispatcher>();
        var notifications = new TestNotificationService();
        var controllableUi = new ControllableUiDispatcher
        {
            Interceptor = (_, _) => throw new InvalidOperationException("UI Dispatch failed completely")
        };

        var handler = new TestHandler(IrisAction.InputTranslate, (_, _) => Task.CompletedTask);
        var dispatcher = new ActionDispatcher([handler], controllableUi, notifications, logger);
        var ctx = new ActionContext(ActionTrigger.Hotkey);

        await dispatcher.DispatchAsync(IrisAction.InputTranslate, ctx);

        // 报告 Unknown 错误
        notifications.Notifications.Count.ShouldBe(1);
        notifications.Notifications[0].Level.ShouldBe(NotificationLevel.Error);
        notifications.Notifications[0].Message.ShouldBe(ErrorMessages.GetDefault(ErrorCode.Unknown));

        // 执行权已释放
        controllableUi.Interceptor = null;
        await dispatcher.DispatchAsync(IrisAction.InputTranslate, ctx);
    }

    // =========================================================================
    // 场景 11: 真实 WPF Dispatcher 的后台和 UI 线程调用
    // =========================================================================

    [Fact]
    public async Task WpfUiDispatcher_RealDispatcher_BackgroundAndUiThreadCalls_ExecuteCorrectly()
    {
        Dispatcher? staDispatcher = null;
        var dispatcherReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var staThread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            staDispatcher = Dispatcher.CurrentDispatcher;
            dispatcherReady.TrySetResult();
            Dispatcher.Run();
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.IsBackground = true;
        staThread.Start();

        try
        {
            await dispatcherReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            staDispatcher.ShouldNotBeNull();

            var wpfUiDispatcher = new WpfUiDispatcher(() => staDispatcher);
            var logger = new TestLogger<ActionDispatcher>();
            var notifications = new TestNotificationService();

            var backgroundThreadId = Environment.CurrentManagedThreadId;
            int? handlerThreadId = null;
            bool? handlerHasAccess = null;
            var innerTaskCompleted = false;

            var handler = new TestHandler(IrisAction.InputTranslate, async (_, _) =>
            {
                handlerThreadId = Environment.CurrentManagedThreadId;
                handlerHasAccess = staDispatcher.CheckAccess();
                await Task.Delay(50);
                innerTaskCompleted = true;
            });

            var dispatcher = new ActionDispatcher([handler], wpfUiDispatcher, notifications, logger);
            var ctx = new ActionContext(ActionTrigger.Hotkey);

            // 从后台线程调用：断言 Handler 启动时在 STA 线程，且内部 Task 完整等待
            await dispatcher.DispatchAsync(IrisAction.InputTranslate, ctx).WaitAsync(TimeSpan.FromSeconds(5));

            handlerHasAccess.ShouldBe(true, "Handler 必须在 UI 线程启动");
            handlerThreadId.ShouldNotBe(backgroundThreadId, "后台调用应调度至独立 STA 线程");
            innerTaskCompleted.ShouldBeTrue("DispatchAsync 必须等待内部 Task 完成");

            // 从 UI 线程直接调用
            var uiCallRan = false;
            var uiHandler = new TestHandler(IrisAction.OpenSettings, async (_, _) =>
            {
                staDispatcher.CheckAccess().ShouldBeTrue();
                await Task.Delay(20);
                uiCallRan = true;
            });
            var uiDispatcherInstance = new ActionDispatcher([uiHandler], wpfUiDispatcher, notifications, logger);

            await staDispatcher.InvokeAsync(async () =>
            {
                await uiDispatcherInstance.DispatchAsync(IrisAction.OpenSettings, ctx);
            }).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(5));

            uiCallRan.ShouldBeTrue();
        }
        finally
        {
            if (staDispatcher != null && !staDispatcher.HasShutdownStarted)
            {
                staDispatcher.InvokeShutdown();
            }
            staThread.Join(5000);
        }
    }

    // =========================================================================
    // 场景 12: 真实 Dispatcher 关闭 / 解析器返回 null
    // =========================================================================

    [Fact]
    public async Task WpfUiDispatcher_WhenDispatcherShutdown_ExitsSilentlyAsCancellation()
    {
        Dispatcher? staDispatcher = null;
        var dispatcherReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var staThread = new Thread(() =>
        {
            staDispatcher = Dispatcher.CurrentDispatcher;
            dispatcherReady.TrySetResult();
            Dispatcher.Run();
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.IsBackground = true;
        staThread.Start();

        try
        {
            await dispatcherReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            staDispatcher.ShouldNotBeNull();

            // 关闭 Dispatcher
            staDispatcher.InvokeShutdown();
            staThread.Join(5000);

            var wpfUiDispatcher = new WpfUiDispatcher(() => staDispatcher);
            var logger = new TestLogger<ActionDispatcher>();
            var notifications = new TestNotificationService();

            var handler = new TestHandler(IrisAction.InputTranslate, (_, _) => Task.CompletedTask);
            var dispatcher = new ActionDispatcher([handler], wpfUiDispatcher, notifications, logger);

            // 关闭状态下 Dispatch 应静默退出
            await dispatcher.DispatchAsync(IrisAction.InputTranslate, new ActionContext(ActionTrigger.Hotkey)).WaitAsync(TimeSpan.FromSeconds(5));

            notifications.Notifications.ShouldBeEmpty("Dispatcher 关闭时静默，不弹错误通知");
        }
        finally
        {
            if (staDispatcher != null && !staDispatcher.HasShutdownStarted)
            {
                staDispatcher.InvokeShutdown();
            }
            staThread.Join(1000);
        }
    }

    [Fact]
    public async Task WpfUiDispatcher_WhenResolverReturnsNull_HandlesAsUnknownErrorAndReleasesRight()
    {
        var wpfUiDispatcher = new WpfUiDispatcher(() => null);
        var logger = new TestLogger<ActionDispatcher>();
        var notifications = new TestNotificationService();

        var handler = new TestHandler(IrisAction.InputTranslate, (_, _) => Task.CompletedTask);
        var dispatcher = new ActionDispatcher([handler], wpfUiDispatcher, notifications, logger);

        await dispatcher.DispatchAsync(IrisAction.InputTranslate, new ActionContext(ActionTrigger.Hotkey)).WaitAsync(TimeSpan.FromSeconds(5));

        // 缺少 Dispatcher 按未知调度失败处理
        notifications.Notifications.Count.ShouldBe(1);
        notifications.Notifications[0].Level.ShouldBe(NotificationLevel.Error);
        notifications.Notifications[0].Message.ShouldBe(ErrorMessages.GetDefault(ErrorCode.Unknown));
    }

    // =========================================================================
    // 测试辅助替身
    // =========================================================================

    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public async Task InvokeAsync(Func<Task> callback, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(callback);
            cancellationToken.ThrowIfCancellationRequested();
            await callback().ConfigureAwait(false);
        }
    }

    private sealed class ControllableUiDispatcher : IUiDispatcher
    {
        public Func<Func<Task>, CancellationToken, Task>? Interceptor { get; set; }

        public bool CheckAccess() => true;

        public async Task InvokeAsync(Func<Task> callback, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(callback);
            if (Interceptor != null)
            {
                await Interceptor(callback, cancellationToken).ConfigureAwait(false);
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await callback().ConfigureAwait(false);
        }
    }

    private sealed class TestHandler : IActionHandler
    {
        public IrisAction Action { get; }
        private readonly Func<ActionContext, CancellationToken, Task> _implementation;

        public TestHandler(IrisAction action, Func<ActionContext, CancellationToken, Task> implementation)
        {
            Action = action;
            _implementation = implementation ?? throw new ArgumentNullException(nameof(implementation));
        }

        public Task ExecuteAsync(ActionContext context, CancellationToken cancellationToken) =>
            _implementation(context, cancellationToken);
    }

    private sealed class TestNotificationService : INotificationService
    {
        public List<NotificationItem> Notifications { get; } = [];
        public Action? OnNotify { get; set; }

        public void Notify(NotificationLevel level, string title, string message)
        {
            OnNotify?.Invoke();
            Notifications.Add(new NotificationItem(level, title, message));
        }
    }

    private sealed record NotificationItem(NotificationLevel Level, string Title, string Message);

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            Entries.Add(new LogEntry(logLevel, eventId, state, exception, message));
        }
    }

    private sealed record LogEntry(LogLevel Level, EventId EventId, object? State, Exception? Exception, string FormattedMessage);
}
