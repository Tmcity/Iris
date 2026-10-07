using System.IO;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.Core.DependencyInjection;
using Iris.Core.Ocr;
using Iris.Core.Platform;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

sealed class RegistryObserver(IOcrProviderRegistry registry):IProviderConfigurationObserver
{
    internal int Calls;
    internal bool Fail;
    internal Func<Task>? BeforeRefresh;
    public async Task RefreshAsync(CancellationToken cancellationToken=default)
    {
        Calls++;
        if(BeforeRefresh!=null)await BeforeRefresh();
        if(Fail)throw new IOException("Controlled apply failure");
        await registry.RefreshAsync(cancellationToken);
    }
}

sealed class ReviewScope:IAsyncDisposable
{
    internal readonly MemorySettings Settings;
    internal readonly MemorySecrets Secrets=new();
    internal readonly TestOcrFactory Cloud=new(OcrProviderKind.BaiduCloud);
    internal readonly TestOcrFactory Local=new(OcrProviderKind.WindowsLocal);
    internal readonly ServiceProvider Root;
    internal readonly IOcrProviderRegistry Registry;
    internal readonly RegistryObserver Observer;
    internal readonly ISettingsEditCoordinator Editor;
    internal readonly SettingsApplyReceiptManager Receipts=new();
    internal readonly GeneralSettingsSaveCoordinator Save;
    internal readonly SettingsViewModel Vm;
    internal readonly SettingsWindow Window;
    internal readonly SettingsWindowSession Session;
    internal readonly ReviewUi Ui;
    internal readonly List<Action> Releases=new();
    internal readonly List<Task> Tasks=new();
    private readonly ReviewSta sta;
    private readonly ThemeService theme;

    // Construct on the owned STA. Core implementations are resolved using public DI.
    internal ReviewScope(ReviewSta sta,AppSettings? initial=null)
    {
        this.sta=sta; Ui=new(sta); Settings=new(initial??new());
        var services=new ServiceCollection();
        services.AddSingleton<ISettingsService>(Settings);
        services.AddSingleton<ISecretStore>(Secrets);
        services.AddSingleton(typeof(ILogger<>),typeof(NullLogger<>));
        services.AddSingleton<ITranslationProviderRegistry>(Substitute.For<ITranslationProviderRegistry>());
        services.AddSingleton<IOcrProviderFactory>(Cloud);
        services.AddSingleton<IOcrProviderFactory>(Local);
        services.AddIrisCore();
        services.RemoveAll<IProviderConfigurationObserver>();
        services.AddSingleton<RegistryObserver>();
        services.AddSingleton<IProviderConfigurationObserver>(s=>s.GetRequiredService<RegistryObserver>());
        Root=services.BuildServiceProvider();
        Registry=Root.GetRequiredService<IOcrProviderRegistry>();
        Observer=Root.GetRequiredService<RegistryObserver>();
        Editor=Root.GetRequiredService<ISettingsEditCoordinator>();
        theme=new(Settings,NullLogger<ThemeService>.Instance);
        Save=new(Settings,Editor,Substitute.For<IAutoStartService>(),Receipts,NullLogger<GeneralSettingsSaveCoordinator>.Instance);
        Vm=new(Settings,theme,Save,Editor,Receipts,Ui,Root.GetRequiredService<IHotkeyBindingPolicy>());
        Window=new(){DataContext=Vm,ShowActivated=false,ShowInTaskbar=false,WindowStartupLocation=System.Windows.WindowStartupLocation.Manual,Left=-24000,Top=-24000};
        Session=new(Root.CreateScope(),Window,Vm,theme,Save);
    }
    internal void Show(){Window.Show();Window.UpdateLayout();}
    internal T Track<T>(T task) where T:Task {Tasks.Add(task);return task;}
    public async ValueTask DisposeAsync()
    {
        foreach(var release in Releases)release();
        Ui.Release();Observer.Fail=false;Observer.BeforeRefresh=null;
        Settings.BeforeUpdate=null;Settings.BeforeMutate=null;Settings.AfterCommit=null;
        Secrets.BeforeGet=Secrets.BeforeSet=Secrets.BeforeRemove=null;
        foreach(var provider in Cloud.Created.Concat(Local.Created)){provider.FailDispose=false;provider.BeforeDispose=null;}
        // Preserve the owner if shutdown fails; never stop a registry holding window leases.
        await sta.Run(()=>Session.ShutdownAsync());
        var errors=new List<Exception>();
        foreach(var task in Tasks)try{await task;}catch(Exception ex){errors.Add(ex);}
        try{await Registry.StopAsync();await Root.DisposeAsync();}catch(Exception ex){errors.Add(ex);}
        await sta.Run(()=>{Save.Dispose();theme.Dispose();return Task.CompletedTask;});
        if(errors.Count>0)throw new AggregateException(errors);
    }
}
