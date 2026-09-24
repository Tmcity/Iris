using Iris.Core.Imaging;

namespace Iris.Core.Actions;

/// <summary>用户可触发的全部动作。热键、托盘、悬浮球、划词图标、命令行最终都路由到 IActionDispatcher。</summary>
public enum IrisAction
{
    InputTranslate = 1,
    SelectionTranslate = 2,
    ScreenshotTranslate = 3,
    ScreenshotOcr = 4,
    SilentScreenshotOcr = 5,
    ClipboardTranslate = 6,

    /// <summary>翻译 ActionContext.Payload 中的文本（拖放到悬浮球、命令行 --translate）。</summary>
    TranslateText = 7,
    ToggleFloatingBall = 8,
    ToggleMouseSelection = 9,
    OpenSettings = 10,
    OpenHistory = 11,
    Exit = 12,
}

public enum ActionTrigger
{
    Hotkey,
    TrayMenu,
    FloatingBall,
    SelectionIcon,
    MouseSelection,
    DragDrop,
    CommandLine,
    InApp,
}

/// <param name="Anchor">窗口定位参考点（物理像素）；null 时由 Presenter 取当前光标位置。</param>
public sealed record ActionContext(ActionTrigger Trigger, PixelPoint? Anchor = null, string? Payload = null);

/// <summary>每个 IrisAction 恰好一个 Handler（DI 注册多个 IActionHandler，Dispatcher 按 Action 建索引）。</summary>
public interface IActionHandler
{
    IrisAction Action { get; }

    Task ExecuteAsync(ActionContext context, CancellationToken cancellationToken);
}

/// <summary>
/// 动作分发（App 层实现）。约束：
/// - 可从任意线程调用；Handler 在 UI 线程上启动（Handler 内部的 await 自行决定线程）。
/// - 防重入：同一 Action 正在执行时再次触发 → 忽略（截图类动作之间互斥）。
/// - 捕获 Handler 的全部异常：IrisException → INotificationService 提示；其他 → 记录日志并提示“未知错误”；OCE 静默。
/// </summary>
public interface IActionDispatcher
{
    Task DispatchAsync(IrisAction action, ActionContext context, CancellationToken cancellationToken = default);
}
