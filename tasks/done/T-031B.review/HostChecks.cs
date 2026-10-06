using System.IO;
using Iris.App.Hosting;
using Iris.App.Translation;
using Iris.Core.Actions;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

static class HostChecks
{
    internal static async Task Run(ReviewSta sta)
    {
        var root=Path.Combine(Path.GetTempPath(),"iris-t031b-root-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var paths=Substitute.For<IAppPaths>();paths.DataDirectory.Returns(root);
        paths.SettingsFile.Returns(Path.Combine(root,"settings.json"));paths.SecretsFile.Returns(Path.Combine(root,"secrets.dat"));
        paths.DatabaseFile.Returns(Path.Combine(root,"history.db"));paths.LogDirectory.Returns(Path.Combine(root,"logs"));
        var settings=Substitute.For<ISettingsService>();settings.Current.Returns(new AppSettings());
        await sta.Run(async()=>
        {
            var host=AppHostBuilder.Build([],paths,new Serilog.Core.LoggingLevelSwitch(),0,services=>
            {
                services.AddSingleton(settings);services.AddSingleton(Substitute.For<ISecretStore>());
                services.AddSingleton<IUiDispatcher>(new ReviewUi(sta));
                services.AddSingleton(Substitute.For<IClipboardService>());
            });
            var old=AppBootstrapper.AppShutdowner;int exits=0;AppBootstrapper.AppShutdowner=_=>exits++;
            try
            {
                var dispatcher=host.Services.GetRequiredService<IActionDispatcher>();
                var actions=host.Services.GetServices<IActionHandler>().Select(h=>h.Action).ToArray();
                if(new[]{IrisAction.ScreenshotTranslate,IrisAction.ScreenshotOcr,IrisAction.SilentScreenshotOcr}.Any(a=>actions.Count(x=>x==a)!=1))throw new InvalidOperationException("Screenshot registration mismatch");
                Console.WriteLine("HOST_THREE_REAL_HANDLERS_RESOLVE=PASS");
                var screenshot=host.Services.GetRequiredService<IScreenshotService>();
                if(!ReferenceEquals(screenshot,host.Services.GetRequiredService<Iris.App.Capture.ScreenshotService>()))throw new InvalidOperationException("Screenshot singleton split");
                if(host.Services.GetRequiredService<TranslateWindowPresenter>().Window!=null)throw new InvalidOperationException("Host constructed a UI window");
                Console.WriteLine("HOST_REAL_CAPTURE_GRAPH_LAZY_SINGLETON=PASS");
                await host.Services.GetRequiredService<AppBootstrapper>().StopAsync();
                if(exits!=1)throw new InvalidOperationException("Host shutdown did not finish");
                Console.WriteLine("HOST_REAL_BOOTSTRAPPER_STOPS=PASS");
            }
            finally
            {
                try{await host.Services.GetRequiredService<AppBootstrapper>().StopAsync();await ((IAsyncDisposable)host).DisposeAsync();}
                finally{AppBootstrapper.AppShutdowner=old;}
            }
            Console.WriteLine("HOST_ASYNC_DISPOSAL_COMPLETES=PASS");
        });
    }
}
