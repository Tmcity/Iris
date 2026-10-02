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

/// <summary>冻结屏幕 + 选区覆盖层。用户取消（Esc / 右键）返回 null。</summary>
public interface IScreenshotService
{
    Task<ScreenshotResult?> CaptureRegionAsync(ScreenshotPurpose purpose, CancellationToken cancellationToken);
}

public sealed record OcrResultViewRequest
{
    public required ImageData Image { get; init; }
    public string? Text { get; init; }
    public string? ProviderName { get; init; }
    public bool IsLoading { get; init; }
    public ErrorInfo? Error { get; init; }
}

/// <summary>OCR 结果窗口（左图右文，可编辑、复制、一键翻译）。重复调用更新同一窗口。</summary>
public interface IOcrResultPresenter
{
    Task ShowAsync(OcrResultViewRequest request);
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
