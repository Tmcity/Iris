using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Actions;
using Iris.Core.DependencyInjection;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Iris.Core.Workflows;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.Core.Tests.Workflows;

public sealed class OpenSettingsHandlerTests
{
    [Fact]
    public void Action_ReturnsOpenSettings()
    {
        var presenter = Substitute.For<ISettingsWindowPresenter>();
        var handler = new OpenSettingsHandler(presenter);

        handler.Action.ShouldBe(IrisAction.OpenSettings);
    }

    [Fact]
    public async Task ExecuteAsync_WhenContextIsNull_ThrowsArgumentNullException()
    {
        var presenter = Substitute.For<ISettingsWindowPresenter>();
        var handler = new OpenSettingsHandler(presenter);

        await Should.ThrowAsync<ArgumentNullException>(() =>
            handler.ExecuteAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_WhenCancellationRequested_ThrowsOperationCanceledException_AndDoesNotCallPresenter()
    {
        var presenter = Substitute.For<ISettingsWindowPresenter>();
        var handler = new OpenSettingsHandler(presenter);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var context = new ActionContext(ActionTrigger.TrayMenu, Payload: "general");

        await Should.ThrowAsync<OperationCanceledException>(() =>
            handler.ExecuteAsync(context, cts.Token));

        await presenter.DidNotReceive().ShowAsync(Arg.Any<string?>());
    }

    [Fact]
    public async Task ExecuteAsync_ForwardsPayloadToPresenterShowAsync()
    {
        var presenter = Substitute.For<ISettingsWindowPresenter>();
        var handler = new OpenSettingsHandler(presenter);

        var context = new ActionContext(ActionTrigger.TrayMenu, Payload: "hotkeys");

        await handler.ExecuteAsync(context, CancellationToken.None);

        await presenter.Received(1).ShowAsync("hotkeys");
    }

    [Fact]
    public async Task ExecuteAsync_WhenPayloadIsNull_ForwardsNullToPresenterShowAsync()
    {
        var presenter = Substitute.For<ISettingsWindowPresenter>();
        var handler = new OpenSettingsHandler(presenter);

        var context = new ActionContext(ActionTrigger.TrayMenu, Payload: null);

        await handler.ExecuteAsync(context, CancellationToken.None);

        await presenter.Received(1).ShowAsync(null);
    }

    [Fact]
    public void CoreDI_WithFakePresenterAndSettings_ResolvesOpenSettingsHandler()
    {
        var services = new ServiceCollection();
        var fakeSettings = Substitute.For<ISettingsService>();
        services.AddSingleton(fakeSettings);

        var fakePresenter = Substitute.For<ISettingsWindowPresenter>();
        services.AddSingleton(fakePresenter);

        var fakeTranslatePresenter = Substitute.For<ITranslateWindowPresenter>();
        services.AddSingleton(fakeTranslatePresenter);
        var fakeClipboard = Substitute.For<Iris.Core.Platform.IClipboardService>();
        services.AddSingleton(fakeClipboard);
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));

        services.AddIrisCore();

        using var provider = services.BuildServiceProvider();
        var handlers = provider.GetRequiredService<IEnumerable<IActionHandler>>().ToList();

        var handler = handlers.OfType<OpenSettingsHandler>().FirstOrDefault();
        handler.ShouldNotBeNull();
        handler.Action.ShouldBe(IrisAction.OpenSettings);
    }
}
