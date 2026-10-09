using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Threading;
using Iris.App.Hosting;
using Iris.Core.Imaging;

sealed class ReviewSta : IAsyncDisposable
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource<Dispatcher> _ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal ReviewSta()
    {
        _thread=new Thread(()=>
        {
            try
            {
                var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
                System.Reflection.Assembly.Load("Wpf.Ui");
                app.Resources=(ResourceDictionary)XamlReader.Parse("<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:ui='http://schemas.lepo.co/wpfui/2022/xaml'><ResourceDictionary.MergedDictionaries><ui:ThemesDictionary Theme='Light'/><ui:ControlsDictionary/></ResourceDictionary.MergedDictionaries></ResourceDictionary>");
                var dispatcher=Dispatcher.CurrentDispatcher;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                _ready.TrySetResult(dispatcher);Dispatcher.Run();
            }
            catch(Exception ex){_ready.TrySetException(ex);}
        }){IsBackground=true,Name="Iris-Settings-Independent-STA"};
        _thread.SetApartmentState(ApartmentState.STA);_thread.Start();
    }
    internal async Task Run(Func<Task> work)
    {
        var dispatcher=await _ready.Task;await dispatcher.InvokeAsync(work).Task.Unwrap();
    }
    internal async Task<T> Run<T>(Func<T> work)
    {
        var dispatcher=await _ready.Task;return await dispatcher.InvokeAsync(work);
    }
    internal bool CheckAccess()=>_ready.Task.IsCompletedSuccessfully && _ready.Task.Result.CheckAccess();
    public async ValueTask DisposeAsync()
    {
        var dispatcher=await _ready.Task;
        await dispatcher.InvokeAsync(()=>
        {
            Console.WriteLine($"STA finalization: owned windows={Application.Current?.Windows.Count ?? 0}");
            if(Application.Current?.Windows.Count > 0)throw new InvalidOperationException("A scenario left an owned window open.");
            Application.Current?.Shutdown();
        });
        if(!dispatcher.HasShutdownStarted)dispatcher.InvokeShutdown();
        if(!await Task.Run(()=>_thread.Join(TimeSpan.FromSeconds(15))))
            throw new TimeoutException($"Independent STA did not exit: started={dispatcher.HasShutdownStarted}, finished={dispatcher.HasShutdownFinished}, state={_thread.ThreadState}.");
        Console.WriteLine("STA finalization: thread exited");
    }
}

sealed class ReviewUi(ReviewSta sta):IUiDispatcher
{
    private readonly object _gate=new();
    private TaskCompletionSource? _pause;
    private TaskCompletionSource? _entered;
    private int _allowed;
    private bool _once,_used;
    internal Task Pause(int allow=0,bool once=false)
    {
        lock(_gate){_pause=new(TaskCreationOptions.RunContinuationsAsynchronously);_entered=new(TaskCreationOptions.RunContinuationsAsynchronously);_allowed=allow;_once=once;_used=false;return _entered.Task;}
    }
    internal void Release(){TaskCompletionSource? release;lock(_gate){release=_pause;_pause=null;}_entered?.TrySetResult();release?.TrySetResult();}
    public bool CheckAccess()=>sta.CheckAccess();
    public async Task InvokeAsync(Func<Task> callback,CancellationToken token)
    {
        Task? pause;lock(_gate){pause=_once && _used?null:_pause?.Task;if(pause!=null && _allowed>0){_allowed--;pause=null;}if(pause!=null){_used=true;_entered!.TrySetResult();}}
        if(pause!=null)await pause.WaitAsync(token);
        token.ThrowIfCancellationRequested();await sta.Run(callback);
    }
}

