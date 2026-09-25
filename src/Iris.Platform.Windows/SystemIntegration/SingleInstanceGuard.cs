using System;
using System.Security.Principal;
using System.Threading;
using Iris.Core.Actions;

namespace Iris.Platform.Windows.SystemIntegration;

public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private bool _hasHandle;
    private bool _disposed;

    private SingleInstanceGuard(Mutex mutex)
    {
        _mutex = mutex;
        _hasHandle = true;
    }

    /// <summary>获取当前用户的 Mutex 名称，供测试断言与内部使用。</summary>
    internal static string GetCurrentUserMutexName()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("无法获取当前用户的 SID。");
        return IpcConstants.MutexNamePrefix + sid;
    }

    /// <summary>名称 = IpcConstants.MutexNamePrefix + 当前用户 SID。已有实例时返回 null。</summary>
    public static SingleInstanceGuard? TryAcquireForCurrentUser()
    {
        return TryAcquire(GetCurrentUserMutexName());
    }

    /// <summary>测试用：指定完整的 Mutex 名称。</summary>
    internal static SingleInstanceGuard? TryAcquire(string mutexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);

        var mutex = new Mutex(initiallyOwned: false, mutexName);
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // 上一个实例异常退出未释放 Mutex，当前线程已自动获得所有权
            acquired = true;
        }

        if (!acquired)
        {
            mutex.Dispose();
            return null;
        }

        return new SingleInstanceGuard(mutex);
    }

    /// <summary>释放并关闭 Mutex。必须在调用 TryAcquire 的同一线程上调用；重复调用无副作用。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_hasHandle)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 非获取线程调用或已释放，忽略
            }

            _hasHandle = false;
        }

        _mutex.Dispose();
    }
}
