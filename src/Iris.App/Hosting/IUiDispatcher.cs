using System;
using System.Threading;
using System.Threading.Tasks;

namespace Iris.App.Hosting;

/// <summary>
/// App 内部 UI 调度接口，按 ARCH §6.1 调度异步委托到 UI 线程并等待内部任务完成。
/// </summary>
internal interface IUiDispatcher
{
    /// <summary>
    /// 在 UI 线程执行异步回调并等待内部任务完成。
    /// </summary>
    Task InvokeAsync(Func<Task> callback, CancellationToken cancellationToken);

    /// <summary>
    /// 判断当前调用线程是否具有访问 UI 线程的权限。
    /// </summary>
    bool CheckAccess();
}
