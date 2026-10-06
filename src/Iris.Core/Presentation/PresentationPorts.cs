using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Translation;

namespace Iris.Core.Presentation;

// 以下接口由 Iris.App 实现。全部实现必须可从任意线程调用（内部自行 Dispatcher 切换）。

public sealed record TranslateWindowRequest
{
    /// <summary>null = 不改动当前原文（仅显示/定位窗口或显示状态）。</summary>
    public string? Text { get; init; }

    public TextOrigin Origin { get; init; } = TextOrigin.Input;

    /// <summary>定位参考点（物理像素）；null = 当前光标。</summary>
    public PixelPoint? Anchor { get; init; }

    /// <summary>仅本次显式提供非空白 Text 且无 Error/加载状态时发起翻译；同一操作至多提交一次。
    /// Text=null 保留原文但不自动翻译，空字符串明确清空。完整规则见 TRANSLATION_WINDOW.md。</summary>
    public bool AutoTranslate { get; init; } = true;

    /// <summary>激活窗口并聚焦原文输入框（输入翻译）。</summary>
    public bool FocusInput { get; init; }

    /// <summary>非空时在结果区显示加载状态文案（如“正在识别文字…”）。</summary>
    public string? StatusMessage { get; init; }

    /// <summary>非空时在结果区显示错误（用于 OCR 失败等翻译前阶段）。</summary>
    public ErrorInfo? Error { get; init; }
}

/// <summary>所属 Presenter 创建的操作租约。Dispose 幂等，只归还调用方持有；不取消已由窗口接管的工作。
/// CancellationToken 在租约有效期内可读取，调用方令牌对已接管工作仍生效；完整所有权见 TRANSLATION_WINDOW.md。</summary>
public interface ITranslateWindowOperation : IDisposable
{
    Guid Id { get; }

    CancellationToken CancellationToken { get; }
}

/// <summary>按需翻译窗口；线程安全，构造无窗口或 I/O。所有发布均需本 Presenter 的有效操作租约。</summary>
public interface ITranslateWindowPresenter
{
    bool IsVisible { get; }

    bool IsActive { get; }

    /// <summary>接纳新意图并使旧操作失效；已取消令牌抛 OperationCanceledException 且不影响当前意图。
    /// 退出中或尚未回收的操作状态达到 8 个时返回 null，不启动前置工作。取消回调不在状态锁内执行。</summary>
    ITranslateWindowOperation? TryBeginOperation(CancellationToken cancellationToken = default);

    /// <summary>在 UI 实际执行时校验身份；本 Presenter 的过期/取消/已释放租约返回 false，不改变或显示窗口。
    /// null 参数或外来租约抛 ArgumentException；同操作并发发布抛 InvalidOperationException。
    /// true 表示本次发布已接受，不等待翻译完成；接受翻译后窗口持有独立工作所有权。</summary>
    Task<bool> TryShowAsync(ITranslateWindowOperation operation, TranslateWindowRequest request);

    /// <summary>以同一操作身份发布安全默认通知，不创建翻译窗口。UI 实际接受时校验，过期/取消/已释放/退出或托盘不可用返回 false。
    /// 正文仅来自 ErrorMessages.GetDefault(code)；null/外来操作抛 ArgumentException，非法枚举抛 ArgumentOutOfRangeException。
    /// 与 TryShow 共用进行中发布所有权，同操作并发发布抛 InvalidOperationException；完整规则见 SELECTION_ENTRYPOINTS.md。</summary>
    Task<bool> TryNotifyAsync(ITranslateWindowOperation operation, NotificationLevel level, ErrorCode code);

    /// <summary>隐藏并撤销发布/自动复制资格；不等待底层 I/O，应用退出通过 App 内部完整屏障等待。</summary>
    Task HideAsync();
}

public enum ScreenshotPurpose
{
    Translate,
    Ocr,
    SilentOcr,
}

public sealed record ScreenshotResult(ImageData Image, PixelRect Region);

/// <summary>冻结屏幕并返回物理选区的PNG。用户取消、新显示意图、确认前交互失焦或拓扑失效返回null；
/// 调用方取消须完成实际工作与清理后抛OperationCanceledException，清理失败仍报告安全错误。
/// 可选复制失败仅提示警告并保留已确认截图；完整规则见SCREENSHOT_INTERACTION.md。</summary>
public interface IScreenshotService
{
    Task<ScreenshotResult?> CaptureRegionAsync(ScreenshotPurpose purpose, CancellationToken cancellationToken);
}

public sealed record OcrResultViewRequest
{
    /// <summary>转交只读图像；调用方不得再修改Bytes，窗口持有至清理完成。</summary>
    public required ImageData Image { get; init; }
    public string? Text { get; init; }
    public string? ProviderName { get; init; }
    public bool IsLoading { get; init; }
    public ErrorInfo? Error { get; init; }
    public PixelPoint? Anchor { get; init; }
    /// <summary>实际结果的逻辑服务ID；显示名可用窗口已有列表映射，缺失时显示ID。</summary>
    public string? ProviderId { get; init; }
    public bool UsedFallback { get; init; }
    /// <summary>仅成功非空白终态在UI接受点认领一次自动复制，任务由窗口拥有。</summary>
    public bool AutoCopy { get; init; }
}

/// <summary>OCR展示操作的调用方租约；释放幂等，不撤销已由窗口接管的复制/识别工作。
/// 调用方必须在自己的异步工作退出后释放；Token只在租约有效期内读取。详见OCR_RESULT_WINDOW.md。</summary>
public interface IOcrResultOperation : IDisposable
{
    Guid Id { get; }
    CancellationToken CancellationToken { get; }
}

/// <summary>有身份的OCR结果窗口。构造无窗口；关闭即释放，同一时刻只显示一个实例。</summary>
public interface IOcrResultPresenter
{
    /// <summary>接纳新意图并撤销旧操作；已取消token抛OperationCanceledException且不影响当前意图。
    /// 退出/关闭未完成或未回收操作达到8个时返回null，不启动工作。</summary>
    IOcrResultOperation? TryBeginOperation(CancellationToken cancellationToken = default);

    /// <summary>UI实际执行时复核身份；旧/取消/已释放操作返回false，不改变窗口。
    /// null或外来租约抛ArgumentException，同操作并发发布抛InvalidOperationException。
    /// 首次绑定只读Image，同操作后续Image须为同一引用；至多接受一次终态。
    /// true表示UI已接受，不等待窗口接管的自动复制。调用方不得修改Image.Bytes。</summary>
    Task<bool> TryShowAsync(IOcrResultOperation operation, OcrResultViewRequest request);
}

public enum NotificationLevel
{
    Info,
    Success,
    Warning,
    Error,
}

public interface INotificationService
{
    void Notify(NotificationLevel level, string title, string message);
}

/// <summary>设置窗口（App 实现），可从任意线程调用，内部切换 UI 线程；构造不创建窗口。
/// 重复显示复用当前窗口和草稿，关闭后释放其会话，再次显示创建新会话；ShowAsync 不等待窗口关闭。
/// pageKey：general / translation / ocr / hotkeys / selection / screenshot / floatingball / network / history / about。
/// null、未知或尚未开放的 pageKey 转到 general；不记录原始 pageKey。完整编辑/保存规则见 SETTINGS_EDITING.md。</summary>
public interface ISettingsWindowPresenter
{
    Task ShowAsync(string? pageKey = null);
}

public interface IHistoryWindowPresenter
{
    Task ShowAsync();
}

public interface IFloatingBallController
{
    bool IsVisible { get; }

    Task SetVisibleAsync(bool visible);
}

public interface IAppLifetime
{
    /// <summary>优雅退出：注销热键、卸载钩子、刷新日志后关闭。</summary>
    void RequestShutdown();
}
