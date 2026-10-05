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
public sealed record ActionContext(ActionTrigger Trigger, PixelPoint? Anchor = null, string? Payload = null)
{
    /// <summary>SelectionTranslate 的可选原来源；鼠标自动/缓存点击必须提供，标题不参与身份。
    /// SelectionIcon 的 Payload 为缓存原文，MouseSelection 为空；完整规则见 MOUSE_SELECTION_INTERACTION.md。</summary>
    public Iris.Core.Platform.ForegroundAppInfo? ExpectedSelectionSource { get; init; }

    public override string ToString() => $"ActionContext {{ Trigger={Trigger} }}";
}

/// <summary>每个已开放的 IrisAction 恰好一个 Handler；允许只注册当前已实现的动作，禁止重复或未定义的 Action。
/// DI 注册多个 IActionHandler，Dispatcher 按 Action 建索引；业务 Handler 在 Core/Workflows 实现。</summary>
public interface IActionHandler
{
    IrisAction Action { get; }

    Task ExecuteAsync(ActionContext context, CancellationToken cancellationToken);
}

/// <summary>
/// 动作分发（App 层实现），完整规则见 ARCH §6.1。
/// 可从任意线程调用；在 UI 线程启动 Handler，等待其完整 Task；构造时不要求 WPF Application 已存在。
/// 在 UI 排队前原子防重入：同一 Action 执行中再次触发立即忽略；ScreenshotTranslate/ScreenshotOcr/SilentScreenshotOcr 互斥。
/// 不同非截图动作可重叠；执行权覆盖排队和完整异步执行，所有退出路径均释放；忽略调用不取消已有调用。
/// 进入时已取消或 Handler 启动前取消则不执行；开始后传递原令牌并等待实际退出，OCE 静默。
/// 未定义 Action/null context 提示 InvalidArgument；已定义但未注册的 Action 提示功能尚未开放。
/// 调度/Handler 异常在边界处理：IrisException 使用错误码默认文案，未知异常使用 Unknown 文案；仅记录安全元数据。
/// 不记录 Payload 或原始异常；通知失败只安全记录，不递归通知。窗口会话与旧结果替换策略不属于 Dispatcher。
/// </summary>
public interface IActionDispatcher
{
    Task DispatchAsync(IrisAction action, ActionContext context, CancellationToken cancellationToken = default);
}
