# 继续同一已授权的 0.2.7 标签发布

用户已明确确认“确认发布0.2.7（仅新版本）”，实际 question item 与原答复记录在 `v027-user-approval-20261105-1.json`。发布源码仍为 `fc44a3a78c2729c25dfb91296c51602b843a94f9`，审阅归档仍为 `06b282bd928249519ea07b3eea999238f1202328`，main/codex 分支没有推送范围。

首次执行在发布前的匿名 REST 查询遇到共享限额 HTTP403，remaining=0，reset=2026-11-05T06:25:19UTC。原 receipt `publication-v0.2.7-20261105-1`、源执行器及全部原始结果完整保留。实际本地/远端均无 v0.2.7，标签创建及 push 尚未请求；这不是 CI 失败。

官网读取路径已实际预检通过：正式旧 Release 页面 HTTP200，新版页面 HTTP404；正式 `releases.win.json` 和旧 full 重新匿名下载成功，64627273 字节 / SHA256 `c86982f930aafcd142167bbd871d03b588630f7d1d625da968a811daa2dd5d14` 与已审阅官方 digest 及基线相同。源码/18 候选资产/原 refs/新来宾进程/开关/三项数据哈希也保持。此路径使用 GitHub 官网与发布下载地址，未重复请求被限额的 REST API。

执行器新增显式 `--public-verification website` 与唯一 `--publication-attempt 2`，继续同一已授权的 v0.2.7 / 固定源码；不更改生产实现、候选、审批范围或失败记录。新模式下缺少本版授权的反例仍在任何目录/标签创建前退出 2。新执行会再次读取实时状态，结果写入新的 `publication-v0.2.7-20261105-2`。

[官网下载复核与原始记录](F:/Iris/tasks/done/T-039C.evidence/v027-public-read-fallback-20261105-1/binding.json)已准备归档。实际 64 MB 重新下载原字节保持在绑定所指本地路径，Git 归档保留长度/SHA、官网原响应和命令记录。

获得同一版本授权后无需重复请求确认。推送后继续核对实际 CI、正式匿名资产及用户手工升级/卸载；C/父 T-039、T-040/NFR 与首次安装性能问题均未结案。
