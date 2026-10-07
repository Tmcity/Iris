using Iris.Core.Ocr;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Iris.Core.Languages;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

sealed class RegistryObserver(IOcrProviderRegistry registry):IProviderConfigurationObserver
{
    internal int Calls;
    internal bool Fail {get;set;}
    internal Func<Task>? BeforeRefresh {get;set;}
    public async Task RefreshAsync(CancellationToken cancellationToken=default)
    {Interlocked.Increment(ref Calls);if(BeforeRefresh!=null)await BeforeRefresh();if(Fail)throw new InvalidOperationException("Controlled observer failure");await registry.RefreshAsync(cancellationToken);}
}

sealed class ReviewScope:IAsyncDisposable
{
    internal readonly MemorySettings Settings;
    internal readonly MemorySecrets Secrets=new();
    internal readonly TestOcrFactory Cloud=new(OcrProviderKind.BaiduCloud);
    internal readonly TestOcrFactory Local=new(OcrProviderKind.WindowsLocal);
    internal readonly OcrProviderRegistry Registry;
    internal readonly RegistryObserver Observer;
    internal readonly SettingsEditCoordinator Editor;
    internal readonly List<Action> Releases=new();
    internal readonly List<IAsyncDisposable> Sessions=new();
    internal readonly List<(Task Task,bool ExpectedFailure)> Tasks=new();
    internal ReviewScope(AppSettings? initial=null)
    {
        Settings=new(initial??new());
        Registry=new(Settings,Secrets,[Cloud,Local],NullLogger<OcrProviderRegistry>.Instance);
        Observer=new(Registry);
        object[] dependencies=[Settings,Secrets,Registry,new IProviderConfigurationObserver[]{Observer},
            Substitute.For<ITranslationProviderRegistry>(),Substitute.For<IPromptTemplateProvider>(),
            TimeProvider.System,NullLogger<SettingsEditCoordinator>.Instance,new HotkeyBindingPolicy()];
        var constructor=typeof(SettingsEditCoordinator).GetConstructors().Single();
        var args=constructor.GetParameters().Select(p=>dependencies.FirstOrDefault(p.ParameterType.IsInstanceOfType)
            ?? (p.HasDefaultValue?p.DefaultValue:throw new InvalidOperationException("Unmapped dependency: "+p.ParameterType.Name))).ToArray();
        Editor=(SettingsEditCoordinator)constructor.Invoke(args);
    }
    internal T Track<T>(T task,bool expectedFailure=false) where T:Task
    {Tasks.Add((task,expectedFailure));return task;}
    public async ValueTask DisposeAsync()
    {
        foreach(var release in Releases)release();Observer.Fail=false;Observer.BeforeRefresh=null;
        Secrets.BeforeSet=Secrets.BeforeGet=Secrets.BeforeRemove=null;
        foreach(var provider in Cloud.Created.Concat(Local.Created)){provider.FailDispose=false;provider.BeforeDispose=null;}
        var errors=new List<Exception>();
        foreach(var session in Sessions)try{await session.DisposeAsync();}catch(Exception ex){errors.Add(ex);}
        foreach(var (task,expected) in Tasks)try{await task;}catch(Exception ex){if(!expected)errors.Add(ex);}
        try{await Registry.StopAsync();await Registry.DisposeAsync();}catch(Exception ex){errors.Add(ex);}
        if(errors.Count>0)throw new AggregateException(errors);
    }
}
