using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Tests.TestSupport;
using Iris.App.Translation;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Languages;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Translation;

public sealed class TranslateWindowViewModelTests
{
    private static (TranslateWindowViewModel ViewModel, ITranslationOrchestrator Orchestrator, IClipboardService Clipboard, ISettingsService SettingsService) CreateViewModel(
        AppSettings? settings = null,
        IUiDispatcher? uiDispatcher = null,
        ITextPreprocessor? textPreprocessor = null,
        ILogger<TranslateWindowViewModel>? logger = null,
        IClipboardService? clipboard = null)
    {
        var appSettings = settings ?? new AppSettings
        {
            Translation = new TranslationSettings
            {
                AutoCopy = AutoCopyMode.FirstResult,
                MaxInputChars = 5000
            }
        };

        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(appSettings);

        var orchestrator = Substitute.For<ITranslationOrchestrator>();
        var cb = clipboard ?? Substitute.For<IClipboardService>();
        var settingsPresenter = Substitute.For<ISettingsWindowPresenter>();
        var dispatcher = uiDispatcher ?? new ImmediateUiDispatcher();

        var preprocessor = textPreprocessor ?? Substitute.For<ITextPreprocessor>();
        if (textPreprocessor == null)
        {
            preprocessor.Prepare(Arg.Any<string>(), Arg.Any<TextOrigin>())
                .Returns(ci => ci.Arg<string>()?.Trim() ?? string.Empty);
        }

        var vm = new TranslateWindowViewModel(
            settingsService,
            orchestrator,
            cb,
            settingsPresenter,
            dispatcher,
            logger ?? NullLogger<TranslateWindowViewModel>.Instance,
            preprocessor);

        return (vm, orchestrator, cb, settingsService);
    }

    [Fact]
    public async Task MainTranslation_CreatesCardsInDescriptorOrder_StreamsAndCompletes()
    {
        var (vm, orchestrator, clipboard, _) = CreateViewModel();

        var opId = Guid.NewGuid();
        var reqId = Guid.NewGuid();
        var presenter = new TranslateWindowPresenter(
            Substitute.For<IServiceProvider>(),
            new ImmediateUiDispatcher(),
            Substitute.For<ISettingsService>(),
            Substitute.For<IDesktopContext>(),
            NullLogger<TranslateWindowPresenter>.Instance);

        var op = new TranslateWindowOperation(presenter, CancellationToken.None);

        async IAsyncEnumerable<TranslationEvent> MockStream()
        {
            var p1 = new ProviderDescriptor("p1", "Provider One", "OpenAICompatible", "gpt-4o", true);
            var p2 = new ProviderDescriptor("p2", "Provider Two", "OpenAICompatible", "claude-3-5", true);
            yield return new TranslationStarted(reqId, new ResolvedLanguagePair("en", "zh-Hans", false), [p1, p2]);

            yield return new ProviderStarted(reqId, "p1");
            yield return new ProviderDelta(reqId, "p1", "Hello ");
            yield return new ProviderDelta(reqId, "p1", "World");
            yield return new ProviderCompleted(reqId, "p1", "权威完整翻译：你好世界", TimeSpan.FromMilliseconds(300), false, false, null);

            yield return new ProviderStarted(reqId, "p2");
            yield return new ProviderCompleted(reqId, "p2", "权威翻译2", TimeSpan.FromMilliseconds(450), true, false, null);

            yield return new TranslationFinished(reqId, 2, 0);
        }

        orchestrator.TranslateAsync(Arg.Any<TranslationRequest>(), Arg.Any<CancellationToken>())
            .Returns(MockStream());

        vm.ApplyRequest(new TranslateWindowRequest { Text = "Hello World", AutoTranslate = true }, op);

        await vm.WaitForActiveWorkAsync();

        // 验证卡片按 descriptor 顺序创建
        vm.Cards.Count.ShouldBe(2);
        vm.Cards[0].ProviderId.ShouldBe("p1");
        vm.Cards[1].ProviderId.ShouldBe("p2");

        // 验证 Completed 用权威文本覆盖 Delta
        vm.Cards[0].Text.ShouldBe("权威完整翻译：你好世界");
        vm.Cards[0].State.ShouldBe(CardState.Completed);

        // 验证首个完整结果触发自动复制，第二个结果不重复复制
        await clipboard.Received(1).SetTextAsync("权威完整翻译：你好世界", Arg.Any<CancellationToken>());
        await clipboard.DidNotReceive().SetTextAsync("权威翻译2", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void TextBudget_SurrogateSafeTruncation_PreventsOrphanSurrogates()
    {
        // 含有高代理项与低代理项的 Emoji (如 😊: \uD83D\uDE0A，长度为 2)
        string emoji = "\uD83D\uDE0A";
        string sample = "ABC" + emoji; // 长度 5: 'A', 'B', 'C', '\uD83D', '\uDE0A'

        // 如果在 index 4 截断，刚好卡在高代理项 '\uD83D' 之后，必须回退至 3
        string truncated = TextBudgetManager.TruncateSurrogateSafe(sample, 4);
        truncated.ShouldBe("ABC");

        // 如果在 index 5 截断，包含完整代理对
        string fullEmoji = TextBudgetManager.TruncateSurrogateSafe(sample, 5);
        fullEmoji.ShouldBe("ABC" + emoji);
    }

    [Fact]
    public async Task SingleCardRetry_WhenACompletedAndBStreaming_RetriesAWithoutAffectingB()
    {
        var (vm, orchestrator, clipboard, _) = CreateViewModel();

        var presenter = new TranslateWindowPresenter(
            Substitute.For<IServiceProvider>(),
            new ImmediateUiDispatcher(),
            Substitute.For<ISettingsService>(),
            Substitute.For<IDesktopContext>(),
            NullLogger<TranslateWindowPresenter>.Instance);

        var op = new TranslateWindowOperation(presenter, CancellationToken.None);
        var reqId = Guid.NewGuid();

        var bStreamingGate = new TaskCompletionSource();

        async IAsyncEnumerable<TranslationEvent> MainStream()
        {
            var p1 = new ProviderDescriptor("p1", "P1", "Kind", null, true);
            var p2 = new ProviderDescriptor("p2", "P2", "Kind", null, true);
            yield return new TranslationStarted(reqId, new ResolvedLanguagePair("en", "zh-Hans", false), [p1, p2]);

            // p1 很快完成
            yield return new ProviderStarted(reqId, "p1");
            yield return new ProviderCompleted(reqId, "p1", "P1 First Result", TimeSpan.FromMilliseconds(100), false, false, null);

            // p2 处于流式中
            yield return new ProviderStarted(reqId, "p2");
            yield return new ProviderDelta(reqId, "p2", "P2 stream part 1...");

            // 等待闸门通知
            await bStreamingGate.Task;
            yield return new ProviderCompleted(reqId, "p2", "P2 stream finished", TimeSpan.FromMilliseconds(800), false, false, null);
        }

        orchestrator.TranslateAsync(Arg.Is<TranslationRequest>(r => r.ProviderIds == null), Arg.Any<CancellationToken>())
            .Returns(MainStream());

        async IAsyncEnumerable<TranslationEvent> RetryStream(Guid retryReqId)
        {
            yield return new ProviderStarted(retryReqId, "p1");
            yield return new ProviderCompleted(retryReqId, "p1", "P1 Retried Text", TimeSpan.FromMilliseconds(200), false, false, null);
        }

        orchestrator.TranslateAsync(Arg.Is<TranslationRequest>(r => r.ProviderIds != null && r.ProviderIds.Contains("p1")), Arg.Any<CancellationToken>())
            .Returns(call => RetryStream(call.Arg<TranslationRequest>().RequestId));

        try
        {
            vm.ApplyRequest(new TranslateWindowRequest { Text = "Test Input", AutoTranslate = true }, op);

            // 等待 P1 完成、P2 开始流式
            while (vm.Cards.Count < 2 || vm.Cards[0].State != CardState.Completed || vm.Cards[1].State != CardState.Streaming)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            vm.Cards[0].State.ShouldBe(CardState.Completed);
            vm.Cards[1].State.ShouldBe(CardState.Streaming);

            // 重试 P1（在 B 处于流式时断言 CanExecute 为 true）
            vm.Cards[0].RetryCommand.CanExecute(null).ShouldBeTrue();
            vm.Cards[0].RetryCommand.Execute(null);

            // P1 重置为 Streaming/更新文本，而 P2 依然在流式运行
            vm.Cards[1].State.ShouldBe(CardState.Streaming);

            // 解除 P2 流式闸门
            bStreamingGate.SetResult();
            await vm.WaitForActiveWorkAsync();

            // 最终 P1 与 P2 均成功独立收尾
            vm.Cards[0].Text.ShouldBe("P1 Retried Text");
            vm.Cards[1].Text.ShouldBe("P2 stream finished");

            // 验证自动复制仅由初始主请求的首个完整结果触发一次，重试不参与自动复制
            await clipboard.Received(1).SetTextAsync("P1 First Result", Arg.Any<CancellationToken>());
            await clipboard.DidNotReceive().SetTextAsync("P1 Retried Text", Arg.Any<CancellationToken>());
        }
        finally
        {
            bStreamingGate.TrySetResult();
        }
    }

    [Fact]
    public async Task MainTranslation_EofDisposeAsyncAndUiFinallyGate_HoldsOperationUntilReleased()
    {
        var disposeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uiFinallyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uiFinallyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var gatingDispatcher = Substitute.For<IUiDispatcher>();
        gatingDispatcher.InvokeAsync(Arg.Any<Func<Task>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var callback = call.Arg<Func<Task>>();
                if (disposeStarted.Task.IsCompleted)
                {
                    uiFinallyStarted.TrySetResult();
                    await uiFinallyGate.Task;
                }
                await callback();
            });

        var (vm, orchestrator, _, _) = CreateViewModel(uiDispatcher: gatingDispatcher);

        var presenter = new TranslateWindowPresenter(
            Substitute.For<IServiceProvider>(),
            gatingDispatcher,
            Substitute.For<ISettingsService>(),
            Substitute.For<IDesktopContext>(),
            NullLogger<TranslateWindowPresenter>.Instance);

        var op = (TranslateWindowOperation)presenter.TryBeginOperation(CancellationToken.None)!;
        var reqId = Guid.NewGuid();

        var stream = new BlockingDisposeAsyncEnumerable(reqId, disposeStarted, disposeGate);
        orchestrator.TranslateAsync(Arg.Any<TranslationRequest>(), Arg.Any<CancellationToken>())
            .Returns(stream);

        try
        {
            vm.ApplyRequest(new TranslateWindowRequest { Text = "Test Stream", AutoTranslate = true }, op!);

            // 调用方模拟展示完成，立即释放前置调用方租约
            op!.Dispose();

            // 1. 等待流执行完 EOF 并进入 DisposeAsync 阶段
            await disposeStarted.Task;

            // 闸门 1：DisposeAsync 处于挂起阻塞中，操作租约由于活跃工作未结束，必须保留且不得提前退休
            op.IsRetired.ShouldBeFalse();
            op.ActiveWorkCount.ShouldBeGreaterThan(0);

            // 放行 DisposeAsync 闸门
            disposeGate.SetResult();

            // 2. 等待进入末尾 UI finally 阶段
            await uiFinallyStarted.Task;

            // 闸门 2：UI finally 处于挂起阻塞中，操作租约仍在保护中，不得提前退休
            op.IsRetired.ShouldBeFalse();
            op.ActiveWorkCount.ShouldBeGreaterThan(0);

            // 放行 UI finally 闸门
            uiFinallyGate.SetResult();

            // 等待 VM 全部工作完成
            await vm.WaitForActiveWorkAsync();

            // 活跃工作已结束，但 session 所有权仍保留在当前会话中供后续重试
            op.ActiveWorkCount.ShouldBe(0);
            op.IsRetired.ShouldBeFalse();

            // 模拟窗口隐藏释放会话所有权
            vm.OnWindowHidden();

            // 最终所有持有者均已释放，租约完全退休
            op.IsRetired.ShouldBeTrue();
        }
        finally
        {
            disposeGate.TrySetResult();
            uiFinallyGate.TrySetResult();
        }
    }

    [Fact]
    public async Task RetryTranslation_OtherCardReplacedInPendingQueue_SetsPreviousPendingToCanceled()
    {
        var (vm, orchestrator, _, _) = CreateViewModel();

        var presenter = new TranslateWindowPresenter(
            Substitute.For<IServiceProvider>(),
            new ImmediateUiDispatcher(),
            Substitute.For<ISettingsService>(),
            Substitute.For<IDesktopContext>(),
            NullLogger<TranslateWindowPresenter>.Instance);

        var op = (TranslateWindowOperation)presenter.TryBeginOperation(CancellationToken.None)!;
        var reqId = Guid.NewGuid();

        // 初始翻译创建 3 张卡片并完成
        async IAsyncEnumerable<TranslationEvent> MainStream()
        {
            var p1 = new ProviderDescriptor("p1", "P1", "Kind", null, true);
            var p2 = new ProviderDescriptor("p2", "P2", "Kind", null, true);
            var p3 = new ProviderDescriptor("p3", "P3", "Kind", null, true);
            yield return new TranslationStarted(reqId, new ResolvedLanguagePair("en", "zh-Hans", false), [p1, p2, p3]);
            yield return new ProviderCompleted(reqId, "p1", "P1", TimeSpan.Zero, false, false, null);
            yield return new ProviderCompleted(reqId, "p2", "P2", TimeSpan.Zero, false, false, null);
            yield return new ProviderCompleted(reqId, "p3", "P3", TimeSpan.Zero, false, false, null);
        }

        orchestrator.TranslateAsync(Arg.Is<TranslationRequest>(r => r.ProviderIds == null), Arg.Any<CancellationToken>())
            .Returns(MainStream());

        var p1RetryStarted = new TaskCompletionSource();
        var p1RetryGate = new TaskCompletionSource();

        async IAsyncEnumerable<TranslationEvent> P1RetryStream(Guid rId)
        {
            yield return new ProviderStarted(rId, "p1");
            p1RetryStarted.TrySetResult();
            await p1RetryGate.Task;
            yield return new ProviderCompleted(rId, "p1", "P1 Retried", TimeSpan.Zero, false, false, null);
        }

        async IAsyncEnumerable<TranslationEvent> P3RetryStream(Guid rId)
        {
            yield return new ProviderStarted(rId, "p3");
            yield return new ProviderCompleted(rId, "p3", "P3 Retried", TimeSpan.Zero, false, false, null);
        }

        orchestrator.TranslateAsync(Arg.Is<TranslationRequest>(r => r.ProviderIds != null && r.ProviderIds.Contains("p1")), Arg.Any<CancellationToken>())
            .Returns(call => P1RetryStream(call.Arg<TranslationRequest>().RequestId));

        orchestrator.TranslateAsync(Arg.Is<TranslationRequest>(r => r.ProviderIds != null && r.ProviderIds.Contains("p3")), Arg.Any<CancellationToken>())
            .Returns(call => P3RetryStream(call.Arg<TranslationRequest>().RequestId));

        try
        {
            vm.ApplyRequest(new TranslateWindowRequest { Text = "Test", AutoTranslate = true }, op!);
            await vm.WaitForActiveWorkAsync();

            vm.Cards.Count.ShouldBe(3);
            vm.Cards[0].State.ShouldBe(CardState.Completed);
            vm.Cards[1].State.ShouldBe(CardState.Completed);
            vm.Cards[2].State.ShouldBe(CardState.Completed);

            // 1. 触发 P1 重试并等待其进入执行（阻塞在 p1RetryGate）
            vm.Cards[0].RetryCommand.Execute(null);
            await p1RetryStarted.Task;

            // 2. 在 P1 占有活跃重试槽位时，用户点击 P2 重试：P2 进入 _pendingRetry 等待队列
            vm.Cards[1].RetryCommand.Execute(null);
            vm.Cards[1].State.ShouldBe(CardState.Streaming);

            // 3. 在 P1 仍未结束、P2 仍在等待队列时，用户又点击了 P3 重试：
            // 异卡替换：等待中的 P2 必须被置为 Canceled，避免遗留为永久 Streaming 的废弃卡片
            vm.Cards[2].RetryCommand.Execute(null);
            vm.Cards[1].State.ShouldBe(CardState.Canceled);
            vm.Cards[2].State.ShouldBe(CardState.Streaming);

            // 4. 放行 P1 重试
            p1RetryGate.SetResult();
            await vm.WaitForActiveWorkAsync();

            // 5. 验证最终状态：P1 完成，被替换的 P2 保持 Canceled，后来的 P3 成功重试完成
            vm.Cards[0].State.ShouldBe(CardState.Completed);
            vm.Cards[0].Text.ShouldBe("P1 Retried");
            vm.Cards[1].State.ShouldBe(CardState.Canceled);
            vm.Cards[2].State.ShouldBe(CardState.Completed);
            vm.Cards[2].Text.ShouldBe("P3 Retried");
        }
        finally
        {
            p1RetryGate.TrySetResult();
        }
    }

    [Fact]
    public async Task TextBudget_CrossCardAndCrossRetry_EnforcesMaxTotalCharsAndTrimsSurrogateSafe()
    {
        var (vm, orchestrator, clipboard, _) = CreateViewModel();

        var presenter = new TranslateWindowPresenter(
            Substitute.For<IServiceProvider>(),
            new ImmediateUiDispatcher(),
            Substitute.For<ISettingsService>(),
            Substitute.For<IDesktopContext>(),
            NullLogger<TranslateWindowPresenter>.Instance);

        var op = (TranslateWindowOperation)presenter.TryBeginOperation(CancellationToken.None)!;
        var reqId = Guid.NewGuid();

        // 构造 5 张卡片：
        // P1: 1,000,000 字符
        // P2: 1,000,000 字符
        // P3: 1,000,000 字符
        // P4: 1,000,000 字符
        // 前 4 张合计 4,000,000 字符。
        // 全局总预算 MaxTotalChars = 4,194,304。
        // 剩余全局总预算 = 4,194,304 - 4,000,000 = 194,304。
        // 单卡上限 MaxCardChars = 1,048,576。
        // 因 194,304 < 1,048,576，全局总预算成为决定性约束！
        int p1Len = 1_000_000;
        int p2Len = 1_000_000;
        int p3Len = 1_000_000;
        int p4Len = 1_000_000;
        string p1Text = new('A', p1Len);
        string p2Text = new('B', p2Len);
        string p3Text = new('C', p3Len);
        string p4Text = new('D', p4Len);

        // 构造 P5 文本：长度 194,305，刚好在全局允许边界 194,304 上卡在高代理项 '\uD83D'
        string emoji = "\uD83D\uDE0A";
        string p5Base = new('E', 194_303); // 长度 194,303
        string p5Text = p5Base + emoji;       // 长度 194,305。索引 194,303 是 '\uD83D'，索引 194,304 是 '\uDE0A'

        async IAsyncEnumerable<TranslationEvent> MainStream()
        {
            var p1 = new ProviderDescriptor("p1", "P1", "Kind", null, true);
            var p2 = new ProviderDescriptor("p2", "P2", "Kind", null, true);
            var p3 = new ProviderDescriptor("p3", "P3", "Kind", null, true);
            var p4 = new ProviderDescriptor("p4", "P4", "Kind", null, true);
            var p5 = new ProviderDescriptor("p5", "P5", "Kind", null, true);
            yield return new TranslationStarted(reqId, new ResolvedLanguagePair("en", "zh-Hans", false), [p1, p2, p3, p4, p5]);
            yield return new ProviderCompleted(reqId, "p1", p1Text, TimeSpan.Zero, false, false, null);
            yield return new ProviderCompleted(reqId, "p2", p2Text, TimeSpan.Zero, false, false, null);
            yield return new ProviderCompleted(reqId, "p3", p3Text, TimeSpan.Zero, false, false, null);
            yield return new ProviderCompleted(reqId, "p4", p4Text, TimeSpan.Zero, false, false, null);
            yield return new ProviderCompleted(reqId, "p5", p5Text, TimeSpan.Zero, false, false, null);
        }

        orchestrator.TranslateAsync(Arg.Is<TranslationRequest>(r => r.ProviderIds == null), Arg.Any<CancellationToken>())
            .Returns(MainStream());

        vm.ApplyRequest(new TranslateWindowRequest { Text = "Test", AutoTranslate = true }, op!);
        await vm.WaitForActiveWorkAsync();

        vm.Cards.Count.ShouldBe(5);
        vm.Cards[0].IsLocallyTruncated.ShouldBeFalse();
        vm.Cards[1].IsLocallyTruncated.ShouldBeFalse();
        vm.Cards[2].IsLocallyTruncated.ShouldBeFalse();
        vm.Cards[3].IsLocallyTruncated.ShouldBeFalse();

        // P5 受全局总预算 (194,304 < MaxCardChars) 决定性截断，并安全规避孤立高代理项（长度回退至 194,303）
        vm.Cards[4].IsLocallyTruncated.ShouldBeTrue();
        vm.Cards[4].Text.Length.ShouldBe(194_303);
        vm.Cards[4].Text.ShouldBe(p5Base);
        vm.Cards[4].CopyTooltip.ShouldBe(UiText.TranslateCopyVisible);

        // 跨重试预算：重试 P1，返回 2,000,000 字符超长文本
        // 其它卡片占用：P2(1,000,000) + P3(1,000,000) + P4(1,000,000) + P5(194,303) = 3,194,303
        // 剩余全局总预算 = 4,194,304 - 3,194,303 = 1,000,001
        // 单卡上限 = 1,048,576
        // 因 1,000,001 < 1,048,576，全局总预算再次成为决定性约束！
        async IAsyncEnumerable<TranslationEvent> P1RetryStream(Guid retryId)
        {
            yield return new ProviderStarted(retryId, "p1");
            yield return new ProviderCompleted(retryId, "p1", new string('Z', 2_000_000), TimeSpan.Zero, false, false, null);
        }

        orchestrator.TranslateAsync(Arg.Is<TranslationRequest>(r => r.ProviderIds != null && r.ProviderIds.Contains("p1")), Arg.Any<CancellationToken>())
            .Returns(call => P1RetryStream(call.Arg<TranslationRequest>().RequestId));

        vm.Cards[0].RetryCommand.Execute(null);
        await vm.WaitForActiveWorkAsync();

        vm.Cards[0].IsLocallyTruncated.ShouldBeTrue();
        vm.Cards[0].Text.Length.ShouldBe(1_000_001, "跨重试时全局剩余总预算 1,000,001 < MaxCardChars 成为截断决定因素");
        vm.Cards[0].CopyTooltip.ShouldBe(UiText.TranslateCopyVisible);
    }

    [Fact]
    public async Task CopyQueue_MultipleConcurrentRequests_OnlyOneInFlight_CoalescesPending_AndHideCancelsAll()
    {
        var (vm, orchestrator, clipboard, _) = CreateViewModel();

        var presenter = new TranslateWindowPresenter(
            Substitute.For<IServiceProvider>(),
            new ImmediateUiDispatcher(),
            Substitute.For<ISettingsService>(),
            Substitute.For<IDesktopContext>(),
            NullLogger<TranslateWindowPresenter>.Instance);

        var op = (TranslateWindowOperation)presenter.TryBeginOperation(CancellationToken.None)!;
        var reqId = Guid.NewGuid();

        async IAsyncEnumerable<TranslationEvent> MainStream()
        {
            var p1 = new ProviderDescriptor("p1", "P1", "Kind", null, true);
            var p2 = new ProviderDescriptor("p2", "P2", "Kind", null, true);
            var p3 = new ProviderDescriptor("p3", "P3", "Kind", null, true);
            yield return new TranslationStarted(reqId, new ResolvedLanguagePair("en", "zh-Hans", false), [p1, p2, p3]);
            yield return new ProviderCompleted(reqId, "p1", "Text1", TimeSpan.Zero, false, false, null);
            yield return new ProviderCompleted(reqId, "p2", "Text2", TimeSpan.Zero, false, false, null);
            yield return new ProviderCompleted(reqId, "p3", "Text3", TimeSpan.Zero, false, false, null);
        }

        orchestrator.TranslateAsync(Arg.Any<TranslationRequest>(), Arg.Any<CancellationToken>())
            .Returns(MainStream());

        vm.ApplyRequest(new TranslateWindowRequest { Text = "Test", AutoTranslate = true }, op!);
        await vm.WaitForActiveWorkAsync();

        // 此时自动复制已经触发 1 次 ("Text1")
        await clipboard.Received(1).SetTextAsync("Text1", Arg.Any<CancellationToken>());
        clipboard.ClearReceivedCalls();

        // 现在模拟手动点击复制队列控制：
        // 第一个在途写入阻塞在 inFlightGate
        var inFlightStarted = new TaskCompletionSource();
        var inFlightGate = new TaskCompletionSource();
        var executedTokens = new List<CancellationToken>();

        clipboard.SetTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                executedTokens.Add(call.Arg<CancellationToken>());
                inFlightStarted.TrySetResult();
                return inFlightGate.Task;
            });

        try
        {
            // 1. 点击 Card 1 复制：启动在途写入
            vm.Cards[0].CopyCommand.Execute(null);
            await inFlightStarted.Task;

            // 2. 在在途写入未完成时，连续点击 Card 2 与 Card 3
            vm.Cards[1].CopyCommand.Execute(null); // 进入 _pendingCopy
            vm.Cards[2].CopyCommand.Execute(null); // 取消 Card 2，替换为 Card 3

            // 3. 此时调用 OnWindowHidden() 模拟隐藏窗口
            // 隐藏窗口必须取消在途写入与待办写入
            vm.OnWindowHidden();

            // 4. 放行在途写入闸门
            inFlightGate.SetResult();
            await vm.WaitForActiveWorkAsync();

            // 5. 验证：
            // - 在途写入的 CancellationToken 被取消
            executedTokens.Count.ShouldBe(1);
            executedTokens[0].IsCancellationRequested.ShouldBeTrue();

            // - 剪贴板服务仅被调用了 1 次（Card 2 被合并取消，Card 3 尚未开始即被隐藏取消）
            await clipboard.Received(1).SetTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            inFlightGate.TrySetResult();
        }
    }

    [Fact]
    public void SwapLanguages_SwapsExplicitOrResolvedLanguages()
    {
        var (vm, _, _, _) = CreateViewModel();

        vm.SourceLanguage = "en";
        vm.TargetLanguage = "zh-Hans";
        vm.CanSwapLanguages.ShouldBeTrue();

        vm.SwapLanguagesCommand.Execute(null);

        vm.SourceLanguage.ShouldBe("zh-Hans");
        vm.TargetLanguage.ShouldBe("en");
    }

    private sealed class BlockingDisposeAsyncEnumerable : IAsyncEnumerable<TranslationEvent>
    {
        private readonly Guid _reqId;
        private readonly TaskCompletionSource _disposeStarted;
        private readonly TaskCompletionSource _disposeGate;

        public BlockingDisposeAsyncEnumerable(Guid reqId, TaskCompletionSource disposeStarted, TaskCompletionSource disposeGate)
        {
            _reqId = reqId;
            _disposeStarted = disposeStarted;
            _disposeGate = disposeGate;
        }

        public IAsyncEnumerator<TranslationEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            return new BlockingEnumerator(_reqId, _disposeStarted, _disposeGate);
        }

        private sealed class BlockingEnumerator : IAsyncEnumerator<TranslationEvent>
        {
            private readonly Guid _reqId;
            private readonly TaskCompletionSource _disposeStarted;
            private readonly TaskCompletionSource _disposeGate;
            private int _state;

            public BlockingEnumerator(Guid reqId, TaskCompletionSource disposeStarted, TaskCompletionSource disposeGate)
            {
                _reqId = reqId;
                _disposeStarted = disposeStarted;
                _disposeGate = disposeGate;
            }

            public TranslationEvent Current { get; private set; } = null!;

            public ValueTask<bool> MoveNextAsync()
            {
                if (_state == 0)
                {
                    _state = 1;
                    var p1 = new ProviderDescriptor("p1", "P1", "Kind", null, true);
                    Current = new TranslationStarted(_reqId, new ResolvedLanguagePair("en", "zh-Hans", false), [p1]);
                    return ValueTask.FromResult(true);
                }
                if (_state == 1)
                {
                    _state = 2;
                    Current = new ProviderStarted(_reqId, "p1");
                    return ValueTask.FromResult(true);
                }
                if (_state == 2)
                {
                    _state = 3;
                    Current = new ProviderCompleted(_reqId, "p1", "Result", TimeSpan.FromMilliseconds(50), false, false, null);
                    return ValueTask.FromResult(true);
                }
                return ValueTask.FromResult(false); // EOF
            }

            public async ValueTask DisposeAsync()
            {
                _disposeStarted.TrySetResult();
                await _disposeGate.Task;
            }
        }
    }

    [Fact]
    public async Task MainTranslation_FirstUiDispatchThrows_DecrementsWorkAndReclaimsOperation()
    {
        int invokeCount = 0;
        var throwingDispatcher = Substitute.For<IUiDispatcher>();
        throwingDispatcher.InvokeAsync(Arg.Any<Func<Task>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                if (Interlocked.Increment(ref invokeCount) == 1)
                {
                    throw new InvalidOperationException("First UI dispatch failed");
                }
                await call.Arg<Func<Task>>()();
            });

        var (vm, orchestrator, _, _) = CreateViewModel(uiDispatcher: throwingDispatcher);
        var presenter = new TranslateWindowPresenter(
            Substitute.For<IServiceProvider>(),
            throwingDispatcher,
            Substitute.For<ISettingsService>(),
            Substitute.For<IDesktopContext>(),
            NullLogger<TranslateWindowPresenter>.Instance);

        var op = (TranslateWindowOperation)presenter.TryBeginOperation(CancellationToken.None)!;

        vm.ApplyRequest(new TranslateWindowRequest { Text = "Test", AutoTranslate = true }, op);
        op.Dispose();

        await vm.WaitForActiveWorkAsync();

        op.ActiveWorkCount.ShouldBe(0, "首个 UI 调度失败后，ActiveWorkCount 必须在 finally 中正常清零");

        vm.OnWindowHidden();
        op.IsRetired.ShouldBeTrue("首个 UI 调度失败且调用方释放后，操作必须能正常退休回收");
    }

    [Fact]
    public async Task MainTranslation_EnumeratorDisposeAsyncThrows_CatchesSafelyAndDecrementsWork()
    {
        var (vm, orchestrator, _, _) = CreateViewModel();
        var presenter = new TranslateWindowPresenter(
            Substitute.For<IServiceProvider>(),
            new ImmediateUiDispatcher(),
            Substitute.For<ISettingsService>(),
            Substitute.For<IDesktopContext>(),
            NullLogger<TranslateWindowPresenter>.Instance);

        var op = (TranslateWindowOperation)presenter.TryBeginOperation(CancellationToken.None)!;
        var reqId = Guid.NewGuid();

        var stream = new ThrowingDisposeAsyncEnumerable(reqId);
        orchestrator.TranslateAsync(Arg.Any<TranslationRequest>(), Arg.Any<CancellationToken>())
            .Returns(stream);

        vm.ApplyRequest(new TranslateWindowRequest { Text = "Test", AutoTranslate = true }, op);
        op.Dispose();

        await vm.WaitForActiveWorkAsync();

        op.ActiveWorkCount.ShouldBe(0);
        vm.IsTranslating.ShouldBeFalse();

        vm.OnWindowHidden();
        op.IsRetired.ShouldBeTrue();
    }

    [Fact]
    public async Task MainTranslation_TrailingUiInvocationThrows_CatchesSafelyAndDecrementsWork()
    {
        int invokeCount = 0;
        var throwingTrailingDispatcher = Substitute.For<IUiDispatcher>();
        throwingTrailingDispatcher.InvokeAsync(Arg.Any<Func<Task>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                if (Interlocked.Increment(ref invokeCount) == 2)
                {
                    throw new InvalidOperationException("Trailing UI failed");
                }
                await call.Arg<Func<Task>>()();
            });

        var (vm, orchestrator, _, _) = CreateViewModel(uiDispatcher: throwingTrailingDispatcher);
        var presenter = new TranslateWindowPresenter(
            Substitute.For<IServiceProvider>(),
            throwingTrailingDispatcher,
            Substitute.For<ISettingsService>(),
            Substitute.For<IDesktopContext>(),
            NullLogger<TranslateWindowPresenter>.Instance);

        var op = (TranslateWindowOperation)presenter.TryBeginOperation(CancellationToken.None)!;
        var reqId = Guid.NewGuid();

        async IAsyncEnumerable<TranslationEvent> SimpleStream()
        {
            yield return new TranslationStarted(reqId, new ResolvedLanguagePair("en", "zh-Hans", false), []);
            yield return new TranslationFinished(reqId, 0, 0);
        }

        orchestrator.TranslateAsync(Arg.Any<TranslationRequest>(), Arg.Any<CancellationToken>())
            .Returns(SimpleStream());

        vm.ApplyRequest(new TranslateWindowRequest { Text = "Test", AutoTranslate = true }, op);
        op.Dispose();

        await vm.WaitForActiveWorkAsync();

        op.ActiveWorkCount.ShouldBe(0, "末尾 UI 回调失败后，ActiveWorkCount 仍必须清零");
        vm.OnWindowHidden();
        op.IsRetired.ShouldBeTrue();
    }

    [Fact]
    public async Task AutoCopy_SameSessionStatusUpdate_DoesNotTriggerSecondAutoCopy_ManualCopyStillWorks()
    {
        var (vm, orchestrator, clipboard, _) = CreateViewModel();
        var presenter = new TranslateWindowPresenter(
            Substitute.For<IServiceProvider>(),
            new ImmediateUiDispatcher(),
            Substitute.For<ISettingsService>(),
            Substitute.For<IDesktopContext>(),
            NullLogger<TranslateWindowPresenter>.Instance);

        var op = (TranslateWindowOperation)presenter.TryBeginOperation(CancellationToken.None)!;
        var reqId = Guid.NewGuid();

        var card2Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<TranslationEvent> TwoCardStream()
        {
            var p1 = new ProviderDescriptor("p1", "P1", "Kind", null, true);
            var p2 = new ProviderDescriptor("p2", "P2", "Kind", null, true);
            yield return new TranslationStarted(reqId, new ResolvedLanguagePair("en", "zh-Hans", false), [p1, p2]);
            yield return new ProviderCompleted(reqId, "p1", "P1 First Text", TimeSpan.Zero, false, false, null);

            await card2Gate.Task;
            yield return new ProviderCompleted(reqId, "p2", "P2 Second Text", TimeSpan.Zero, false, false, null);
        }

        orchestrator.TranslateAsync(Arg.Any<TranslationRequest>(), Arg.Any<CancellationToken>())
            .Returns(TwoCardStream());

        vm.ApplyRequest(new TranslateWindowRequest { Text = "Hello", AutoTranslate = true }, op);

        var deadline = Environment.TickCount64 + 3000;
        while (!clipboard.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IClipboardService.SetTextAsync)) && Environment.TickCount64 < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        await clipboard.Received(1).SetTextAsync("P1 First Text", Arg.Any<CancellationToken>());

        // 同一会话调用 ApplyRequest 更新状态/重新展示
        vm.ApplyRequest(new TranslateWindowRequest { Text = "Hello", AutoTranslate = false, StatusMessage = "Status Updated" }, op);

        // 放行第 2 张卡
        card2Gate.SetResult();
        await vm.WaitForActiveWorkAsync();

        // 核心验证：自动复制仅发生一次，第 2 张卡不会因为状态更新重置了资格而再次自动复制
        await clipboard.Received(1).SetTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await clipboard.DidNotReceive().SetTextAsync("P2 Second Text", Arg.Any<CancellationToken>());

        // 手动复制第 2 张卡仍正常工作
        vm.Cards[1].CopyCommand.Execute(null);
        await vm.WaitForActiveWorkAsync();
        await clipboard.Received(1).SetTextAsync("P2 Second Text", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteTranslateFromUser_WithWhitespacePadding_UsesPreprocessedLengthForValidation()
    {
        var settings = new AppSettings
        {
            Translation = new TranslationSettings
            {
                MaxInputChars = 3
            }
        };

        var textPreprocessor = Substitute.For<ITextPreprocessor>();
        textPreprocessor.Prepare(Arg.Any<string>(), Arg.Any<TextOrigin>())
            .Returns("hi");

        var (vm, orchestrator, _, _) = CreateViewModel(settings, textPreprocessor: textPreprocessor);

        var presenter = new TranslateWindowPresenter(
            Substitute.For<IServiceProvider>(),
            new ImmediateUiDispatcher(),
            Substitute.For<ISettingsService>(),
            Substitute.For<IDesktopContext>(),
            NullLogger<TranslateWindowPresenter>.Instance);
        vm.AttachPresenter(presenter);

        vm.InputText = "  hi  ";
        vm.ExecuteTranslateFromUser();

        await vm.WaitForActiveWorkAsync();

        vm.Error.ShouldBeNull("经 ITextPreprocessor 预处理后的文本长度为 2 <= 3，不得报 InputTooLong");
        orchestrator.Received(1).TranslateAsync(Arg.Is<TranslationRequest>(r => r.Text == "  hi  "), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ErrorSanitizationAndLogging_DoesNotLeakRawExceptionOrSensitiveInfo()
    {
        var testLogger = new TestDiagnosticLogger<TranslateWindowViewModel>();
        var (vm, orchestrator, _, _) = CreateViewModel(logger: testLogger);

        var presenter = new TranslateWindowPresenter(
            Substitute.For<IServiceProvider>(),
            new ImmediateUiDispatcher(),
            Substitute.For<ISettingsService>(),
            Substitute.For<IDesktopContext>(),
            NullLogger<TranslateWindowPresenter>.Instance);

        var op = (TranslateWindowOperation)presenter.TryBeginOperation(CancellationToken.None)!;

        // 1. 测试前置 request.Error：包含敏感标识
        var sensitiveError = new ErrorInfo(ErrorCode.ProviderUnavailable, "SECRET_API_KEY_IN_ERROR_MSG", Detail: "SECRET_ACCESS_TOKEN_DETAIL");
        vm.ApplyRequest(new TranslateWindowRequest { Error = sensitiveError }, op);

        vm.Error.ShouldNotBeNull();
        vm.Error.Message.ShouldNotContain("SECRET");
        vm.Error.Detail.ShouldBeNull("敏感 Detail 必须被清空");

        // 2. 测试流中未知异常：包含敏感堆栈/消息
        static async IAsyncEnumerable<TranslationEvent> FailingStream()
        {
            await Task.Yield();
            throw new InvalidOperationException("SECRET_INTERNAL_DB_PASSWORD");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        orchestrator.TranslateAsync(Arg.Any<TranslationRequest>(), Arg.Any<CancellationToken>())
            .Returns(FailingStream());

        vm.ApplyRequest(new TranslateWindowRequest { Text = "Test", AutoTranslate = true }, op);
        await vm.WaitForActiveWorkAsync();

        vm.Error.ShouldNotBeNull();
        vm.Error.Message.ShouldBe(UiText.ErrorUnknown);
        vm.Error.Message.ShouldNotContain("SECRET");

        // 3. 验证日志记录：异常参数必须为 null，且日志消息不得包含原始敏感信息
        testLogger.LoggedExceptions.ShouldAllBe(e => e == null, "ILogger 调用的 Exception 参数本身必须为 null");
        foreach (var msg in testLogger.LoggedMessages)
        {
            msg.ShouldNotContain("SECRET_INTERNAL_DB_PASSWORD");
        }
    }

    [Fact]
    public async Task CopyCancellation_WhenCallbackBlocked_WaitForActiveWorkAsyncWaitsUntilCallbackExits()
    {
        var clipboard = Substitute.For<IClipboardService>();
        var (vm, orchestrator, _, _) = CreateViewModel(clipboard: clipboard);

        var presenter = new TranslateWindowPresenter(
            Substitute.For<IServiceProvider>(),
            new ImmediateUiDispatcher(),
            Substitute.For<ISettingsService>(),
            Substitute.For<IDesktopContext>(),
            NullLogger<TranslateWindowPresenter>.Instance);
        vm.AttachPresenter(presenter);

        var op = (TranslateWindowOperation)presenter.TryBeginOperation(CancellationToken.None)!;

        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        clipboard.SetTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ct = ci.Arg<CancellationToken>();
                ct.Register(() =>
                {
                    callbackEntered.TrySetResult();
                    callbackRelease.Task.GetAwaiter().GetResult();
                    callbackExited.TrySetResult();
                });
                writeEntered.TrySetResult();
                return writeRelease.Task;
            });

        var reqId = Guid.NewGuid();
        var p1 = new ProviderDescriptor("p1", "P1", "Kind", null, true);
        async IAsyncEnumerable<TranslationEvent> Stream()
        {
            yield return new TranslationStarted(reqId, new ResolvedLanguagePair("en", "zh-Hans", false), [p1]);
            yield return new ProviderCompleted(reqId, "p1", "Target Text", TimeSpan.Zero, false, false, null);
            await Task.Yield();
        }

        orchestrator.TranslateAsync(Arg.Any<TranslationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Stream());

        try
        {
            vm.ApplyRequest(new TranslateWindowRequest { Text = "Hello", AutoTranslate = true }, op);

            // 1. 等待写入进入并到达原生提交点
            await writeEntered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // 2. 写入仍在执行时接纳取消，保证该注册的回调确实进入。
            vm.OnWindowHidden();

            // 3. 等待取消回调进入并阻塞。
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // 4. 此时放行写入，并观察实际写入任务返回；取消回调仍然挂起。
            writeRelease.TrySetResult();
            await writeRelease.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            writeRelease.Task.IsCompletedSuccessfully.ShouldBeTrue();

            // 5. 核心验证：写入已经返回，但取消回调仍在执行（阻塞在 callbackRelease），WaitForActiveWorkAsync 不得完成！
            var waitTask = vm.WaitForActiveWorkAsync();
            var delayCheck = await Task.WhenAny(waitTask, Task.Delay(100, TestContext.Current.CancellationToken));
            delayCheck.ShouldNotBeSameAs(waitTask, "当取消回调仍在执行挂起时，WaitForActiveWorkAsync 必须等待，不得提前返回！");

            // 6. 放行取消回调（callback exited）
            callbackRelease.TrySetResult();
            await callbackExited.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // 7. 取消回调完全退出后，所有工作自然完成
            await waitTask.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            waitTask.IsCompletedSuccessfully.ShouldBeTrue();
        }
        finally
        {
            callbackRelease.TrySetResult();
            writeRelease.TrySetResult();
            op?.Dispose();
            await vm.WaitForActiveWorkAsync();
            await presenter.ShutdownAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task CopyCancellation_WhenCancelCallbackExitsBeforeWriterReturns_TokenRemainsUsableUntilWriterReturns()
    {
        var clipboard = Substitute.For<IClipboardService>();
        var (vm, orchestrator, _, _) = CreateViewModel(clipboard: clipboard);

        var presenter = new TranslateWindowPresenter(
            Substitute.For<IServiceProvider>(),
            new ImmediateUiDispatcher(),
            Substitute.For<ISettingsService>(),
            Substitute.For<IDesktopContext>(),
            NullLogger<TranslateWindowPresenter>.Instance);
        vm.AttachPresenter(presenter);

        var op = (TranslateWindowOperation)presenter.TryBeginOperation(CancellationToken.None)!;

        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken capturedToken = default;

        clipboard.SetTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                capturedToken = ci.Arg<CancellationToken>();
                capturedToken.Register(() => callbackEntered.TrySetResult());
                writeEntered.TrySetResult();
                await writeRelease.Task;
            });

        var reqId = Guid.NewGuid();
        var p1 = new ProviderDescriptor("p1", "P1", "Kind", null, true);
        async IAsyncEnumerable<TranslationEvent> Stream()
        {
            yield return new TranslationStarted(reqId, new ResolvedLanguagePair("en", "zh-Hans", false), [p1]);
            yield return new ProviderCompleted(reqId, "p1", "Target Text", TimeSpan.Zero, false, false, null);
            await Task.Yield();
        }

        orchestrator.TranslateAsync(Arg.Any<TranslationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Stream());

        try
        {
            vm.ApplyRequest(new TranslateWindowRequest { Text = "Hello", AutoTranslate = true }, op);

            // 1. 等待写入进入并到达原生提交点
            await writeEntered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // 2. 隐藏窗口触发取消，取消回调同步执行并退出
            vm.OnWindowHidden();
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // 3. 验证：此时取消回调已完成，但写入尚未返回，CTS 绝不能被提前释放！
            writeRelease.Task.IsCompleted.ShouldBeFalse("写入操作尚未返回");
            bool tokenUsable = true;
            try
            {
                _ = capturedToken.WaitHandle;
            }
            catch (ObjectDisposedException)
            {
                tokenUsable = false;
            }
            tokenUsable.ShouldBeTrue("取消回调先退出时，传给剪贴板服务的 CancellationToken 必须在写入返回前保持有效未释放");

            // 4. 放行写入返回
            writeRelease.TrySetResult();

            // 5. 等待排空完成
            await vm.WaitForActiveWorkAsync().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        }
        finally
        {
            writeRelease.TrySetResult();
            op?.Dispose();
            await vm.WaitForActiveWorkAsync();
            await presenter.ShutdownAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task CopyCancellation_WhenCallbackThrows_ExceptionIsSafelyHandledAndWorkDrains()
    {
        var testLogger = new TestDiagnosticLogger<TranslateWindowViewModel>();
        var clipboard = Substitute.For<IClipboardService>();
        var (vm, orchestrator, _, _) = CreateViewModel(clipboard: clipboard, logger: testLogger);

        var presenter = new TranslateWindowPresenter(
            Substitute.For<IServiceProvider>(),
            new ImmediateUiDispatcher(),
            Substitute.For<ISettingsService>(),
            Substitute.For<IDesktopContext>(),
            NullLogger<TranslateWindowPresenter>.Instance);
        vm.AttachPresenter(presenter);

        var op = (TranslateWindowOperation)presenter.TryBeginOperation(CancellationToken.None)!;

        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackExecuted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        clipboard.SetTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var ct = ci.Arg<CancellationToken>();
                ct.Register(() =>
                {
                    callbackExecuted.TrySetResult();
                    throw new InvalidOperationException("FAILLOG_CALLBACK_SECRET_TEST");
                });
                writeEntered.TrySetResult();
                await writeRelease.Task;
            });

        var reqId = Guid.NewGuid();
        var p1 = new ProviderDescriptor("p1", "P1", "Kind", null, true);
        async IAsyncEnumerable<TranslationEvent> Stream()
        {
            yield return new TranslationStarted(reqId, new ResolvedLanguagePair("en", "zh-Hans", false), [p1]);
            yield return new ProviderCompleted(reqId, "p1", "Target Text", TimeSpan.Zero, false, false, null);
            await Task.Yield();
        }

        orchestrator.TranslateAsync(Arg.Any<TranslationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Stream());

        try
        {
            vm.ApplyRequest(new TranslateWindowRequest { Text = "Hello", AutoTranslate = true }, op);

            // 1. 等待在途写入进入并注册回调
            await writeEntered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // 2. 隐藏窗口触发取消，回调抛出异常
            vm.OnWindowHidden();

            await callbackExecuted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // 3. 放行写入
            writeRelease.TrySetResult();

            // 4. 验证：WaitForActiveWorkAsync 能够安全排空而不向外抛错
            await vm.WaitForActiveWorkAsync().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // 5. 验证：异常被安全捕获并记录类型，未泄露敏感字符串
            testLogger.LoggedMessages.ShouldAllBe(m => !m.Contains("SECRET"));
        }
        finally
        {
            writeRelease.TrySetResult();
            op?.Dispose();
            await vm.WaitForActiveWorkAsync();
            await presenter.ShutdownAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task CopyCancellation_PendingReplacement_WaitsForCallbackExit()
    {
        var clipboard = Substitute.For<IClipboardService>();
        var appSettings = new AppSettings
        {
            Translation = new TranslationSettings
            {
                AutoCopy = AutoCopyMode.None,
                MaxInputChars = 5000
            }
        };
        var (vm, orchestrator, _, _) = CreateViewModel(settings: appSettings, clipboard: clipboard);

        var presenter = new TranslateWindowPresenter(
            Substitute.For<IServiceProvider>(),
            new ImmediateUiDispatcher(),
            Substitute.For<ISettingsService>(),
            Substitute.For<IDesktopContext>(),
            NullLogger<TranslateWindowPresenter>.Instance);
        vm.AttachPresenter(presenter);

        var op = (TranslateWindowOperation)presenter.TryBeginOperation(CancellationToken.None)!;
        var reqId = Guid.NewGuid();

        var p1 = new ProviderDescriptor("p1", "P1", "Kind", null, true);
        var p2 = new ProviderDescriptor("p2", "P2", "Kind", null, true);
        var p3 = new ProviderDescriptor("p3", "P3", "Kind", null, true);

        async IAsyncEnumerable<TranslationEvent> Stream()
        {
            yield return new TranslationStarted(reqId, new ResolvedLanguagePair("en", "zh-Hans", false), [p1, p2, p3]);
            yield return new ProviderCompleted(reqId, "p1", "Text1", TimeSpan.Zero, false, false, null);
            yield return new ProviderCompleted(reqId, "p2", "Text2", TimeSpan.Zero, false, false, null);
            yield return new ProviderCompleted(reqId, "p3", "Text3", TimeSpan.Zero, false, false, null);
            await Task.Yield();
        }

        orchestrator.TranslateAsync(Arg.Any<TranslationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Stream());

        var inFlightStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlightGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        clipboard.SetTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var text = ci.Arg<string>();
                if (text == "Text1")
                {
                    inFlightStarted.TrySetResult();
                    await inFlightGate.Task;
                }
            });

        try
        {
            vm.ApplyRequest(new TranslateWindowRequest { Text = "Test", AutoTranslate = true }, op);
            await vm.WaitForActiveWorkAsync();

            // 1. 复制 Card 1（在途）
            vm.Cards[0].CopyCommand.Execute(null);
            await inFlightStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // 2. 连续复制 Card 2 与 Card 3（Card 2 被 Card 3 替换并取消）
            vm.Cards[1].CopyCommand.Execute(null);
            vm.Cards[2].CopyCommand.Execute(null);

            // 放行在途任务
            inFlightGate.TrySetResult();

            await vm.WaitForActiveWorkAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            inFlightGate.TrySetResult();
            vm.OnWindowHidden();
            op?.Dispose();
            await vm.WaitForActiveWorkAsync();
            await presenter.ShutdownAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task CopyCancellation_PendingReplacement_DoesNotThrowOrLogObjectDisposedException()
    {
        var testLogger = new TestDiagnosticLogger<TranslateWindowViewModel>();
        var clipboard = Substitute.For<IClipboardService>();
        var appSettings = new AppSettings
        {
            Translation = new TranslationSettings
            {
                AutoCopy = AutoCopyMode.None,
                MaxInputChars = 5000
            }
        };
        var (vm, orchestrator, _, _) = CreateViewModel(settings: appSettings, clipboard: clipboard, logger: testLogger);

        var presenter = new TranslateWindowPresenter(
            Substitute.For<IServiceProvider>(),
            new ImmediateUiDispatcher(),
            Substitute.For<ISettingsService>(),
            Substitute.For<IDesktopContext>(),
            NullLogger<TranslateWindowPresenter>.Instance);
        vm.AttachPresenter(presenter);

        var op = (TranslateWindowOperation)presenter.TryBeginOperation(CancellationToken.None)!;
        var reqId = Guid.NewGuid();

        var p1 = new ProviderDescriptor("p1", "P1", "Kind", null, true);
        var p2 = new ProviderDescriptor("p2", "P2", "Kind", null, true);
        var p3 = new ProviderDescriptor("p3", "P3", "Kind", null, true);

        async IAsyncEnumerable<TranslationEvent> Stream()
        {
            yield return new TranslationStarted(reqId, new ResolvedLanguagePair("en", "zh-Hans", false), [p1, p2, p3]);
            yield return new ProviderCompleted(reqId, "p1", "Text1", TimeSpan.Zero, false, false, null);
            yield return new ProviderCompleted(reqId, "p2", "Text2", TimeSpan.Zero, false, false, null);
            yield return new ProviderCompleted(reqId, "p3", "Text3", TimeSpan.Zero, false, false, null);
            await Task.Yield();
        }

        orchestrator.TranslateAsync(Arg.Any<TranslationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Stream());

        var inFlightStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlightGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        clipboard.SetTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var text = ci.Arg<string>();
                if (text == "Text1")
                {
                    inFlightStarted.TrySetResult();
                    await inFlightGate.Task;
                }
            });

        try
        {
            vm.ApplyRequest(new TranslateWindowRequest { Text = "Test", AutoTranslate = true }, op);
            await vm.WaitForActiveWorkAsync();

            // 1. 首个复制在途挂起
            vm.Cards[0].CopyCommand.Execute(null);
            await inFlightStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // 2. 连续触发两次复制形成待项替换
            vm.Cards[1].CopyCommand.Execute(null);
            vm.Cards[2].CopyCommand.Execute(null);

            // 此时待项已替换，验证未发生 ObjectDisposedException
            testLogger.LoggedMessages.ShouldNotContain(m => m.Contains("ObjectDisposedException"));
            testLogger.LoggedExceptions.ShouldNotContain(e => e is ObjectDisposedException);

            // 3. 放行在途任务并等待全部完成
            inFlightGate.TrySetResult();
            await vm.WaitForActiveWorkAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            // 4. 再次验证全流程无 ObjectDisposedException
            testLogger.LoggedMessages.ShouldNotContain(m => m.Contains("ObjectDisposedException"));
            testLogger.LoggedExceptions.ShouldNotContain(e => e is ObjectDisposedException);

            // 验证最终仅首项与最新合法待项执行了剪贴板写入
            await clipboard.Received(1).SetTextAsync("Text1", Arg.Any<CancellationToken>());
            await clipboard.Received(0).SetTextAsync("Text2", Arg.Any<CancellationToken>());
            await clipboard.Received(1).SetTextAsync("Text3", Arg.Any<CancellationToken>());
        }
        finally
        {
            inFlightGate.TrySetResult();
            vm.OnWindowHidden();
            op?.Dispose();
            await vm.WaitForActiveWorkAsync();
            await presenter.ShutdownAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task CopyCancellation_PendingHide_DoesNotThrowOrLogObjectDisposedException()
    {
        var testLogger = new TestDiagnosticLogger<TranslateWindowViewModel>();
        var clipboard = Substitute.For<IClipboardService>();
        var appSettings = new AppSettings
        {
            Translation = new TranslationSettings
            {
                AutoCopy = AutoCopyMode.None,
                MaxInputChars = 5000
            }
        };
        var (vm, orchestrator, _, _) = CreateViewModel(settings: appSettings, clipboard: clipboard, logger: testLogger);

        var presenter = new TranslateWindowPresenter(
            Substitute.For<IServiceProvider>(),
            new ImmediateUiDispatcher(),
            Substitute.For<ISettingsService>(),
            Substitute.For<IDesktopContext>(),
            NullLogger<TranslateWindowPresenter>.Instance);
        vm.AttachPresenter(presenter);

        var op = (TranslateWindowOperation)presenter.TryBeginOperation(CancellationToken.None)!;
        var reqId = Guid.NewGuid();

        var p1 = new ProviderDescriptor("p1", "P1", "Kind", null, true);
        var p2 = new ProviderDescriptor("p2", "P2", "Kind", null, true);

        async IAsyncEnumerable<TranslationEvent> Stream()
        {
            yield return new TranslationStarted(reqId, new ResolvedLanguagePair("en", "zh-Hans", false), [p1, p2]);
            yield return new ProviderCompleted(reqId, "p1", "Text1", TimeSpan.Zero, false, false, null);
            yield return new ProviderCompleted(reqId, "p2", "Text2", TimeSpan.Zero, false, false, null);
            await Task.Yield();
        }

        orchestrator.TranslateAsync(Arg.Any<TranslationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Stream());

        var inFlightStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlightGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        clipboard.SetTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var text = ci.Arg<string>();
                if (text == "Text1")
                {
                    inFlightStarted.TrySetResult();
                    await inFlightGate.Task;
                }
            });

        try
        {
            vm.ApplyRequest(new TranslateWindowRequest { Text = "Test", AutoTranslate = true }, op);
            await vm.WaitForActiveWorkAsync();

            // 1. 首个复制在途挂起
            vm.Cards[0].CopyCommand.Execute(null);
            await inFlightStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // 2. 排队待项
            vm.Cards[1].CopyCommand.Execute(null);

            // 3. 隐藏窗口取消待项与在途项
            vm.OnWindowHidden();

            // 验证未发生 ObjectDisposedException
            testLogger.LoggedMessages.ShouldNotContain(m => m.Contains("ObjectDisposedException"));
            testLogger.LoggedExceptions.ShouldNotContain(e => e is ObjectDisposedException);

            // 4. 放行在途任务并等待全部完成
            inFlightGate.TrySetResult();
            await vm.WaitForActiveWorkAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            // 5. 再次验证全流程无 ObjectDisposedException
            testLogger.LoggedMessages.ShouldNotContain(m => m.Contains("ObjectDisposedException"));
            testLogger.LoggedExceptions.ShouldNotContain(e => e is ObjectDisposedException);
        }
        finally
        {
            inFlightGate.TrySetResult();
            vm.OnWindowHidden();
            op?.Dispose();
            await vm.WaitForActiveWorkAsync();
            await presenter.ShutdownAsync(TestContext.Current.CancellationToken);
        }
    }

    private sealed class ThrowingDisposeAsyncEnumerable : IAsyncEnumerable<TranslationEvent>
    {
        private readonly Guid _reqId;
        public ThrowingDisposeAsyncEnumerable(Guid reqId) => _reqId = reqId;

        public IAsyncEnumerator<TranslationEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => new ThrowingEnumerator(_reqId);

        private sealed class ThrowingEnumerator : IAsyncEnumerator<TranslationEvent>
        {
            private readonly Guid _reqId;
            private int _state;
            public ThrowingEnumerator(Guid reqId) => _reqId = reqId;
            public TranslationEvent Current { get; private set; } = null!;
            public ValueTask<bool> MoveNextAsync()
            {
                if (_state == 0)
                {
                    _state = 1;
                    Current = new TranslationStarted(_reqId, new ResolvedLanguagePair("en", "zh-Hans", false), []);
                    return ValueTask.FromResult(true);
                }
                return ValueTask.FromResult(false);
            }
            public ValueTask DisposeAsync() => throw new InvalidOperationException("DisposeAsync failed");
        }
    }

    private sealed class TestDiagnosticLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<Exception?> LoggedExceptions { get; } = [];
        public List<string> LoggedMessages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            LoggedExceptions.Add(exception);
            LoggedMessages.Add(formatter(state, exception));
        }
    }
}

