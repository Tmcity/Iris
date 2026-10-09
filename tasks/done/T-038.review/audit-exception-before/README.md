# T-038 审计例外遗漏的独立复现

2026-10-09。只在自有临时副本运行，未改产品源码。

使用实际已构建 Roslyn/XAML 盘点器和 audit.py，复制实际资源/目录清单/例外，仅保留原 App.xaml.cs。基线盘点 3 项、audit exit 0。随后在同一副本文件新增创建 WPF TextBlock 的方法，Text 使用已有 LoggerMessage 的中文 `Iris 已退出，退出码 {ExitCode}`。新盘点为 4 项，新增项 Context 明确是 UI 赋值，但 fingerprint 仍和日志相同，audit 继续 exit 0 / unapproved 0。

`baseline-*`、`mutant-*` 为原始输出，`tree` 保留当时的检查器、资源、清单和变异源码；未复制编译器二进制。`binding.json` 保存原输入和实际盘点器 DLL 的 SHA。原因是 audit.py 仅按 Fingerprint 判断例外，未约束语法上下文、出现位置或次数。

可用已保存的实际盘点结果重放误通过：`python tree/build/localization/audit.py --root tree --inventory mutant-inventory.json --output replay.json`。修复后应重新对源码副本盘点，验证新增 UI 命中必须失败；不能只修改这份历史结果。
