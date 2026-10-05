using Iris.Core.Imaging;

namespace Iris.Core.Platform;

public enum SelectionGesture
{
    Drag,
    DoubleClick,
    TripleClick,
    ShiftClick,
}

public sealed class SelectionGestureEventArgs(PixelPoint cursorPosition, SelectionGesture gesture, ForegroundAppInfo? app) : EventArgs
{
    public PixelPoint CursorPosition { get; } = cursorPosition;
    public SelectionGesture Gesture { get; } = gesture;
    public ForegroundAppInfo? App { get; } = app;
    public long Generation { get; init; }
    public override string ToString() => $"SelectionGestureEventArgs {{ Gesture={Gesture}, Generation={Generation} }}";
}

public sealed class MouseSelectionInputEventArgs(PixelPoint cursorPosition, long generation) : EventArgs
{
    public PixelPoint CursorPosition { get; } = cursorPosition;
    public long Generation { get; } = generation;
    public override string ToString() => $"MouseSelectionInputEventArgs {{ Generation={Generation} }}";
}

public sealed class MouseSelectionInvalidatedEventArgs(long generation, Iris.Core.Errors.ErrorInfo? error = null) : EventArgs
{
    public long Generation { get; } = generation;
    public Iris.Core.Errors.ErrorInfo? Error { get; } = error;
    public override string ToString() => $"MouseSelectionInvalidatedEventArgs {{ Generation={Generation}, Failed={Error != null} }}";
}

public sealed record MouseSelectionMonitorOptions(int MinDragDistancePx, bool RequireIBeamCursor, IReadOnlyList<string> ExcludedProcesses)
{
    public override string ToString() => "MouseSelectionMonitorOptions";
}

/// <summary>
/// 低级鼠标钩子（WH_MOUSE_LL），构造无 I/O；回调仅作获批的有界元数据快照与入队，不阻止输入。
/// 事件在后台有序发布；消费者在 UI 实际接受时核对 Generation。完整规则见 MOUSE_SELECTION_MONITOR.md。
/// IsRunning 只表示逻辑接纳，不保证能检测系统静默移除；同步 Dispose 只允许从未使用或已完整停止。
/// </summary>
public interface IMouseSelectionMonitor : IDisposable, IAsyncDisposable
{
    bool IsRunning { get; }

    long Generation { get; }

    /// <summary>首次等待前捕获 options；顺序重复启动先完整停止旧 run。预取消/非法参数不影响当前 run。
    /// 安装/清理失败安全抛 HookInstallFailed；并发启动或清理未完明确拒绝，不无限排队。</summary>
    Task StartAsync(MouseSelectionMonitorOptions options, CancellationToken cancellationToken = default);

    /// <summary>先逻辑失效，再等待真实卸载、回调、消费者和消息线程退出；并发停止共享清理，失败保留资源可重试。</summary>
    Task StopAsync();

    event EventHandler<SelectionGestureEventArgs>? SelectionGestureDetected;

    /// <summary>收到的任意按钮 down，携带原 run 代次，不直接在 native hook 中调用订阅者。</summary>
    event EventHandler<MouseSelectionInputEventArgs>? MouseDown;

    /// <summary>停止/丢包等逻辑撤销；已知运行故障可带安全默认错误，不表示物理资源一定已清理。</summary>
    event EventHandler<MouseSelectionInvalidatedEventArgs>? Invalidated;
}
