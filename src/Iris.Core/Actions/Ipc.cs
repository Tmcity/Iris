namespace Iris.Core.Actions;

/// <summary>第二实例 → 主实例的命令。UTF-8 JSON 一行一条；使用 IrisJson.Options 的私有紧凑副本，不修改全局选项。
/// 仅本机同用户/同会话，参数、帧预算、接纳确认及完整退出规则见 IPC_COMMAND_LINE.md。</summary>
public sealed record IpcCommand(IrisAction Action, string? Payload = null)
{
    public override string ToString() => $"IpcCommand {{ Action={Action} }}";
}

public static class IpcConstants
{
    /// <summary>完整名称 = 前缀 + 当前用户 SID。</summary>
    public const string PipeNamePrefix = "Iris.Ipc.";

    /// <summary>完整名称 = 前缀 + 当前用户 SID。</summary>
    public const string MutexNamePrefix = @"Local\Iris.SingleInstance.";
}
