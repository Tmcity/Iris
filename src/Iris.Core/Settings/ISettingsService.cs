using System.Text.Json.Nodes;

namespace Iris.Core.Settings;

public sealed class SettingsChangedEventArgs(AppSettings oldSettings, AppSettings newSettings) : EventArgs
{
    public AppSettings OldSettings { get; } = oldSettings;
    public AppSettings NewSettings { get; } = newSettings;
}

/// <summary>
/// 设置服务（Infrastructure 实现，JSON 文件）。约束：
/// - LoadAsync：文件不存在 → 写入默认值；SchemaVersion 较旧 → ISettingsMigrator 迁移后回写；
///   JSON 损坏 → 备份为 settings.corrupt-{yyyyMMddHHmmss}.json、使用默认值，并抛出一次
///   IrisException(SettingsCorrupted)（调用方提示用户后继续运行，Current 已可用）。
/// - UpdateAsync：串行化执行（SemaphoreSlim）；mutate 结果与当前值引用相等则不写盘不通知；
///   原子写入（写 .tmp → File.Move 覆盖），写入前保留 settings.json.bak；成功后同步触发 Changed。
/// - Changed 可能在任意线程触发，订阅方自行切换线程。
/// </summary>
public interface ISettingsService
{
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
