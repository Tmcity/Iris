# C 协调入口

2026-10-25。当前工单是 `F:\Iris\tasks\current_task.md` / T-039C。

- 工程师聊天 `01a16ef2-a0bb-71a1-8230-57165c05cc5b`，初始C执行轮 `01a171f7-c7fd-7bc1-bef6-a2d84d1be50c`，已实际完整读取并确认本地第一阶段边界。
- B最终独立验收已归档于7e852917…；签单commit5077d500…；C执行基线以工作树和实际提交刷新，不依赖旧聊天状态推断。
- `order-message-20261025.md` 一次性发送，`dispatch-20261025.json` 只为queue确认；真实工程师回复另以watch快照证明。
- `watch_engineer.py` 只读同一聊天增量可见信息。volatile cursor放在artifacts/t039c，避免不断修改已归档B状态；空快照/超时不能授权重开执行轮。
- 当前尚未批准首次push、上传、发布或触发发布动作。具体本地材料到位后按 CHECKPOINT_REVIEW.md 实际复核，由架构师记录核准范围；用户不再负责传话。
- 构建/测试窗口交给工程师，架构师没有运行中的任务。自有Sandbox目前均结束，下一实际环境以最新list/intent核对。

## 检查点批准后的当前状态（覆盖上方准备阶段描述）

第一阶段实际来源为dcdfdded…，checkpoint为3bb52042184b4c4aef8ad75dfab9bf0f40c1f666。正常merge保留两段历史；两版candidate-3完整资产各16/18、各97payload、713源/原始提交证据的独立1302项核对全部通过，工具16/16、actionlint/系统PS5.1语法检查也已独立执行通过。原索引、PS展开、legacy full集合及.gitattributes归档遗漏都保留原始失败，修后不假称CI已通过。

明确批准在CHECKPOINT_APPROVAL_20261025.md。仅核准该checkpoint到codex/t039c-public-release和正常fast-forward main、再单独推指向dcdfdde源的v0.2.0。v0.2.1是有条件批准，须旧版公开CI/资产、默认无预填缓存运行时引导及真实旧Iris/合成数据就绪；否则回报架构师。工程师在新轮01a172dc-5b9d-75b3-ad89-f1c99de2a0b6实际读取批准并确认顺序，开始远端只读复核。窗口已归还工程师，无需用户代传。父T-039/C未验收，T-040未签发，M6延期。

## 首轮实际 CI 失败与诊断接单（2026-10-27 最新状态）

原核准 push 已执行，main/codex 分支为 3bb5204；v0.2.0 固定解引用为 dcdfdde，实际 run40193113435 失败。Release 构建零警告错误、工具 16/16，全量 3728/3726 成功/1 失败/1 跳过；失败用例已建立隔离站，package/publish 未执行，不能改记环境跳过。原 archive digest、51 份原件及 xUnit 已独立匹配，见 ci-failure-independent-check-1.json。

新诊断范围在 CI_FAILURE_SCOPE_APPROVAL_20261025.md 与 current_task；只允许测试范围内自有 Sandbox 诊断/夹具修正和本地新版准备，产品修正须先提交具体反例与范围。新版方向 0.2.1(Previous=null)→0.2.2；先保留旧源/plan/候选并核对未占用。当前未批准任何新远端写入、tag 创建/推送、workflow 重跑/触发或 Release 写入，之前第二版的条件未满足。

原诊断补充只发送一次。2026-10-27 的实际桌面及同一聊天执行轮 01a17cd6-daa3-72c2-863a-e10b65b791a6 已显示工程师确认接续，并实际完整读取 current_task、精确批准、源码/规范和自有进程/Sandbox 状态；接单证据见 ci-failure-scope-received-20261027.json。API 的 interrupted/completedAt=null 与桌面重连状态未当成终态，未重复派单或重开。工程师持有构建/测试/来宾执行窗口，架构师只读准备 DIAGNOSTIC_REVIEW_GATES.md，待真实原始诊断后独立裁定。父 T-039/C 仍进行中，T-040 未签发，M6 延期。

架构师独立留存的修前输入见 architect-diagnostic-prestate-20261027.json：HEAD 3bb5204、13 份相关源码与 HEAD Git blob 匹配、6 个现有关键程序集/运行配置复制前后字节匹配。副本仅在 artifacts/t039c/architect-diagnostic-prestate-20261027；未编译或执行 helper、未操作来宾。该记录只用于修前输入比对，不宣称它们是 CI 实际二进制或完整可运行依赖集，工程师仍需冻结其实际完整诊断输入。

2026-10-27T11:06Z 再次匿名读取公开状态，原始六份 HTTP JSON 只保存在 artifacts/t039c/public-observations；长度与 SHA 见 public-observation-20261027T110611929092Z.json。main 仍为 3bb5204，唯一 v0.2.* tag ref 为未改变的 v0.2.0 annotated object ebe1945；原 run40193113435 仍 completed/failure、run_attempt=1、source=dcdfdde。公开 Release 列表为空，v0.2.1/v0.2.2 Release API 均404且没有相应公开 tag ref，当前本地版本方向可继续，下一远端检查点前仍须刷新并核查可能隐藏的草稿。该观察只下载公开 API 元数据，未下载发布资产/安装器或操作远端状态，不作为安装/升级验收。
