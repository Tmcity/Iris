using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Iris.Core.Settings;

/// <summary>Core 配置编辑单例，构造不做 I/O；实现与回执规则见 PROVIDER_EDITING.md。
/// 所有 Provider 引用写入经此协调器串行化；方法不遗忘后台任务，调用方等待完整退出后释放其依赖。</summary>
public interface ISettingsEditCoordinator
{
    /// <summary>只同步捕获OCR草稿并创建测试会话，不读取配置/密钥或创建Provider。详见OCR_SETTINGS_EDITING.md。</summary>
    IOcrDraftTestSession CreateOcrDraftTest(OcrProviderDraft draft);

    /// <summary>同步捕获计划，stage 新凭据，一次 UpdateAsync 联合提交，刷新全部观察者后清理旧值。
    /// 提交前取消在清理本次暂存值后抛 OperationCanceledException；提交后忽略迟到取消并返回明确已提交结果。</summary>
    Task<SettingsEditResult> CommitAsync(SettingsEditPlan plan, CancellationToken cancellationToken = default);

    /// <summary>仅重试回执的刷新/清理，不重新 stage 或发布草稿；对已完成回执幂等。
    /// 取消不改变原提交事实；未知/其它协调器的回执作为参数错误拒绝。</summary>
    Task<SettingsApplyResult> RetryApplyAsync(ISettingsApplyReceipt receipt, CancellationToken cancellationToken = default);

    /// <summary>以草稿和显式解析的凭据创建瞬态租约测试翻译，不写设置或密钥；实际工作退出后释放租约。
    /// 业务失败写入结果，调用方取消抛 OperationCanceledException；测试语言/Prompt 正文取既有已保存配置语义。</summary>
    Task<Iris.Core.Common.ProviderTestResult> TestDraftAsync(TranslationProviderDraft draft, CancellationToken cancellationToken = default);

    /// <summary>以瞬态租约获取草稿端点的模型；不要求先选模型，不写设置或密钥，工作退出后释放租约。
    /// 无模型列表能力或配置不足抛安全 IrisException(NotConfigured)；调用方取消抛 OperationCanceledException。</summary>
    Task<IReadOnlyList<string>> ListDraftModelsAsync(TranslationProviderDraft draft, CancellationToken cancellationToken = default);
}
