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

    /// <summary>显示后立即以 Text 发起翻译。</summary>
    public bool AutoTranslate { get; init; } = true;

    /// <summary>激活窗口并聚焦原文输入框（输入翻译）。</summary>
    public bool FocusInput { get; init; }

    /// <summary>非空时在结果区显示加载状态文案（如“正在识别文字…”）。</summary>
    public string? StatusMessage { get; init; }

    /// <summary>非空时在结果区显示错误（用于 OCR 失败等翻译前阶段）。</summary>
    public ErrorInfo? Error { get; init; }
}

public interface ITranslateWindowPresenter
{
    bool IsVisible { get; }

    bool IsActive { get; }

    Task ShowAsync(TranslateWindowRequest request);

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
