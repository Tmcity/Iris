using Iris.Core.Errors;
using Iris.Core.Settings;

namespace Iris.Core.Platform;

public enum HotkeyCaptureOutcome
{
    Captured = 1,
    Cleared = 2,
    Canceled = 3,
    Failed = 4,
}

/// <summary>录入预览快照；Key 仅为白名单规范键名，null 表示尚无主键，不含键入文本。</summary>
public sealed record HotkeyCaptureProgress(
    long Revision,
    bool IsReady,
    HotkeyModifiers Modifiers,
    string? Key = null,
    ErrorInfo? Error = null)
{
    public override string ToString() => "HotkeyCaptureProgress";
}

/// <summary>逻辑录入结果；不代表原生资源已清理，应用草稿前必须成功等待会话 DisposeAsync。</summary>
public sealed record HotkeyCaptureResult(
    HotkeyCaptureOutcome Outcome,
    HotkeyBinding? Binding = null,
    ErrorInfo? Error = null)
{
    public override string ToString() => $"HotkeyCaptureResult {{ Outcome = {Outcome} }}";
}

/// <summary>单次录入所有权，规则见 HOTKEY_RECORDING_UI.md；清理失败时服务继续持有资源，可重试 DisposeAsync。</summary>
public interface IHotkeyCaptureSession : IAsyncDisposable
{
    Guid Id { get; }
    HotkeyCaptureProgress Current { get; }
    Task<HotkeyCaptureResult> Completion { get; }

    /// <summary>在原生回调之外有界合并通知；接收方读取 Current 并自行调度 UI，不能假定通知线程。</summary>
    event EventHandler? ProgressChanged;
}

/// <summary>Platform 实现的短时按键录入。构造无 I/O；仅 BeginAsync 创建原生资源，进程内最多一个会话。</summary>
public interface IHotkeyCaptureService : IDisposable, IAsyncDisposable
{
    /// <summary>仅接收本进程设置窗口的有效句柄；开始取消需等待回滚，失败资源仍由服务持有。</summary>
    Task<IHotkeyCaptureSession> BeginAsync(nint ownerWindowHandle, CancellationToken cancellationToken = default);

    /// <summary>立即停止新捕获并等待当前或初始化中会话真正清理；幂等、共享在途屏障，失败可重试。</summary>
    Task StopAsync();
}
