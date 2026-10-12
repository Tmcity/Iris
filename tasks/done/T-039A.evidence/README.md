# T-039A 实证重跑说明

本目录保留原型源码、命令数组、原始输出和指纹。正式生产 Velopack 接入没有实施；仅执行期补签的 IPC 缺陷已作最小生产修正。实际安装/卸载/Update.exe 应用不在 A 执行。

所有脚本只对 `F:/Iris/artifacts/t039a` 和明确给出的自有临时输出操作。`run.py --label NAME -- COMMAND ...` 不使用 shell，保存 `NAME.log` 和 `NAME.command.json`，拒绝覆盖旧日志。后续重跑请使用新 label 和新的输出目录；保留原产物，不对现有 evidence 运行清理。它继承调用方文化；`run-before-culture-fix.py.txt` 是第一次误覆盖英文环境的历史版本，不能作为最终运行器。

复现实证的顺序：

1. `prepare.py` 从固定基线 git archive 创建隔离 Iris 源码，生成独立 PackId/NuGet.Config；只复制白名单官方资料，不读取密钥。已有工作目录不要再次初始化，应新建隔离目录并替换后续命令中的路径。
2. 按 `tool-install.command.json` 安装固定 vpk 到任务本地 tool-path，按 `fetch-source.command.json` 获取官方 1.2.161 源码。`tool-list.log`、`vpk-pack-*.log` 与 SDK 日志分别证明工具和库版本。
3. 重放 `iris-publish-baseline.command.json` 和 `uia-publish.command.json`。`inspect_publish.py` 记录错误 RID 嵌套路径与独立 publish 组合结果。组合不启动 Iris，不冒称已修复正式 csproj。
4. 将 `Payload` 复制到自有构建目录，按 `payload-publish-v1/v2.command.json` 框架依赖发布。`pack_local.py` / `vpk-pack-*.command.json` 生成两个版本、feed、full/delta、Setup/Portable，工具显式 `--skip-updates`，不访问更新服务。v1 输出另保存在 releases-v1，避免 v2 同名 Setup/Portable 覆盖历史副本。
5. 将 `Probe` 复制到自有构建目录，按 `probe-build-3.command.json` 构建，重放 `sdk-probes-2.command.json` 时为最后一个参数给新的自有运行根。实际 SDK/本地包保留，外部来源、延后 I/O、进程启动/Exit 边界受控。不要把 IProcessImpl 替身改成真正启动 Update.exe。
6. `run_probes.py boot` 展示各 fast/普通 hook 和单个无业务子进程的真实 Environment.Exit；`run_probes.py auto` 记录默认自动应用与关闭后仍清理本地包。重跑时同样给新 label/root。没有启动安装器、卸载器或真实 Iris。
7. `collect_evidence.py` 对源码、原型包中 UiaHost/资源的路径及实际 SHA、运行时/min-OS 元数据进行检查。后续使用新的报告目录，不能覆盖本轮报告。

网络仅用于取得官方资料、固定依赖和官方诊断工具。自动化更新验证仅使用 SimpleFileSource、受控流或合成 GithubSource 下载器；合成私有 404 是负例，正式目标后来已改为公开 Tmcity/Iris。仓库公开状态由架构师独立验证，见 `T-039A.review/github-public-access.json`。

IPC 证据：`ipc-before`、`ipc-early-before.log` 固定原源码和真实 ERROR_NO_DATA；`ipc-hung-*`/`ipc-async-*` 是自有测试进程的栈/状态诊断。完整 dump 仅在 artifacts，未提交。`ipc-hung-termination.json` 和 `final-full-2.command.json` 记录单独终止与真实返回。`ipc-early-after-2.log`、`ipc-regression-2.log` 验证修正、原对象重试和原协议行为。

失败原件均保留：不支持的 vpk --version；英文运行器环境下的既有精确中文断言失败；默认环境 IPC 失败/挂起；早断开门闩失败；BOOL 测试断言编译错误；不适用类名 glob 导致零测试。它们不改写为通过，最终结果以归档报告中的最后一轮串行日志为准。

`recommendation.md` 与 `ProposedContracts.cs.txt` 是待架构师批准的后续建议。父 T-039 的真实安装、更新应用、卸载选择和 VM 验收仍必须完成，不能由原型或 Sandbox 预检替代。
