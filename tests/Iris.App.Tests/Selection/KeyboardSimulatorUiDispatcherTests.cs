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

        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            Exception? caughtEx = null;

            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    int uiThreadId = Environment.CurrentManagedThreadId;
                    var fakeNative = new ThreadTrackingFakeNativeAdapter(uiThreadId);
                    fakeNative.SendReleaseTcs.TrySetResult(); // 不阻塞发送

                    await using var simulator = new KeyboardSimulator(fakeNative);
                    var result = await simulator.CopyAsync(Target, TestContext.Current.CancellationToken);

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
            thread.Join(3000).ShouldBeTrue("WPF STA 线程应在完成调度后正常退出");

            outcome.ShouldBe(KeyboardCopyOutcome.Injected);
            nativeCallsOnUi.ShouldBe(0, "所有原生状态/目标查询必须完全离开 UI 线程");
            sendCallsOnUi.ShouldBe(0, "所有 SendInput 调用必须完全离开 UI 线程");
            totalSends.ShouldBe(4, "四事件标准序列完整发送");
        }
        finally
        {
            if (thread.IsAlive)
            {
                thread.Join(1000);
            }
        }
    }

    [Fact]
    public async Task CopyAsync_WhenNativeSendBlocked_UiDispatcherRemainsResponsiveToCallbacks()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool independentCallbackProcessed = false;
        int nativeCallsOnUi = -1;
        int sendCallsOnUi = -1;

        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            Exception? caughtEx = null;

            dispatcher.InvokeAsync(async () =>
            {
                ThreadTrackingFakeNativeAdapter? fakeNative = null;
                Task<KeyboardCopyResult>? copyTask = null;
                try
                {
                    int uiThreadId = Environment.CurrentManagedThreadId;
                    fakeNative = new ThreadTrackingFakeNativeAdapter(uiThreadId);

                    await using var simulator = new KeyboardSimulator(fakeNative);

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
                    fakeNative?.SendReleaseTcs.TrySetResult();
                    if (copyTask != null)
                    {
                        try { await copyTask; } catch { }
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
            thread.Join(3000).ShouldBeTrue("WPF STA 线程应正常退出");

            independentCallbackProcessed.ShouldBeTrue("原生调用阻塞期间 UI 线程必须仍能处理独立事件回调");
            nativeCallsOnUi.ShouldBe(0);
            sendCallsOnUi.ShouldBe(0);
        }
        finally
        {
            if (thread.IsAlive)
            {
                thread.Join(1000);
            }
        }
    }

    [Fact]
    public async Task StopAsync_WhenNativeSendBlocked_AwaitsBackgroundReleaseCleanly()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool stopCompletedCleanly = false;

        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            Exception? caughtEx = null;

            dispatcher.InvokeAsync(async () =>
            {
                ThreadTrackingFakeNativeAdapter? fakeNative = null;
                Task<KeyboardCopyResult>? copyTask = null;
                Task? stopTask = null;
                try
                {
                    int uiThreadId = Environment.CurrentManagedThreadId;
                    fakeNative = new ThreadTrackingFakeNativeAdapter(uiThreadId);

                    await using var simulator = new KeyboardSimulator(fakeNative);

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
                    fakeNative?.SendReleaseTcs.TrySetResult();
                    if (copyTask != null)
                    {
                        try { await copyTask; } catch { }
                    }
                    if (stopTask != null)
                    {
                        try { await stopTask; } catch { }
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
            thread.Join(3000).ShouldBeTrue("WPF STA 线程应正常退出");

            stopCompletedCleanly.ShouldBeTrue("在途 Native 阻塞时 StopAsync 必须等待后台在途工作并干净收尾");
        }
        finally
        {
            if (thread.IsAlive)
            {
                thread.Join(1000);
            }
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
