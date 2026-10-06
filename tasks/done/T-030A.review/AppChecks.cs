using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.App.Tray;
using Iris.Core.Actions;
using Iris.Core.Ocr;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

static class AppChecks
{
    static readonly TimeSpan Limit=TimeSpan.FromSeconds(15);
    internal static async Task RunAsync(string mode)
    {
        var settings=new ReviewSettings(new(){Ocr=new(){Providers=Array.AsReadOnly(new[]{new OcrProviderProfile{Id="local",Kind=OcrProviderKind.WindowsLocal,DisplayName="local",WindowsOptions=new()}}),ActiveProviderId="local",FallbackProviderId=null}});
        var factory=new ReviewFactory();var registry=RegistryChecks.Create(settings,new(),factory);
        IOcrProviderSnapshot? lease=await registry.AcquireSnapshotAsync().WaitAsync(Limit);
        var provider=(ReviewProvider)lease.Providers.Single().Provider;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner=Substitute.For<ISettingsShutdownCoordinator>();int ownerCalls=0,hostStops=0,shutdowns=0;
        owner.ShutdownAsync(Arg.Any<CancellationToken>()).Returns(async _=>
        {
            int attempt=Interlocked.Increment(ref ownerCalls);entered.TrySetResult();
            if(mode=="owner-failure" && attempt==1)throw new InvalidOperationException("controlled owner failure");
            await release.Task;
            if(lease!=null){await lease.DisposeAsync();lease=null;}
        });
        var capture=Substitute.For<IHotkeyCaptureService>();int captureStops=0;
        capture.StopAsync().Returns(_=>mode=="early-failure" && Interlocked.Increment(ref captureStops)==1 ? Task.FromException(new InvalidOperationException("controlled native failure")) : Task.CompletedTask);
        var theme=new ThemeService(settings);
        var log=new LogLevelController(settings,new Serilog.Core.LoggingLevelSwitch(),NullLogger<LogLevelController>.Instance);
        var tray=new TrayIconController(Substitute.For<IAppLifetime>(),settings,Substitute.For<IActionDispatcher>(),NullLogger<TrayIconController>.Instance){SuppressIconCreationForTesting=true};
        var host=Substitute.For<IHost>();
        host.StopAsync(Arg.Any<CancellationToken>()).Returns(_=>
        {
            RegistryChecks.Check("HOST_STOPS_ONLY_AFTER_PROVIDER_DISPOSAL_"+mode,provider.Disposed);
            Interlocked.Increment(ref hostStops);return Task.CompletedTask;
        });
        var bootstrap=new AppBootstrapper(settings,Substitute.For<ITranslationProviderRegistry>(),Substitute.For<IDatabaseInitializer>(),log,theme,tray,new TrayNotificationService(NullLogger<TrayNotificationService>.Instance),host,NullLogger<AppBootstrapper>.Instance,new AppStartupInfo(System.Diagnostics.Stopwatch.GetTimestamp()),settingsShutdownCoordinator:owner,captureService:capture,ocrRegistry:registry);
        var original=AppBootstrapper.AppShutdowner;AppBootstrapper.AppShutdowner=_=>Interlocked.Increment(ref shutdowns);
        Task? stopping=null;
        try
        {
            stopping=bootstrap.StopAsync();await entered.Task.WaitAsync(Limit);
            bool refused=false;IOcrProviderSnapshot? accidental=null;
            try{accidental=await registry.AcquireSnapshotAsync();}catch(ObjectDisposedException){refused=true;}
            if(accidental!=null)await accidental.DisposeAsync();
            RegistryChecks.Check("BOOTSTRAP_CLOSES_NEW_OCR_ADMISSION_"+mode,refused && hostStops==0);
            if(mode=="owner-failure")
            {
                Exception? error=null;try{await stopping.WaitAsync(Limit);}catch(Exception ex){error=ex;}
                RegistryChecks.Check("OWNER_FAILURE_REPORTS_WITHOUT_ABANDONING_LEASE",error!=null && error is not TimeoutException && !provider.Disposed && hostStops==0 && shutdowns==0);
                release.TrySetResult();stopping=bootstrap.StopAsync();await stopping.WaitAsync(Limit);
                RegistryChecks.Check("OWNER_RETRY_DRAINS_PENDING_REGISTRY",provider.Disposed && hostStops==1 && shutdowns==1 && ownerCalls==2);
            }
            else
            {
                RegistryChecks.Check("BOOTSTRAP_WAITS_FOR_OWNER_"+mode,!stopping.IsCompleted && !provider.Disposed);
                release.TrySetResult();Exception? error=null;try{await stopping.WaitAsync(Limit);}catch(Exception ex){error=ex;}
                if(mode=="early-failure")
                {
                    RegistryChecks.Check("EARLY_FAILURE_STILL_DRAINS_OCR_BEFORE_REPORT",error!=null && provider.Disposed && hostStops==0 && shutdowns==0);
                    stopping=bootstrap.StopAsync();await stopping.WaitAsync(Limit);
                    RegistryChecks.Check("EARLY_FAILURE_RETRY_COMPLETES_HOST",hostStops==1 && shutdowns==1);
                }
                else RegistryChecks.Check("NORMAL_BOOTSTRAP_EXIT_COMPLETES",error==null && provider.Disposed && hostStops==1 && shutdowns==1);
            }
        }
        finally
        {
            release.TrySetResult();
            if(stopping!=null)try{await stopping.WaitAsync(Limit);}catch(Exception){}
            try{await RegistryChecks.FinishAsync(registry,factory,lease);}
            finally{theme.Dispose();log.Dispose();tray.Dispose();AppBootstrapper.AppShutdowner=original;}
        }
    }
}
