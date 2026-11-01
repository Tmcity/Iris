# T-039C 手工操作与反馈清单

用户于 2026-11-01 要求：需要实际操作的项目由用户手工确认、验证后反馈。按下表逐步推进。源码/证据整理由本主会话负责；后续远端动作和 Windows 实际验收按反馈推进。C、父 T-039、T-040 尚未结案；M6/T-033 保持延期。

## 当前本地依据与原始失败

- 新发布源码：`fb9f46f247e2ed4f6b2ee849e67b4b7d9afbdb11`，源树 `60422257ce3c47f3eb83b291b02a84d4c75add8a`；720个提交源码文件与候选匹配。
- Release：0警告/0错误；全量3755 / 3753成功 / 0失败 / 2个既有error183隔离站跳过；Core983/983、维护39/39、发布工具16/16。
- 两版完整候选：0.2.2为首版（Previous=null，16资产）；0.2.3前版为0.2.2（18资产），各97个实际载荷。固定vpk1.2.161；App/UiaHost为FDD，Maintenance为SC；未签名；full应用，delta仅生成。
- 原v0.2.0与原v0.2.1均保留。实际0.2.1/run41289210712建测失败、打包/发布跳过，没有正式0.2.1安装资产。原ZIP503625字节、SHA256 9c1f5e441d542332d58e7c6694541c3eaedc3e1ba7f8de17006b7984153c2e45匹配官方digest；原全量3754/3752成功/1失败/1跳过及所有原件保留。
- 已修正CI并发测试的共享可释放快照夹具，每个请求独立租约并以握手验证真实重叠与分别释放；产品翻译实现不变。
- 先前source dfc68cc的0.2.1完整候选已在smoke13证明无.NET10/无缓存的默认引导、97文件最终位置/指纹及no-start；这项旧版本实证保留。新0.2.2包的实际正式源安装交用户手工验证，尚未执行或记成通过。
- 0.2.2/0.2.3标签尚未创建/推送。先确认新0.2.2发布；旧0.2.1的授权不代替本次确认，0.2.3须在旧实例/数据就绪后另行确认。

## 操作顺序

| 步骤 | 执行人与操作 | 通过条件与需反馈内容 |
|---|---|---|
| 0 | 用户确认第一版发布；本主会话随后正常推送已审历史到 main/codex 分支，只创建并推送指向上述源码的 annotated v0.2.2，等待 Actions 构建、草稿字节读回与公开发布 | 先回复“确认发布0.2.2”。CI 完成、实际公开资产/哈希核对后，本主会话给出正式下载地址与实际公开 SHA；当前本地候选 SHA 不冒充 CI 包 SHA |
| 1 | 用户双击本单 `.wsb` 配置，创建全新 Sandbox。在来宾浏览器从正式 GitHub Release 下载外层 `Iris-0.2.2-Setup.exe` 并正常安装；不预装 .NET、不手工填运行时缓存 | 记录初始无 Desktop10/无缓存、权限/系统提示、安装完成、真实 Iris 运行及设置页版本0.2.2。所有本轮安装/更新/卸载在此来宾执行 |
| 2 | 用户在旧版设置→常规取消“启动时检查更新 / Check for updates at startup”并保存；按下方合成服务步骤制造设置、DPAPI秘密、历史和日志，然后保持旧版运行 | 设置已保存，合成服务 Key 已保存且首条翻译成功，历史可打开。保存 `old-ready` 快照，反馈“0.2.2已就绪，合成数据已准备”，暂不升级 |
| 3 | 用户确认第二版发布；本主会话核对旧实例实证后，只创建/推送 v0.2.3，等待该版 CI/公开读回完成 | 回复“确认发布0.2.3”。该版发布成功后，再操作下面三步；两个 tag 不一起推送 |
| 4 | 用户在真实设置→常规点击“检查更新 / Check for updates”，等出现0.2.3可用；此时不点下载 | 仍运行0.2.2；未出现0.2.3完整包、未启动安装/重启。截状态图并保存 `offer` 快照 |
| 5 | 用户点击“下载更新 / Download update”，等到可安装；此时不点安装 | 显示 Ready/可安装；0.2.3完整包已到本产品缓存；应用仍为0.2.2，原主进程继续运行。截状态图并保存 `ready` 快照 |
| 6 | 用户点击“安装并重启 / Install and restart”，在确认框确认。旧应用退出后等待新实例；用托盘打开设置和历史 | 当前版本0.2.3；完整退出/维护/native顺序由原始进程及SDK日志复核；正常应用只重启一次。设置/已保存Key/历史仍可读，合成翻译再次成功。保存 `updated` 快照、版本/历史截图 |
| 7 | 用户在来宾系统“已安装的应用”里对更新后的 Iris 卸载，选择默认保留数据 | 程序、快捷方式和卸载项移除，Iris进程结束；用户数据目录保留；共享.NET运行时仍在。保存 `uninstalled` 快照及结果，停止本单记录，保留输出后关闭Sandbox |

任何一步失败就停在该步骤，保留当前窗口、错误与原始日志并反馈。由本主会话修复/重新绑定来源与唯一版本，再给后续步骤；不覆写既有发布或失败记录。

## 来宾准备与证据命令

该 `.wsb` 只共享本单 `input`（只读）和 `output`（可写），没有自动安装或自动启动命令，剪贴板重定向关闭。正式发布后再进行线上验收；只在来宾执行以下辅助命令。

安装前，在来宾 PowerShell 执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\IrisAcceptanceInput\manual-start-evidence.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\IrisAcceptanceInput\manual-capture-state.ps1 -Stage clean
```

确认 `C:\IrisAcceptanceOutput\session-...\provider-status.json` 和 `watcher-status.json` 的 Ready=true。前者只提供合成翻译，后者记录进程事件；它们不安装或控制 Iris，不替换生产更新源。安装包未签名，系统提示与提升要求作为实际结果记录。

完成各阶段后，用同一命令保存快照，仅更换 `-Stage`：`installed`、`old-ready`、`offer`、`ready`、`updated`、`uninstalled`。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\IrisAcceptanceInput\manual-capture-state.ps1 -Stage old-ready
```

记录输出会自动出现在宿主 `F:\Iris\artifacts\t039c\manual-verification-v022-20261101-1\output`。快照记录版本、运行时、进程、完整包和数据文件哈希，并复制原始产品/SDK/运行时日志；秘密文件仅记录哈希。截图只截 Sandbox 窗口并保存到同一输出目录。快速hook可能记录为 StartedUnresolved，交本主会话结合SDK原日志归类，不能据此自行宣布单次重启通过。

验收结束或暂停采集时：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\IrisAcceptanceInput\manual-stop-evidence.ps1
```

这只通知本单记录器和合成服务自行收尾，不杀 Iris；关闭Sandbox前保留全部输出。重做时开启新来宾/新记录会话，保留旧会话。

## 制造可验证的合成设置、秘密与历史

在真实 Iris 设置→翻译服务新增 OpenAI兼容服务，填入并保存：

| 字段 | 值 |
|---|---|
| 名称 | `T039C Synthetic` |
| Base URL | `http://127.0.0.1:18039/v1` |
| Model | `iris-fixture` |
| API Key | `iris-evidence-not-a-real-key`（专用虚构值） |
| 启用/当前服务 | 启用并设为当前 |

该回环服务要求实际收到上述虚构 Key 才返回译文；不读取/打印真实凭据。只填写这些合成值。

从托盘“输入翻译 / Input translation”打开真实翻译窗口，输入 `Iris acceptance check` 并翻译。应收到 `Iris 公共升级验证`，从托盘“历史 / History”打开该记录。升级后再翻译一次，确认同一服务的已保存 Key 仍实际可用，旧历史仍可读。更新检查与下载始终使用 `Tmcity/Iris` 的正式生产源。

## T-040 后续完整手工验收

C/父T-039的生产闭环通过后继续，届时补充具体场景/计时入口；以下要求仍保留，未因当前本地验证而通过。

- 功能：托盘/单实例/CLI/IPC退出；设置保存、取消、冲突与秘密保管；翻译流式、并列服务、取消/错误、缓存/历史/重译；热键、剪贴板与划词；截图翻译、截图OCR、静默OCR、取消/焦点恢复；OCR与代理设置；zh-CN/en-US、明暗主题、DPI/小工作区；安装更新与卸载恢复。
- 冷启动≤1.5秒，测量环境CPU负载<30%；单4K屏热键→截图覆盖层≤150毫秒；划词热键→翻译窗口≤300毫秒（不含选区读取超时）；首个流式字符的Iris附加开销≤50毫秒。
- 稳态（启动空闲30秒、完整GC后的状态）Private Bytes≤100MB / Working Set≤160MB；空闲CPU≈0%，开启鼠标钩子时<0.5%；未处理异常记录日志并提示，无静默退出。以ARCH§1.2原上限验收。
- M6/T-033悬浮球继续延期，不纳入本轮，需求保留。

## 每步反馈格式

```text
步骤：0 / 1 / 2 / 3 / 4 / 5 / 6 / 7
结论：确认执行 / 通过 / 未通过
显示版本：
旧/新PID（若已记录）：
现象或错误原文：
合成设置/Key/旧历史是否保留：
输出会话目录、截图文件名：
```
