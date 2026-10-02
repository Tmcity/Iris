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
        var (vm, orchestrator, _, _) = CreateViewModel();

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

            // 重试 P1
            vm.Cards[0].RetryCommand.Execute(null);

            // P1 重置为 Streaming/更新文本，而 P2 依然在流式运行
            vm.Cards[1].State.ShouldBe(CardState.Streaming);

            // 解除 P2 流式闸门
            bStreamingGate.SetResult();
            await vm.WaitForActiveWorkAsync();

            // 最终 P1 与 P2 均成功独立收尾
            vm.Cards[0].Text.ShouldBe("P1 Retried Text");
            vm.Cards[1].Text.ShouldBe("P2 stream finished");
        }
        finally
        {
            bStreamingGate.TrySetResult();
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
}
