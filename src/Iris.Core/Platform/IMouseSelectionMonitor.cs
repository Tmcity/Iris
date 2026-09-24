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
}

public sealed record MouseSelectionMonitorOptions(int MinDragDistancePx, bool RequireIBeamCursor, IReadOnlyList<string> ExcludedProcesses);

/// <summary>
/// 低级鼠标钩子（WH_MOUSE_LL，独立线程 + 消息循环）。钩子回调只做入队（必须在数毫秒内返回），
/// 手势识别在后台消费者中完成。事件在线程池线程触发。Start 重复调用 = 以新选项重启。
/// 安装失败抛 IrisException(HookInstallFailed)。
/// </summary>
public interface IMouseSelectionMonitor : IDisposable
{
    bool IsRunning { get; }

    void Start(MouseSelectionMonitorOptions options);

    void Stop();

    event EventHandler<SelectionGestureEventArgs>? SelectionGestureDetected;

    /// <summary>任意鼠标按下（用于划词图标“点击别处即隐藏”）。</summary>
    event EventHandler<PixelPoint>? MouseDown;
}
