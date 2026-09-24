using Iris.Core.Actions;
using Iris.Core.Imaging;
using Iris.Core.Languages;
using Iris.Core.Ocr;
using Iris.Core.Translation;

namespace Iris.Core.Settings;

/// <summary>
/// 应用设置根对象（不可变 record；修改只能通过 ISettingsService.UpdateAsync 与 with 表达式）。
/// 反序列化时缺失的字段取此处默认值，因此新增字段只要有合理默认值就无需迁移。
/// </summary>
public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public GeneralSettings General { get; init; } = new();
    public IReadOnlyDictionary<IrisAction, HotkeyBinding> Hotkeys { get; init; } = HotkeyDefaults.Create();
    public TranslationSettings Translation { get; init; } = new();
    public OcrSettings Ocr { get; init; } = new();
    public SelectionSettings Selection { get; init; } = new();
    public ScreenshotSettings Screenshot { get; init; } = new();
    public FloatingBallSettings FloatingBall { get; init; } = new();
    public TranslateWindowSettings TranslateWindow { get; init; } = new();
    public HistorySettings History { get; init; } = new();
    public NetworkSettings Network { get; init; } = new();
}

public static class HotkeyDefaults
{
    public static IReadOnlyDictionary<IrisAction, HotkeyBinding> Create() => new Dictionary<IrisAction, HotkeyBinding>
    {
        [IrisAction.SelectionTranslate] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "D"),
        [IrisAction.InputTranslate] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "F"),
        [IrisAction.ScreenshotTranslate] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "S"),
        [IrisAction.ScreenshotOcr] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "O"),
        [IrisAction.SilentScreenshotOcr] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, "O", Enabled: false),
        [IrisAction.ClipboardTranslate] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "V", Enabled: false),
        [IrisAction.ToggleFloatingBall] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "B", Enabled: false),
    };
}

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public sealed record GeneralSettings
{
    /// <summary>UI 语言：zh-CN / en-US。</summary>
    public string UiLanguage { get; init; } = "zh-CN";

    public AppTheme Theme { get; init; } = AppTheme.System;
    public bool LaunchAtStartup { get; init; }
    public bool ShowStartupNotification { get; init; } = true;
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>Serilog 最低级别：Verbose/Debug/Information/Warning/Error。</summary>
    public string LogLevel { get; init; } = "Information";
}

public enum AutoCopyMode
{
    None,
    FirstResult,
}

public enum LineBreakHandling
{
    Keep,
    MergeSmart,
}

public sealed record TranslationSettings
{
    public string DefaultSourceLanguage { get; init; } = LanguageCodes.Auto;
    public string PrimaryTargetLanguage { get; init; } = LanguageCodes.ChineseSimplified;
    public string SecondaryTargetLanguage { get; init; } = LanguageCodes.English;

    /// <summary>源语言与主目标语言同族时，自动改译为次目标语言。</summary>
    public bool AutoSwapTarget { get; init; } = true;

    public IReadOnlyList<TranslationProviderProfile> Providers { get; init; } = [];
    public IReadOnlyList<PromptTemplate> CustomPrompts { get; init; } = [];
    public string DefaultPromptTemplateId { get; init; } = BuiltInPrompts.DefaultTemplateId;
    public int MaxConcurrentProviders { get; init; } = 4;
    public int MaxInputChars { get; init; } = 5000;
    public bool CacheEnabled { get; init; } = true;
    public int CacheCapacity { get; init; } = 500;
    public AutoCopyMode AutoCopy { get; init; } = AutoCopyMode.None;
    public LineBreakHandling SelectionLineBreaks { get; init; } = LineBreakHandling.Keep;
}

public sealed record OcrSettings
{
    public string ActiveProviderId { get; init; } = BuiltInProviderIds.BaiduOcr;
    public string? FallbackProviderId { get; init; } = BuiltInProviderIds.WindowsOcr;

    public IReadOnlyList<OcrProviderProfile> Providers { get; init; } =
    [
        new() { Id = BuiltInProviderIds.BaiduOcr, Kind = OcrProviderKind.BaiduCloud, DisplayName = "百度智能云 OCR", BaiduOptions = new() },
        new() { Id = BuiltInProviderIds.WindowsOcr, Kind = OcrProviderKind.WindowsLocal, DisplayName = "Windows 本地 OCR", WindowsOptions = new() },
    ];

    public OcrTextLayout TextLayout { get; init; } = OcrTextLayout.MergeParagraphs;
    public string LanguageHint { get; init; } = LanguageCodes.Auto;

    /// <summary>截图 OCR（非静默）完成后自动复制文本。</summary>
    public bool AutoCopyOnOcr { get; init; }

    public bool SilentOcrShowNotification { get; init; } = true;
}

public enum MouseSelectionMode
{
    /// <summary>仅热键划词，不安装鼠标钩子。</summary>
    Off,

    /// <summary>划选后在光标旁显示小图标，点击后翻译。</summary>
    ShowIcon,

    /// <summary>划选后直接翻译。</summary>
    AutoTranslate,
}

public enum SelectionReadMethod
{
    UIAutomation,
    Clipboard,
}

public sealed record SelectionSettings
{
    public MouseSelectionMode MouseMode { get; init; } = MouseSelectionMode.Off;
    public IReadOnlyList<SelectionReadMethod> ReadOrder { get; init; } = [SelectionReadMethod.UIAutomation, SelectionReadMethod.Clipboard];
    public bool RestoreClipboard { get; init; } = true;
    public int ClipboardTimeoutMs { get; init; } = 400;

    /// <summary>仅当按下鼠标时光标为 I 型（文本光标）才视为划词手势，避免拖拽文件/窗口误触。</summary>
    public bool RequireIBeamCursor { get; init; } = true;

    public int MinDragDistancePx { get; init; } = 6;
    public int IconAutoHideMs { get; init; } = 3000;

    /// <summary>完全不响应鼠标划词的进程名（不区分大小写，含 .exe）。</summary>
    public IReadOnlyList<string> ExcludedProcesses { get; init; } = [];

    /// <summary>禁止模拟 Ctrl+C 读取选区的进程（终端中 Ctrl+C = 中断信号），这些进程只用 UIAutomation。</summary>
    public IReadOnlyList<string> ClipboardUnsafeProcesses { get; init; } =
    [
        "WindowsTerminal.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "conhost.exe", "OpenConsole.exe",
        "mintty.exe", "wezterm-gui.exe", "alacritty.exe", "putty.exe", "kitty.exe", "Tabby.exe",
    ];

    /// <summary>热键划词未读到文本时打开空白输入翻译窗口（false 则仅提示）。</summary>
    public bool OpenInputWhenEmpty { get; init; } = true;
}

public sealed record ScreenshotSettings
{
    /// <summary>松开鼠标即确认选区（false = 需按 Enter / 双击确认，可调整选区）。</summary>
    public bool ConfirmOnMouseUp { get; init; } = true;

    public bool ShowMagnifier { get; init; } = true;

    /// <summary>截图前隐藏 Iris 自身窗口（翻译窗、悬浮球）。</summary>
    public bool HideIrisWindows { get; init; } = true;

    public bool CopyImageToClipboard { get; init; }
    public double MaskOpacity { get; init; } = 0.45;
}

public sealed record FloatingBallSettings
{
    public bool Enabled { get; init; }
    public IrisAction ClickAction { get; init; } = IrisAction.ScreenshotTranslate;
    public double Size { get; init; } = 44;
    public double Opacity { get; init; } = 0.9;
    public bool EdgeSnap { get; init; } = true;
    public bool AutoHideAtEdge { get; init; } = true;
    public bool AcceptDrop { get; init; } = true;

    /// <summary>上次位置（物理像素，窗口左上角）；null = 主屏右侧中部。</summary>
    public PixelPoint? Position { get; init; }

    /// <summary>上次所在显示器；该显示器不存在时回退到主屏。</summary>
    public string? MonitorDeviceName { get; init; }
}

public enum WindowPlacementMode
{
    FollowCursor,
    RememberLast,
    ScreenCenter,
}

public sealed record TranslateWindowSettings
{
    public WindowPlacementMode Placement { get; init; } = WindowPlacementMode.FollowCursor;
    public bool Topmost { get; init; } = true;

    /// <summary>失去焦点时自动隐藏（窗口处于“钉住”状态时不隐藏）。</summary>
    public bool HideOnDeactivate { get; init; } = true;

    /// <summary>Enter 翻译、Shift+Enter 换行；false 则 Ctrl+Enter 翻译。</summary>
    public bool EnterToTranslate { get; init; } = true;

    public double Width { get; init; } = 460;
    public double FontSize { get; init; } = 14;

    /// <summary>RememberLast 模式使用（物理像素）。</summary>
    public PixelRect? LastBounds { get; init; }
}

public sealed record HistorySettings
{
    public bool Enabled { get; init; } = true;
    public int MaxEntries { get; init; } = 5000;
}

public enum ProxyMode
{
    System,
    None,
    Custom,
}

public sealed record NetworkSettings
{
    public ProxyMode ProxyMode { get; init; } = ProxyMode.System;

    /// <summary>如 http://127.0.0.1:7890 或 socks5://127.0.0.1:1080。</summary>
    public string? CustomProxyUrl { get; init; }

    public bool BypassProxyOnLocal { get; init; } = true;
}
