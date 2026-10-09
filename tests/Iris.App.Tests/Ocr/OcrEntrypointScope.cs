using System.Collections.Concurrent;
using System.Windows;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Tray;
using Iris.App.Translation;
using Iris.Core.Actions;
using Iris.Core.DependencyInjection;
using Iris.Core.Imaging;
using Iris.Core.Ocr;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Iris.App.Tests.Ocr;

internal sealed class OcrEntrypointScope : IAsyncDisposable
{
    internal readonly ISettingsService Settings = Substitute.For<ISettingsService>();
    internal readonly IScreenshotService Screenshot = Substitute.For<IScreenshotService>();
    internal readonly IOcrService Ocr = Substitute.For<IOcrService>();
    internal readonly IClipboardService Clipboard = Substitute.For<IClipboardService>();
    internal readonly ITranslationOrchestrator Translation = Substitute.For<ITranslationOrchestrator>();
    internal readonly NativeNotifications Native = new();
    internal readonly ControlledUi Ui = new();
    internal readonly ConcurrentQueue<TranslationRequest> Translations = new();
    internal readonly ConcurrentQueue<string> Copies = new();
    internal readonly ConcurrentQueue<ScreenshotPurpose> Captures = new();
    internal readonly ServiceProvider Services;
    internal readonly TranslateWindowPresenter Presenter;
    internal readonly ActionDispatcher Dispatcher;
    internal readonly ScreenshotOcrFeedback Feedback;
    internal readonly TrayNotificationService Notifications;
    internal readonly TrayIconController Tray;
    internal readonly IActionHandler[] Handlers;
    internal static OcrOutcome Outcome(string text = "OCR 原文\nHello 😀", bool fallback = false) => new(new("private-provider", [], null, TimeSpan.Zero), text, fallback);
    internal static readonly ScreenshotResult Shot = new(new([1, 2, 3], ImageFormat.Png, 40, 40), new(-400, 200, 40, 40));

    internal OcrEntrypointScope(Action<TranslateWindow>? configureWindow = null)
    {
        Settings.Current.Returns(new AppSettings { TranslateWindow = new() { HideOnDeactivate = false, Topmost = false } });
        Screenshot.CaptureRegionAsync(Arg.Any<ScreenshotPurpose>(), Arg.Any<CancellationToken>()).Returns(c =>
        { Captures.Enqueue(c.Arg<ScreenshotPurpose>()); return Shot; });
        Ocr.RecognizeAsync(Arg.Any<ImageData>(), null, Arg.Any<CancellationToken>()).Returns(Outcome());
        Clipboard.SetTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(c =>
        { Copies.Enqueue(c.Arg<string>()); return Task.CompletedTask; });
        Translation.TranslateAsync(Arg.Any<TranslationRequest>(), Arg.Any<CancellationToken>()).Returns(c =>
        { Translations.Enqueue(c.Arg<TranslationRequest>()); return EmptyTranslation(); });
        var desktop = Substitute.For<IDesktopContext>();
        desktop.GetMonitors().Returns(Array.Empty<MonitorInfo>());
        var services = new ServiceCollection();
        services.AddLogging(); services.AddIrisCore();
        services.AddSingleton(Settings); services.AddSingleton(Screenshot); services.AddSingleton(Ocr);
        services.AddSingleton(Clipboard); services.AddSingleton(Translation); services.AddSingleton(desktop);
        services.AddSingleton(Substitute.For<ISettingsWindowPresenter>());
        services.AddSingleton(Substitute.For<IHistoryWindowPresenter>());
        services.AddSingleton(Substitute.For<ISelectionTextReader>());
        services.AddSingleton<IUiDispatcher>(Ui);
        services.AddSingleton<TranslateWindowPresenter>();
        services.AddSingleton<ITranslateWindowPresenter>(s => s.GetRequiredService<TranslateWindowPresenter>());
        services.AddScoped<TranslateWindowViewModel>();
        services.AddTransient<TranslateWindow>(_ =>
        {
            var window = new TranslateWindow
            {
                Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            configureWindow?.Invoke(window);
            return window;
        });
        services.AddSingleton<TrayNotificationService>();
        services.AddSingleton<INotificationService>(s => s.GetRequiredService<TrayNotificationService>());
        services.AddSingleton<ScreenshotOcrFeedback>();
        services.AddSingleton<IScreenshotOcrFeedback>(s => s.GetRequiredService<ScreenshotOcrFeedback>());
        services.AddSingleton<IActionDispatcher, ActionDispatcher>();
        Services = services.BuildServiceProvider();
        Presenter = Services.GetRequiredService<TranslateWindowPresenter>();
        Dispatcher = (ActionDispatcher)Services.GetRequiredService<IActionDispatcher>();
        Feedback = Services.GetRequiredService<ScreenshotOcrFeedback>();
        Notifications = Services.GetRequiredService<TrayNotificationService>();
        Handlers = Services.GetServices<IActionHandler>().ToArray();
        Tray = new(Substitute.For<IAppLifetime>(), Settings, Dispatcher, NullLogger<TrayIconController>.Instance, Native)
        { SuppressIconCreationForTesting = true };
        Notifications.AttachTray(Tray);
    }

    internal Task Run(IrisAction action, CancellationToken token = default) => Dispatcher.DispatchAsync(action, new(ActionTrigger.TrayMenu), token);

    public async ValueTask DisposeAsync()
    {
        Ui.Release(); Dispatcher.StopAcceptance(); Feedback.StopAcceptance(); Presenter.StopAcceptance();
        await Feedback.StopAsync();
        await Dispatcher.WaitForInFlightAsync();
        await Presenter.ShutdownAsync(CancellationToken.None);
        Tray.Dispose(); await Services.DisposeAsync();
    }

    private static async IAsyncEnumerable<TranslationEvent> EmptyTranslation()
    { await Task.CompletedTask; yield break; }

    internal sealed class NativeNotifications : ITrayNotificationNative
    {
        public bool IsCreated { get; set; } = true;
        internal readonly List<(string Title, string Text, NotificationLevel Level)> Messages = [];
        public void Show(string title, string message, NotificationLevel level) => Messages.Add((title, message, level));
    }

    internal sealed class ControlledUi : IUiDispatcher
    {
        private readonly WpfUiDispatcher _actual = new();
        private TaskCompletionSource? _release;
        internal TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Pause() { Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); _release = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        internal void Release() { _release?.TrySetResult(); _release = null; }
        public bool CheckAccess() => _actual.CheckAccess();
        public async Task InvokeAsync(Func<Task> callback, CancellationToken cancellationToken)
        {
            var release = _release;
            if (release != null) { Entered.TrySetResult(); await release.Task.ConfigureAwait(false); }
            await _actual.InvokeAsync(callback, cancellationToken).ConfigureAwait(false);
        }
    }
}
