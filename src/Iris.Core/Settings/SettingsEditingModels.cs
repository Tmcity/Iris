using System;
using System.Collections.Generic;

namespace Iris.Core.Settings;

/// <summary>非 null 补丁项表示显式编辑意图；Base 用于冲突检测，Target 为目标值。</summary>
public sealed record SettingsFieldEdit<T>(T Base, T Target)
{
    public override string ToString() => "SettingsFieldEdit";
}

/// <summary>常规设置字段增量；null 项保留提交时的最新值。原四参数构造/解构保持。
/// UiLanguage 只接受规范值zh-CN/en-US，保存后下次启动生效，完整规则见LOCALIZATION.md。</summary>
public sealed record GeneralSettingsPatch(
    SettingsFieldEdit<AppTheme>? Theme = null,
    SettingsFieldEdit<bool>? LaunchAtStartup = null,
    SettingsFieldEdit<bool>? ShowStartupNotification = null,
    SettingsFieldEdit<string>? LogLevel = null)
{
    public SettingsFieldEdit<string>? UiLanguage { get; init; }
}

/// <summary>划词设置增量。null 项保留最新值；Base 只用于冲突比较，显式 Target 按 SELECTION_SETTINGS_UI.md / MOUSE_SELECTION_INTERACTION.md 验证。
/// 所有列表的 Base/Target 均须在 Commit 入口同步独立复制；原五参数构造/解构保持。</summary>
public sealed record SelectionSettingsPatch(
    SettingsFieldEdit<IReadOnlyList<SelectionReadMethod>>? ReadOrder = null,
    SettingsFieldEdit<bool>? RestoreClipboard = null,
    SettingsFieldEdit<int>? ClipboardTimeoutMs = null,
    SettingsFieldEdit<IReadOnlyList<string>>? ClipboardUnsafeProcesses = null,
    SettingsFieldEdit<bool>? OpenInputWhenEmpty = null)
{
    public SettingsFieldEdit<MouseSelectionMode>? MouseMode { get; init; }
    public SettingsFieldEdit<bool>? RequireIBeamCursor { get; init; }
    public SettingsFieldEdit<int>? MinDragDistancePx { get; init; }
    public SettingsFieldEdit<int>? IconAutoHideMs { get; init; }
    public SettingsFieldEdit<IReadOnlyList<string>>? ExcludedProcesses { get; init; }

    public override string ToString() => "SelectionSettingsPatch";
}

public enum CredentialEditKind
{
    Keep = 0,
    Replace = 1,
    Clear = 2,
}

/// <summary>Replace 必须提供完整凭据对；Keep/Clear 的 Replacement 必须为 null。不得持久化此 DTO。</summary>
public sealed record ProviderCredentialEdit(
    CredentialEditKind Kind,
    Iris.Core.Common.ProviderCredentials? Replacement = null)
{
    public override string ToString() => $"ProviderCredentialEdit {{ Kind = {Kind} }}";
}

public enum TranslationProfileChangeKind
{
    Add = 1,
    Update = 2,
    Delete = 3,
}

/// <summary>Add: BaseProfile=null；Update: 同 ID 的 base/target；Delete: TargetProfile=null 且凭据 Keep。
/// Profile 的凭据引用由协调器管理，调用方不能通过 TargetProfile 任意切换引用。</summary>
public sealed record TranslationProfileChange(
    TranslationProfileChangeKind Kind,
    TranslationProviderProfile? BaseProfile,
    TranslationProviderProfile? TargetProfile,
    ProviderCredentialEdit Credentials)
{
    public override string ToString() => $"TranslationProfileChange {{ Kind = {Kind} }}";
}

/// <summary>BaseIds 是基线中全部翻译服务按 Order 稳定排序的 ID；TargetIds 是应用增删后完整的目标顺序。</summary>
public sealed record TranslationOrderChange(
    IReadOnlyList<string> BaseIds,
    IReadOnlyList<string> TargetIds)
{
    public override string ToString() => "TranslationOrderChange";
}

/// <summary>一次联合提交计划。入口同步捕获所有嵌套集合；不允许用整份陈旧 AppSettings 覆盖 Current。</summary>
public sealed record SettingsEditPlan(
    GeneralSettingsPatch General,
    IReadOnlyList<TranslationProfileChange> TranslationChanges,
    TranslationOrderChange? TranslationOrder = null)
{
    /// <summary>快捷键增量；默认无编辑。入口复制列表，未编辑 Action 保留提交时最新配置。</summary>
    public IReadOnlyList<HotkeyBindingChange> HotkeyChanges { get; init; } = [];

    /// <summary>划词基础字段补丁；默认 null 表示无编辑，不覆盖未编辑的鼠标或其它设置。</summary>
    public SelectionSettingsPatch? Selection { get; init; }

    public OcrSettingsPatch? Ocr { get; init; }
    public IReadOnlyList<OcrProfileChange> OcrChanges { get; init; } = [];
    public ScreenshotSettingsPatch? Screenshot { get; init; }

    /// <summary>历史设置增量；null保持最新值，纯历史编辑无需Provider回执。见HISTORY_UI.md。</summary>
    public HistorySettingsPatch? History { get; init; }
    public NetworkSettingsPatch? Network { get; init; }

    public override string ToString() => "SettingsEditPlan";
}

/// <summary>BaseBinding=null 表示基线无此 Action；TargetBinding 不得为 null，清除用禁用空绑定。</summary>
public sealed record HotkeyBindingChange(
    Iris.Core.Actions.IrisAction Action,
    HotkeyBinding? BaseBinding,
    HotkeyBinding TargetBinding)
{
    public override string ToString() => $"HotkeyBindingChange {{ Action = {Action} }}";
}

/// <summary>BaseProfile=null 表示新增草稿；Keep 解析已有凭据时须核对当前 base，网络阶段不持有编辑锁。</summary>
public sealed record TranslationProviderDraft(
    TranslationProviderProfile Profile,
    TranslationProviderProfile? BaseProfile,
    ProviderCredentialEdit Credentials)
{
    public override string ToString() => "TranslationProviderDraft";
}

/// <summary>OCR字段增量。null保留提交时最新字段；完整规则见OCR_SETTINGS_EDITING.md。</summary>
public sealed record OcrSettingsPatch(
    SettingsFieldEdit<string>? ActiveProviderId = null,
    SettingsFieldEdit<string?>? FallbackProviderId = null,
    SettingsFieldEdit<Iris.Core.Ocr.OcrTextLayout>? TextLayout = null,
    SettingsFieldEdit<string>? LanguageHint = null,
    SettingsFieldEdit<bool>? AutoCopyOnOcr = null,
    SettingsFieldEdit<bool>? SilentOcrShowNotification = null)
{
    public override string ToString() => "OcrSettingsPatch";
}

public sealed record ScreenshotSettingsPatch(
    SettingsFieldEdit<bool>? ConfirmOnMouseUp = null,
    SettingsFieldEdit<bool>? ShowMagnifier = null,
    SettingsFieldEdit<bool>? HideIrisWindows = null,
    SettingsFieldEdit<bool>? CopyImageToClipboard = null,
    SettingsFieldEdit<double>? MaskOpacity = null)
{
    public override string ToString() => "ScreenshotSettingsPatch";
}

/// <summary>历史记录设置增量；MaxEntries目标100..100000，收藏额外保留，不立即修剪旧库。</summary>
public sealed record HistorySettingsPatch(
    SettingsFieldEdit<bool>? Enabled = null,
    SettingsFieldEdit<int>? MaxEntries = null)
{
    public override string ToString() => "HistorySettingsPatch";
}

public enum OcrProfileChangeKind { Add = 1, Update = 2, Delete = 3 }

/// <summary>按ID编辑OCR Profile；凭据引用由协调器管理，禁止跨领域同ID映射串线。</summary>
public sealed record OcrProfileChange(
    OcrProfileChangeKind Kind,
    OcrProviderProfile? BaseProfile,
    OcrProviderProfile? TargetProfile,
    ProviderCredentialEdit Credentials)
{
    public override string ToString() => $"OcrProfileChange {{ Kind = {Kind} }}";
}

public sealed record OcrProviderDraft(
    OcrProviderProfile Profile,
    OcrProviderProfile? BaseProfile,
    ProviderCredentialEdit Credentials)
{
    public override string ToString() => "OcrProviderDraft";
}

/// <summary>单次OCR草稿测试拥有者；创建无I/O，Run只调用Provider.TestAsync。
/// 调用方必须等待Dispose成功再释放会话；失败保有原快照，重试不重复测试。</summary>
public interface IOcrDraftTestSession : IAsyncDisposable
{
    Task<Iris.Core.Common.ProviderTestResult> RunAsync(CancellationToken cancellationToken = default);
}

public enum SettingsEditStatus
{
    Uncommitted = 0,
    Applied = 1,
    ApplyPending = 2,
}

/// <summary>已提交但观察者刷新/旧凭据清理未完成的 opaque 回执；不含原凭据、JSON、Provider 或 UI 引用。
/// 只能交回创建它的协调器；由调用方持有，不是持久化格式，不代表尚未保存。</summary>
public interface ISettingsApplyReceipt
{
    Guid CommitId { get; }
}

/// <summary>Applied/ApplyPending 均表示已提交；仅 ApplyPending 带 PendingApply。
/// IsConflict 仅适用于未提交的并发冲突；Error 必须是安全错误信息。</summary>
public sealed record SettingsEditResult(
    SettingsEditStatus Status,
    bool IsConflict = false,
    Iris.Core.Errors.ErrorInfo? Error = null,
    ISettingsApplyReceipt? PendingApply = null)
{
    public override string ToString() => $"SettingsEditResult {{ Status = {Status}, IsConflict = {IsConflict} }}";
}

/// <summary>重试只完成已有提交的刷新/清理；失败仍为已提交，调用方继续持有原回执。</summary>
public sealed record SettingsApplyResult(
    bool IsComplete,
    Iris.Core.Errors.ErrorInfo? Error = null)
{
    public override string ToString() => $"SettingsApplyResult {{ IsComplete = {IsComplete} }}";
}
