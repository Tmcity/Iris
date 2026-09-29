using Iris.Core.Settings;

namespace Iris.Core.Translation;

/// <summary>
/// 翻译编排器（Core）。精确规则见 TRANSLATION_ORCHESTRATOR.md。
/// 首次枚举检查取消、捕获请求与 ProviderIds，并在首次 await 前捕获缓存 Generation；随后借用 Registry 快照。
/// 预处理、语言、模板及执行开关使用快照，排队前固定 PreparedPrompt 与缓存键；空输入/超长输入在首次枚举抛出。
/// ProviderIds=null 选已启用且已配置项，无可用项或显式空集合抛 NotConfigured；显式非空列表精确去重并保持请求顺序。
/// 显式不存在/禁用/未配置项仍产出失败卡片。每项 Started→Delta*→Completed/Failed；不同项互不因单项失败取消。
/// 真实调用并发度不超过快照配置，事件用有界背压传递；缓存命中不调用 Provider，BypassCache 只跳过读取。
/// 全部子任务退出并读尽事件、最后取消检查通过后进入正常收尾；此前取消抛 OCE，提前结束消费取消并等待子任务，均不写本次缓存/历史。
/// 正常收尾阶段将新鲜完整成功值以原 Generation 尝试缓存，按 RecordHistory/快照 History.Enabled 条件保存成功结果（含截断标记）。
/// 收尾后的迟到取消不撤回既定完成结果；历史保存有独立取消预算，失败只记安全日志，最后产出 TranslationFinished。
/// 请求级错误/正常收尾前取消/提前结束没有 Finished；租约在全部 Provider 清理后、Finished 前释放。
/// </summary>
public interface ITranslationOrchestrator
{
    IAsyncEnumerable<TranslationEvent> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// 文本预处理（规则详见 ARCH §7.5）：换行统一为 \n；移除 U+200B / U+FEFF / U+2060（保留 ZWJ 与 ZWNJ）；U+00A0 → 空格；
/// 连续多个空行压缩为 1 个；Origin=Selection 且 SelectionLineBreaks=MergeSmart 时执行软换行合并；最后去除首尾空白。
/// </summary>
public interface ITextPreprocessor
{
    string Prepare(string raw, TextOrigin origin);

    /// <summary>使用指定配置执行同一预处理规则，不读取当前设置、不修改传入配置。</summary>
    string Prepare(string raw, TextOrigin origin, TranslationSettings settings);
}
