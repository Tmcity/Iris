using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Iris.Platform.Windows.Selection;
using Shouldly;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Xunit;

namespace Iris.App.Tests.Selection;

public sealed class KeyboardSimulatorUiDispatcherTests
{
    private static readonly KeyboardCopyTarget Target = new(1, 5000);

    [Fact]
    public async Task CopyAsync_WhenInvokedFromWpfUiDispatcher_ExecutesNativeCallsOffUi()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int nativeCallsOnUi = -1;
        int sendCallsOnUi = -1;
        int totalSends = -1;
        KeyboardCopyOutcome? outcome = null;
        ThreadTrackingFakeNativeAdapter? fakeNativeRef = null;

        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            Exception? caughtEx = null;

            dispatcher.InvokeAsync(async () =>
            {
                ThreadTrackingFakeNativeAdapter? fakeNative = null;
                KeyboardSimulator? simulator = null;
                Task<KeyboardCopyResult>? copyTask = null;
                try
                {
                    int uiThreadId = Environment.CurrentManagedThreadId;
                    fakeNative = new ThreadTrackingFakeNativeAdapter(uiThreadId);
                    fakeNativeRef = fakeNative;
                    fakeNative.SendReleaseTcs.TrySetResult(); // 不阻塞发送

                    simulator = new KeyboardSimulator(fakeNative);
                    copyTask = simulator.CopyAsync(Target, TestContext.Current.CancellationToken);
                    var result = await copyTask;

                    outcome = result.Outcome;
                    nativeCallsOnUi = fakeNative.UiCalls;
                    sendCallsOnUi = fakeNative.UiSends;
                    totalSends = fakeNative.TotalSends;
                }
                catch (Exception ex)
                {
                    caughtEx = ex;
                }
                finally
                {
                    fakeNative?.SendReleaseTcs.TrySetResult();
                    if (copyTask != null)
                    {
                        try { await copyTask.ConfigureAwait(false); } catch { }
                    }
                    if (simulator != null)
                    {
                        try
                        {
                            await simulator.DisposeAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            caughtEx = caughtEx == null ? ex : new AggregateException(caughtEx, ex);
                        }
                    }
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });

            Dispatcher.Run();

            if (caughtEx != null)
            {
                done.TrySetException(caughtEx);
            }
            else
            {
                done.TrySetResult();
            }
        });

        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        try
        {
            await done.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            outcome.ShouldBe(KeyboardCopyOutcome.Injected);
            nativeCallsOnUi.ShouldBe(0, "所有原生状态/目标查询必须完全离开 UI 线程");
            sendCallsOnUi.ShouldBe(0, "所有 SendInput 调用必须完全离开 UI 线程");
            totalSends.ShouldBe(4, "四事件标准序列完整发送");
        }
        finally
        {
            fakeNativeRef?.SendReleaseTcs.TrySetResult();
            thread.Join(3000).ShouldBeTrue("WPF STA 线程应在完成调度后正常退出");
        }
    }

    [Fact]
    public async Task CopyAsync_WhenNativeSendBlocked_UiDispatcherRemainsResponsiveToCallbacks()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool independentCallbackProcessed = false;
        int nativeCallsOnUi = -1;
        int sendCallsOnUi = -1;
        ThreadTrackingFakeNativeAdapter? fakeNativeRef = null;

        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            Exception? caughtEx = null;

            dispatcher.InvokeAsync(async () =>
            {
                ThreadTrackingFakeNativeAdapter? fakeNative = null;
                KeyboardSimulator? simulator = null;
                Task<KeyboardCopyResult>? copyTask = null;
                try
                {
                    int uiThreadId = Environment.CurrentManagedThreadId;
                    fakeNative = new ThreadTrackingFakeNativeAdapter(uiThreadId);
                    fakeNativeRef = fakeNative;

                    simulator = new KeyboardSimulator(fakeNative);

                    // 在 UI 线程启动 CopyAsync
                    copyTask = simulator.CopyAsync(Target, TestContext.Current.CancellationToken);

                    // 等待后台进入 SendInput 阻塞点
                    await fakeNative.SendEnteredTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

                    // 验证此时 UI 仍然响应独立回调
                    await dispatcher.InvokeAsync(() =>
                    {
                        independentCallbackProcessed = true;
                    });

                    // 放行后台 SendInput
                    fakeNative.SendReleaseTcs.TrySetResult();

                    var result = await copyTask;
                    result.Outcome.ShouldBe(KeyboardCopyOutcome.Injected);

                    nativeCallsOnUi = fakeNative.UiCalls;
                    sendCallsOnUi = fakeNative.UiSends;
                }
                catch (Exception ex)
                {
                    caughtEx = ex;
                }
                finally
                {
                    // 先放行闸门，再收尾任务与模拟器，杜绝 DisposeAsync 与闸门形成死锁
                    fakeNative?.SendReleaseTcs.TrySetResult();
                    if (copyTask != null)
                    {
                        try { await copyTask.ConfigureAwait(false); } catch { }
                    }
                    if (simulator != null)
                    {
                        try
                        {
                            await simulator.DisposeAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            caughtEx = caughtEx == null ? ex : new AggregateException(caughtEx, ex);
                        }
                    }
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });

            Dispatcher.Run();

            if (caughtEx != null)
            {
                done.TrySetException(caughtEx);
            }
            else
            {
                done.TrySetResult();
            }
        });

        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        try
        {
            await done.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            independentCallbackProcessed.ShouldBeTrue("原生调用阻塞期间 UI 线程必须仍能处理独立事件回调");
            nativeCallsOnUi.ShouldBe(0);
            sendCallsOnUi.ShouldBe(0);
        }
        finally
        {
            fakeNativeRef?.SendReleaseTcs.TrySetResult();
            thread.Join(3000).ShouldBeTrue("WPF STA 线程应正常退出");
        }
    }

    [Fact]
    public async Task StopAsync_WhenNativeSendBlocked_AwaitsBackgroundReleaseCleanly()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool stopCompletedCleanly = false;
        ThreadTrackingFakeNativeAdapter? fakeNativeRef = null;

        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            Exception? caughtEx = null;

            dispatcher.InvokeAsync(async () =>
            {
                ThreadTrackingFakeNativeAdapter? fakeNative = null;
                KeyboardSimulator? simulator = null;
                Task<KeyboardCopyResult>? copyTask = null;
                Task? stopTask = null;
                try
                {
                    int uiThreadId = Environment.CurrentManagedThreadId;
                    fakeNative = new ThreadTrackingFakeNativeAdapter(uiThreadId);
                    fakeNativeRef = fakeNative;

                    simulator = new KeyboardSimulator(fakeNative);

                    copyTask = simulator.CopyAsync(Target, TestContext.Current.CancellationToken);

                    // 等待进入 SendInput
                    await fakeNative.SendEnteredTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

                    // 发起 StopAsync，它必须等待后台在途工作完成
                    stopTask = simulator.StopAsync();
                    stopTask.IsCompleted.ShouldBeFalse("StopAsync 必须等待后台在途 native 工作完成");

                    // 放行后台 SendInput
                    fakeNative.SendReleaseTcs.TrySetResult();

                    await copyTask;
                    await stopTask;
                    stopCompletedCleanly = true;
                }
                catch (Exception ex)
                {
                    caughtEx = ex;
                }
                finally
                {
                    // 先放行闸门，再收尾任务与模拟器，杜绝 DisposeAsync 与闸门形成死锁
                    fakeNative?.SendReleaseTcs.TrySetResult();
                    if (copyTask != null)
                    {
                        try { await copyTask.ConfigureAwait(false); } catch { }
                    }
                    if (stopTask != null)
                    {
                        try { await stopTask.ConfigureAwait(false); } catch { }
                    }
                    if (simulator != null)
                    {
                        try
                        {
                            await simulator.DisposeAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            caughtEx = caughtEx == null ? ex : new AggregateException(caughtEx, ex);
                        }
                    }
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });

            Dispatcher.Run();

            if (caughtEx != null)
            {
                done.TrySetException(caughtEx);
            }
            else
            {
                done.TrySetResult();
            }
        });

        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        try
        {
            await done.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            stopCompletedCleanly.ShouldBeTrue("在途 Native 阻塞时 StopAsync 必须等待后台在途工作并干净收尾");
        }
        finally
        {
            fakeNativeRef?.SendReleaseTcs.TrySetResult();
            thread.Join(3000).ShouldBeTrue("WPF STA 线程应正常退出");
        }
    }

    [Fact]
    public async Task CopyAsync_WhenTestBodyFailsWhileNativeSendBlocked_ReleasesGateAndExitsCleanlyWithoutDeadlock()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ThreadTrackingFakeNativeAdapter? fakeNativeRef = null;

        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            Exception? caughtEx = null;

            dispatcher.InvokeAsync(async () =>
            {
                ThreadTrackingFakeNativeAdapter? fakeNative = null;
                KeyboardSimulator? simulator = null;
                Task<KeyboardCopyResult>? copyTask = null;
                try
                {
                    int uiThreadId = Environment.CurrentManagedThreadId;
                    fakeNative = new ThreadTrackingFakeNativeAdapter(uiThreadId);
                    fakeNativeRef = fakeNative;

                    simulator = new KeyboardSimulator(fakeNative);

                    // 在 UI 线程启动 CopyAsync
                    copyTask = simulator.CopyAsync(Target, TestContext.Current.CancellationToken);

                    // 等待后台进入 SendInput 阻塞点（此时 SendReleaseTcs 仍未放行）
                    await fakeNative.SendEnteredTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

                    // 故意注入受控测试体异常，验证在闸门未打开时异常展开不发生死锁
                    throw new InvalidOperationException("Simulated test body failure during blocked send");
                }
                catch (Exception ex)
                {
                    caughtEx = ex;
                }
                finally
                {
                    // 核心修复验证：先放行闸门，再收尾任务与模拟器
                    fakeNative?.SendReleaseTcs.TrySetResult();
                    if (copyTask != null)
                    {
                        try { await copyTask.ConfigureAwait(false); } catch { }
                    }
                    if (simulator != null)
                    {
                        try
                        {
                            await simulator.DisposeAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            caughtEx = caughtEx == null ? ex : new AggregateException(caughtEx, ex);
                        }
                    }
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });

            Dispatcher.Run();

            if (caughtEx != null)
            {
                done.TrySetException(caughtEx);
            }
            else
            {
                done.TrySetResult();
            }
        });

        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        try
        {
            // 断言异常直接可见且不被 TimeoutException 替代
            var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
                done.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            ex.Message.ShouldBe("Simulated test body failure during blocked send");
        }
        finally
        {
            fakeNativeRef?.SendReleaseTcs.TrySetResult();
            thread.Join(3000).ShouldBeTrue("STA 线程必须在发生测试体异常后正常收尾退出，不得悬挂或死锁");
        }
    }

    [Fact]
    public async Task StopAsync_WhenTestBodyFailsWhileNativeSendBlocked_ReleasesGateAndExitsCleanlyWithoutDeadlock()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ThreadTrackingFakeNativeAdapter? fakeNativeRef = null;

        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            Exception? caughtEx = null;

            dispatcher.InvokeAsync(async () =>
            {
                ThreadTrackingFakeNativeAdapter? fakeNative = null;
                KeyboardSimulator? simulator = null;
                Task<KeyboardCopyResult>? copyTask = null;
                Task? stopTask = null;
                try
                {
                    int uiThreadId = Environment.CurrentManagedThreadId;
                    fakeNative = new ThreadTrackingFakeNativeAdapter(uiThreadId);
                    fakeNativeRef = fakeNative;

                    simulator = new KeyboardSimulator(fakeNative);

                    copyTask = simulator.CopyAsync(Target, TestContext.Current.CancellationToken);

                    // 等待进入 SendInput
                    await fakeNative.SendEnteredTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

                    stopTask = simulator.StopAsync();

                    // 故意注入受控测试体异常，验证在 StopAsync 正在等待时异常展开不发生死锁
                    throw new InvalidOperationException("Simulated test body failure during blocked stop");
                }
                catch (Exception ex)
                {
                    caughtEx = ex;
                }
                finally
                {
                    // 核心修复验证：先放行闸门，再收尾任务与模拟器
                    fakeNative?.SendReleaseTcs.TrySetResult();
                    if (copyTask != null)
                    {
                        try { await copyTask.ConfigureAwait(false); } catch { }
                    }
                    if (stopTask != null)
                    {
                        try { await stopTask.ConfigureAwait(false); } catch { }
                    }
                    if (simulator != null)
                    {
                        try
                        {
                            await simulator.DisposeAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            caughtEx = caughtEx == null ? ex : new AggregateException(caughtEx, ex);
                        }
                    }
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });

            Dispatcher.Run();

            if (caughtEx != null)
            {
                done.TrySetException(caughtEx);
            }
            else
            {
                done.TrySetResult();
            }
        });

        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        try
        {
            // 断言异常直接可见且不被 TimeoutException 替代
            var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
                done.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            ex.Message.ShouldBe("Simulated test body failure during blocked stop");
        }
        finally
        {
            fakeNativeRef?.SendReleaseTcs.TrySetResult();
            thread.Join(3000).ShouldBeTrue("STA 线程必须在发生测试体异常后正常收尾退出，不得悬挂或死锁");
        }
    }

    private sealed class ThreadTrackingFakeNativeAdapter : INativeKeyboardInputAdapter
    {
        private readonly int _uiThreadId;

        public int UiCalls { get; private set; }
        public int UiSends { get; private set; }
        public int TotalSends { get; private set; }

        public TaskCompletionSource SendEnteredTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SendReleaseTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ThreadTrackingFakeNativeAdapter(int uiThreadId)
        {
            _uiThreadId = uiThreadId;
        }

        private void Touch()
        {
            if (Environment.CurrentManagedThreadId == _uiThreadId)
            {
                UiCalls++;
            }
        }

        public short GetAsyncKeyState(int key) { Touch(); return 0; }
        public nint GetForegroundWindow() { Touch(); return 1; }
        public uint GetWindowProcessId(nint window) { Touch(); return 5000; }
        public uint GetCurrentProcessId() { Touch(); return 1000; }
        public bool IsWindow(nint window) { Touch(); return true; }

        public uint SendInput(uint count, INPUT[] inputs, int cb)
        {
            Touch();
            if (Environment.CurrentManagedThreadId == _uiThreadId)
            {
                UiSends++;
            }
            TotalSends += (int)count;
            SendEnteredTcs.TrySetResult();
            SendReleaseTcs.Task.GetAwaiter().GetResult();
            return count;
        }

        public int GetLastError() => 0;
    }
}
