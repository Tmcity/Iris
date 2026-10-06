using System.IO;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Tray;
using Iris.Core.Actions;
using Iris.Core.DependencyInjection;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Ocr;
using Iris.Core.Presentation;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Iris.Platform.Windows.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

static class AppChecks
{
    static readonly TimeSpan Limit=TimeSpan.FromSeconds(15);
    internal static async Task RunAsync(bool failRelease)
    {
        var main=new PipelineProvider("main");
        var snapshot=new PipelineSnapshot{FailRelease=failRelease,Captured=new(){Ocr=new(){ActiveProviderId="main",FallbackProviderId=null}},Entries=new[]{new OcrProviderRegistration(new(){Id="main",DisplayName="main",Kind=OcrProviderKind.WindowsLocal},main,Guid.NewGuid())}};
        var registry=new PipelineRegistry{Factory=()=>snapshot};
        var images=new WinRtImagePreprocessor(new WinRtImagePreparationFactory());
        var settings=new ReviewSettings(new());var services=new ServiceCollection();
        services.AddSingleton<ISettingsService>(settings);services.AddSingleton<ISecretStore>(new ReviewSecrets());services.AddSingleton<IImagePreprocessor>(images);services.AddSingleton<IOcrProviderRegistry>(registry);
        services.AddSingleton(typeof(ILogger<>),typeof(NullLogger<>));services.AddIrisCore();
        await using var container=services.BuildServiceProvider();var service=container.GetRequiredService<IOcrService>();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var canceled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        main.Body=async(request,token)=>{using var registration=token.Register(()=>canceled.TrySetResult());entered.TrySetResult();await release.Task;return new OcrResult("main",new[]{new OcrLine("done")},null,TimeSpan.Zero);};
        var image=new ImageData(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory,"fixtures","hello-123.png")),ImageFormat.Png,800,180);
        int hostStops=0,exits=0;bool sawStoppedPreprocessor=false;
        registry.OnStop=async()=>
        {
            try{await images.PrepareAsync(image,main.Limits,CancellationToken.None);}catch(IrisException){sawStoppedPreprocessor=true;}
        };
        var host=Substitute.For<IHost>();host.StopAsync(Arg.Any<CancellationToken>()).Returns(_=>{hostStops++;PipelineScope.Check("HOST_AFTER_OCR_AND_PREPROCESSOR_"+failRelease,sawStoppedPreprocessor && snapshot.Releases>0);return Task.CompletedTask;});
        var theme=new ThemeService(settings);var log=new LogLevelController(settings,new Serilog.Core.LoggingLevelSwitch(),NullLogger<LogLevelController>.Instance);
        var tray=new TrayIconController(Substitute.For<IAppLifetime>(),settings,Substitute.For<IActionDispatcher>(),NullLogger<TrayIconController>.Instance){SuppressIconCreationForTesting=true};
        var bootstrap=new AppBootstrapper(settings,Substitute.For<ITranslationProviderRegistry>(),Substitute.For<IDatabaseInitializer>(),log,theme,tray,new TrayNotificationService(NullLogger<TrayNotificationService>.Instance),host,NullLogger<AppBootstrapper>.Instance,new AppStartupInfo(0),imagePreprocessor:images,ocrRegistry:registry,ocrService:service);
        var original=AppBootstrapper.AppShutdowner;AppBootstrapper.AppShutdowner=_=>exits++;
        Task<OcrOutcome>? work=null;Task? stop=null;
        try
        {
            work=service.RecognizeAsync(image);await entered.Task.WaitAsync(Limit);stop=bootstrap.StopAsync();await canceled.Task.WaitAsync(Limit);
            var permitted=await images.PrepareAsync(image,main.Limits,CancellationToken.None).WaitAsync(Limit);
            PipelineScope.Check("PREPROCESSOR_ALIVE_WHILE_OCR_DRAINS_"+failRelease,ReferenceEquals(permitted,image) && !work.IsCompleted && !stop.IsCompleted && registry.Stops==0 && hostStops==0);
            release.TrySetResult();Exception? requestError=null;try{await work.WaitAsync(Limit);}catch(Exception ex){requestError=ex;}
            Exception? stopError=null;try{await stop.WaitAsync(Limit);}catch(Exception ex){stopError=ex;}
            if(failRelease)
            {
                PipelineScope.Check("OCR_CLEANUP_FAILURE_PREVENTS_DEPENDENCY_DISPOSAL",requestError is IrisException && stopError is IrisException && hostStops==0 && exits==0 && !sawStoppedPreprocessor);
                _ = await images.PrepareAsync(image,main.Limits,CancellationToken.None).WaitAsync(Limit);
                snapshot.FailRelease=false;stop=bootstrap.StopAsync();await stop.WaitAsync(Limit);
            }
            else PipelineScope.Check("OCR_CANCEL_DRAINS_BEFORE_SUCCESSFUL_EXIT",requestError is OperationCanceledException && stopError==null);
            PipelineScope.Check("FINAL_BOOTSTRAP_EXIT_NO_RECOGNITION_REPLAY_"+failRelease,hostStops==1 && exits==1 && main.Requests.Count==1 && sawStoppedPreprocessor);
        }
        finally
        {
            snapshot.FailRelease=false;release.TrySetResult();var errors=new List<Exception>();
            if(work!=null)try{await work.WaitAsync(Limit);}catch(Exception){}
            if(stop!=null)try{await stop.WaitAsync(Limit);}catch(Exception){}
            try{await bootstrap.StopAsync().WaitAsync(Limit);}catch(Exception ex){errors.Add(ex);}
            try{await service.StopAsync().WaitAsync(Limit);}catch(Exception ex){errors.Add(ex);}
            try{await images.StopAsync().WaitAsync(Limit);}catch(Exception ex){errors.Add(ex);}
            theme.Dispose();log.Dispose();tray.Dispose();AppBootstrapper.AppShutdowner=original;
            if(errors.Count!=0)throw new AggregateException("App pipeline cleanup failed.",errors);
        }
    }
}
