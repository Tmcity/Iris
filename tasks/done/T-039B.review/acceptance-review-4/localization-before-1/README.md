# R4 本地化收尾审计实际失败

2026-10-25 · 架构师在当前工作树执行现有完整审计；这不是仅凭文件名判断的意见，也不是此前已通过的编译/单元测试失败。

原始 `localization-inventory-before-1.log`：扫描当前 C#/XAML 的中文条目 1203；实际编译资源与 source 及复合格式匹配 1230，exit0。随后 `localization-catalog-before-1.log` 的 audit.py **exit1**，结果在 audit-result.json。

1. App 中英资源各 462 键，但已审校目录仅 457。缺少 R4 新增的 ShutdownIncomplete、UpdateRecoveryCancel、UpdateRecoveryCleanupPending、UpdateRecoveryRequired、UpdateRecoveryRetry；Core 148 / Maintenance 5 的目录和值匹配。五对现有文案经架构师逐项审阅，确切允许新增的值在 approved-catalog-additions.json。不修改现有翻译、默认值或格式。
2. 当前有 6 个未批准源码站点：GeneralSettingsSaveCoordinator.cs 的两处“仅供测试”的 XML 注释，以及 UpdateHandoff.cs 的 4 处硬编码中文异常消息（撤销两处、对端退出和超时各一处）。它们是 R2 以来残留，本轮完整审计才实际暴露。不能引用 B 早期审计宣称当前代码已通过。

继续同一 R4：同步已审校五键；保留测试专用兼容成员的说明，可用英文 XML 文档；交接异常应使用对应 ErrorCode 的既有本地化默认消息，或明确的资源化消息，保留现有类型/代码/重试/异常所有权语义。不以无差别豁免或扩大异常预算让审计变绿。`exceptions.json` 原指纹与全部原始失败保留。

修后运行当前全量 source/compiled/catalog 审计。不要执行 migration；其它已通过行为不重做无关实现。若产品异常消息发生变化，按原工单重跑受影响验证、最终建测与新来源绑定/唯一新包；真实来宾验收仍等待已请求的环境，不能绕过。

input/ 为输入原字节，input-binding.json 记录实际工具 DLL 与源码/资源/目录 SHA。最初一次快速对照误用了 Core 的 Resources 路径；随后已改用实际 Localization 路径，真正结论来自当前完整审计及保留的结果，不能把路径读取失败当成产品失败。
