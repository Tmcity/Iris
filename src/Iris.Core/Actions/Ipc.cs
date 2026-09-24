namespace Iris.Core.Actions;

/// <summary>第二实例 → 主实例的命令（命名管道，UTF-8 JSON，一行一条，使用 IrisJson.Options 序列化）。</summary>
public sealed record IpcCommand(IrisAction Action, string? Payload = null);

public static class IpcConstants
{
    /// <summary>完整名称 = 前缀 + 当前用户 SID。</summary>
    public const string PipeNamePrefix = "Iris.Ipc.";

    /// <summary>完整名称 = 前缀 + 当前用户 SID。</summary>
    public const string MutexNamePrefix = @"Local\Iris.SingleInstance.";
}
