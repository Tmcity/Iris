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
using Iris.Core.Languages;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Translation;

public sealed class TranslateWindowViewModelTests
{
    private static (TranslateWindowViewModel ViewModel, ITranslationOrchestrator Orchestrator, IClipboardService Clipboard, ISettingsService SettingsService) CreateViewModel(AppSettings? settings = null)
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
        var clipboard = Substitute.For<IClipboardService>();
        var settingsPresenter = Substitute.For<ISettingsWindowPresenter>();
        var dispatcher = new ImmediateUiDispatcher();

        var vm = new TranslateWindowViewModel(
            settingsService,
            orchestrator,
            clipboard,
            settingsPresenter,
            dispatcher,
            NullLogger<TranslateWindowViewModel>.Instance);

        return (vm, orchestrator, clipboard, settingsService);
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
        var (vm, orchestrator, _, _) = CreateViewModel();

        var presenter = new TranslateWindowPresenter(
            Substitute.For<IServiceProvider>(),
            new ImmediateUiDispatcher(),
            Substitute.For<ISettingsService>(),
            Substitute.For<IDesktopContext>(),
            NullLogger<TranslateWindowPresenter>.Instance);

        var op = (TranslateWindowOperation)presenter.TryBeginOperation(CancellationToken.None)!;
        var reqId = Guid.NewGuid();

        var disposeStarted = new TaskCompletionSource();
        var disposeGate = new TaskCompletionSource();

        var stream = new BlockingDisposeAsyncEnumerable(reqId, disposeStarted, disposeGate);
        orchestrator.TranslateAsync(Arg.Any<TranslationRequest>(), Arg.Any<CancellationToken>())
            .Returns(stream);

        try
        {
            vm.ApplyRequest(new TranslateWindowRequest { Text = "Test Stream", AutoTranslate = true }, op!);

            // 调用方模拟展示完成，立即释放前置调用方租约
            op!.Dispose();

            // 等待流执行完 EOF 并进入 DisposeAsync 阶段
            await disposeStarted.Task;

            // 此时 DisposeAsync 处于挂起阻塞中，操作租约由于活跃工作未结束，必须保留且不得提前退休
            op.IsRetired.ShouldBeFalse();
            op.ActiveWorkCount.ShouldBeGreaterThan(0);

            // 放行 DisposeAsync 闸门
            disposeGate.SetResult();

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

        // 构造 4 张卡片：
        // P1: 1,000,000 字符
        // P2: 1,000,000 字符
        // P3: 1,000,000 字符
        // P4: 超出剩余预算并以代理对结尾
        int p1Len = 1_000_000;
        int p2Len = 1_000_000;
        int p3Len = 1_000_000;
        string p1Text = new('A', p1Len);
        string p2Text = new('B', p2Len);
        string p3Text = new('C', p3Len);

        // 剩余全局总预算 = MaxTotalChars (4,194,304) - 3,000,000 = 1,194,304
        // 单卡上限 = MaxCardChars = 1,048,576
        // P4 允许长度 = min(1,048,576, 1,194,304) = 1,048,576
        // 构造 P4 文本：刚好在允许边界 1,048,576 上卡在高代理项 '\uD83D'
        string emoji = "\uD83D\uDE0A";
        string p4Base = new('D', 1_048_575); // 长度 1,048,575
        string p4Text = p4Base + emoji;       // 长度 1,048,577。索引 1,048,575 是 '\uD83D'，索引 1,048,576 是 '\uDE0A'

        async IAsyncEnumerable<TranslationEvent> MainStream()
        {
            var p1 = new ProviderDescriptor("p1", "P1", "Kind", null, true);
            var p2 = new ProviderDescriptor("p2", "P2", "Kind", null, true);
            var p3 = new ProviderDescriptor("p3", "P3", "Kind", null, true);
            var p4 = new ProviderDescriptor("p4", "P4", "Kind", null, true);
            yield return new TranslationStarted(reqId, new ResolvedLanguagePair("en", "zh-Hans", false), [p1, p2, p3, p4]);
            yield return new ProviderCompleted(reqId, "p1", p1Text, TimeSpan.Zero, false, false, null);
            yield return new ProviderCompleted(reqId, "p2", p2Text, TimeSpan.Zero, false, false, null);
            yield return new ProviderCompleted(reqId, "p3", p3Text, TimeSpan.Zero, false, false, null);
            yield return new ProviderCompleted(reqId, "p4", p4Text, TimeSpan.Zero, false, false, null);
        }

        orchestrator.TranslateAsync(Arg.Is<TranslationRequest>(r => r.ProviderIds == null), Arg.Any<CancellationToken>())
            .Returns(MainStream());

        vm.ApplyRequest(new TranslateWindowRequest { Text = "Test", AutoTranslate = true }, op!);
        await vm.WaitForActiveWorkAsync();

        vm.Cards.Count.ShouldBe(4);
        vm.Cards[0].IsLocallyTruncated.ShouldBeFalse();
        vm.Cards[1].IsLocallyTruncated.ShouldBeFalse();
        vm.Cards[2].IsLocallyTruncated.ShouldBeFalse();

        // P4 被本地截断并安全规避孤立高代理项（长度回退至 1,048,575）
        vm.Cards[3].IsLocallyTruncated.ShouldBeTrue();
        vm.Cards[3].Text.Length.ShouldBe(1_048_575);
        vm.Cards[3].Text.ShouldBe(p4Base);
        vm.Cards[3].CopyTooltip.ShouldBe(UiText.TranslateCopyVisible);

        // 跨重试预算：重试 P1，返回超长文本
        async IAsyncEnumerable<TranslationEvent> P1RetryStream(Guid retryId)
        {
            // 其它卡片占用：P2 (1,000,000) + P3 (1,000,000) + P4 (1,048,575) = 3,048,575
            // 剩余总预算 = 4,194,304 - 3,048,575 = 1,145,729
            // 单卡上限 = 1,048,576
            // 传入 2,000,000 字符，应被截断为 1,048,576
            yield return new ProviderStarted(retryId, "p1");
            yield return new ProviderCompleted(retryId, "p1", new string('Z', 2_000_000), TimeSpan.Zero, false, false, null);
        }

        orchestrator.TranslateAsync(Arg.Is<TranslationRequest>(r => r.ProviderIds != null && r.ProviderIds.Contains("p1")), Arg.Any<CancellationToken>())
            .Returns(call => P1RetryStream(call.Arg<TranslationRequest>().RequestId));

        vm.Cards[0].RetryCommand.Execute(null);
        await vm.WaitForActiveWorkAsync();

        vm.Cards[0].IsLocallyTruncated.ShouldBeTrue();
        vm.Cards[0].Text.Length.ShouldBe(TextBudgetManager.MaxCardChars);
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
}

