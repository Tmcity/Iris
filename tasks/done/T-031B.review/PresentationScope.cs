using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using Iris.App.Translation;
using Iris.App.Services;
using Iris.App.Tray;
using Iris.Core.Actions;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

sealed class TranslationBoundary:ITranslationOrchestrator
{
    internal readonly ConcurrentQueue<TranslationRequest> Requests=new();
    internal Func<TranslationRequest,CancellationToken,Task> Wait = (_,_)=>Task.CompletedTask;
    public async IAsyncEnumerable<TranslationEvent> TranslateAsync(TranslationRequest request,[EnumeratorCancellation]CancellationToken cancellationToken)
    { Requests.Enqueue(request);await Wait(request,cancellationToken);yield break; }
}

sealed class PlacementBoundary:INativeWindowPlacementAdapter
{
    internal readonly ConcurrentQueue<PixelRect> RequestedBounds=new();
    public bool SetWindowBounds(nint handle,PixelRect bounds,bool show,bool activate)
    { RequestedBounds.Enqueue(bounds);return true; }
    public bool TryGetWindowBounds(nint handle,out PixelRect bounds)
    { bounds=new(-12000,-12000,960,600);return true; }
}

// Private Application/STA is supplied by ReviewSta; no system clipboard or desktop service.
sealed class PresentationScope:IAsyncDisposable
{
    internal readonly ReviewSta Sta;
    internal readonly ReviewUi Ui;
    internal readonly TranslationBoundary Translation=new();
    internal readonly PlacementBoundary Placement=new();
    internal readonly NotificationBoundary Shell=new();
    internal readonly TrayNotificationService Notifications=new(NullLogger<TrayNotificationService>.Instance);
    internal readonly TrayIconController Tray;
    internal readonly IClipboardService Clipboard=Substitute.For<IClipboardService>();
    internal readonly ISettingsService Settings=Substitute.For<ISettingsService>();
    internal readonly List<ITranslateWindowOperation> Operations=new();
    internal readonly List<Task> Work=new();
    internal readonly List<Action> Release=new();
    internal readonly List<TranslateWindow> Windows=new();
    internal readonly ServiceProvider Services;
    internal readonly TranslateWindowPresenter Presenter;
    internal PresentationScope(ReviewSta sta)
    {
        Sta=sta;Ui=new(sta);Settings.Current.Returns(new AppSettings());
        Tray=new(Substitute.For<IAppLifetime>(),Settings,Substitute.For<IActionDispatcher>(),NullLogger<TrayIconController>.Instance,Shell){SuppressIconCreationForTesting=true};
        Notifications.AttachTray(Tray);
        var desktop=Substitute.For<IDesktopContext>();
        desktop.GetMonitors().Returns([new MonitorInfo("Review synthetic monitor",new(0,0,1920,1080),new(0,0,1920,1040),1,true)]);
        desktop.GetCursorPosition().Returns(new PixelPoint(100,100));
        var preprocessor=Substitute.For<ITextPreprocessor>();
        preprocessor.Prepare(Arg.Any<string>(),Arg.Any<TextOrigin>()).Returns(c=>c.Arg<string>().Trim());
        preprocessor.Prepare(Arg.Any<string>(),Arg.Any<TextOrigin>(),Arg.Any<TranslationSettings>()).Returns(c=>c.Arg<string>().Trim());
        var services=new ServiceCollection();
        services.AddSingleton(Clipboard);services.AddSingleton(Settings);
        services.AddScoped(_=>new TranslateWindowViewModel(Settings,Translation,Clipboard,Substitute.For<ISettingsWindowPresenter>(),Ui,NullLogger<TranslateWindowViewModel>.Instance,preprocessor));
        services.AddTransient(_=>{var window=new TranslateWindow {ShowActivated=false,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual,Left=-12000,Top=-12000,Opacity=0};Windows.Add(window);return window;});
        Services=services.BuildServiceProvider();
        Presenter=new(Services,Ui,Settings,desktop,NullLogger<TranslateWindowPresenter>.Instance,Placement,Notifications);
    }
    internal ITranslateWindowOperation Begin(CancellationToken token=default)
    {var operation=Presenter.TryBeginOperation(token)??throw new InvalidOperationException("Expected operation admission");Operations.Add(operation);return operation;}
    internal Task<bool> Show(ITranslateWindowOperation operation,TranslateWindowRequest request)
    {var task=Presenter.TryShowAsync(operation,request);Work.Add(task);return task;}
    public async ValueTask DisposeAsync()
    {
        Ui.Release();foreach(var release in Release)release();
        foreach(var operation in Operations)operation.Dispose();
        var errors=new List<Exception>();
        foreach(var task in Work)try{await task;}catch(OperationCanceledException){}catch(Exception ex){errors.Add(ex);}
        try{await Presenter.ShutdownAsync();}catch(Exception ex){errors.Add(ex);}
        try{await Sta.Run(()=>{foreach(var window in Windows)if(new WindowInteropHelper(window).Handle!=0)throw new InvalidOperationException("Owned translation HWND remains alive");return Task.CompletedTask;});}catch(Exception ex){errors.Add(ex);}
        try{await Services.DisposeAsync();}catch(Exception ex){errors.Add(ex);}
        try{await Sta.Run(()=>{Tray.Dispose();return Task.CompletedTask;});}catch(Exception ex){errors.Add(ex);}
        if(errors.Count!=0)throw new AggregateException(errors);
    }
}
