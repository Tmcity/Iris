using System.Text.Json.Nodes;

namespace Iris.Core.Settings;

/// <summary>ISettingsService 发出的变更事件，其 OldSettings 与 NewSettings 均为深度不可变快照。</summary>
public sealed class SettingsChangedEventArgs(AppSettings oldSettings, AppSettings newSettings) : EventArgs
{
    public AppSettings OldSettings { get; } = oldSettings;
    public AppSettings NewSettings { get; } = newSettings;
}

/// <summary>
/// 设置服务（Infrastructure 实现，JSON 文件）。约束：
/// - Current：永不为 null；LoadAsync 之前返回默认快照。任何时刻都满足规范化不变量（ARCH §12.2）。
///   同一已发布版本重复读取返回同一实例；全部可达集合已复制隔离且不可修改，不在 getter 中克隆。
/// - LoadAsync：进程内只允许调用一次（重复调用抛 InvalidOperationException）。
///   文件不存在 → 写入默认值；SchemaVersion 较旧 → 依次执行 ISettingsMigration 迁移后回写；
///   单个字段 / 条目非法 → 按 ARCH §12.2 挽救与规范化后回写（只记日志，不抛异常）；
///   JSON 语法损坏、存在重复属性名（含仅大小写不同）或根节点不是对象 → 备份为 settings.corrupt-{yyyyMMddHHmmss}.json、使用默认值，
///   并抛出一次 IrisException(SettingsCorrupted)（调用方提示用户后继续运行，Current 已可用）。
///   只允许抛出：IrisException(SettingsCorrupted)（已加载）、IrisException(StorageFailure)（未加载，可重试）、
///   OperationCanceledException，以及编程错误导致的 InvalidOperationException。
/// - UpdateAsync：必须在 LoadAsync 之后调用（否则抛 InvalidOperationException）；串行化执行（SemaphoreSlim）；
///   mutate 结果与当前值引用相等、或序列化结果完全相同 → 不写盘、不通知；结果先规范化再写入；
///   规范化后，在序列化及异步写盘之前捕获独立快照；不得保留调用方仍可改写的集合别名。
///   原子写入（写 .tmp → File.Move 覆盖），写入前保留 settings.json.bak；写盘失败抛 IrisException(StorageFailure)
///   且 Current 保持不变；成功后在内部锁内同步触发 Changed（保证事件顺序与写入顺序一致）。
/// - Changed：可能在任意线程触发，订阅方自行切换线程。处理器必须快速返回，且不得同步阻塞等待
///   UpdateAsync（可以不 await 地发起新的 UpdateAsync）；单个处理器抛出的异常会被记录并忽略，不影响其他处理器。
/// </summary>
public interface ISettingsService
{
    /// <summary>本次加载为未来schema时只读；Update在调用mutate之前拒绝。</summary>
    bool IsReadOnly { get; }

    AppSettings Current { get; }

    Task LoadAsync(CancellationToken cancellationToken = default);

    Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default);

    event EventHandler<SettingsChangedEventArgs>? Changed;
}

/// <summary>对原始 JSON 做版本迁移，每个版本一个实现（FromVersion → FromVersion + 1），按顺序链式执行。</summary>
public interface ISettingsMigration
{
    int FromVersion { get; }

    JsonObject Migrate(JsonObject root);
}
