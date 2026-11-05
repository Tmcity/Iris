# 正式 0.2.7：先核对更新提示

[0.2.7 正式 Release](https://github.com/Tmcity/Iris/releases/tag/v0.2.7)已发布。实际 CI `41976966535` / attempt 1 的 3759 项全部通过；原 ZIP 与官网 digest 匹配，正式 18 资产 / 97 载荷 / 721 来源均独立核对，所有下载还与官网展示的 SHA256 匹配。

| 正式资产 | 字节数 | SHA256 |
|---|---:|---|
| Iris-0.2.7-Setup.exe | 116767767 | `d294341700e1d9155567fadc56b9c3f1883217bd3cf0ce23102c94f57c83d570` |
| Iris-0.2.7-full.nupkg | 64627391 | `f8f9de897b637f8c98a07d601420885bf65529701d014fc52b59f87ce75bace8` |

2026-11-05T06:14:26UTC 已只读复核当前 Sandbox `2fc7c168-2f34-437a-8319-8974e6f592b2` 的原 0.2.2/PID 7312、开关 false、三项数据哈希和 V3 记录器保持。

请在保留的当前 Sandbox 手工执行本段：

1. 从 Iris 托盘打开设置，在“常规”页的更新区域点击“检查更新”。
2. 应显示当前 0.2.2，提供 0.2.7。待检查结束，在来宾 `C:\IrisAcceptanceInput` 双击 `Save-Offer-State.cmd` 保存记录。
3. 反馈当前版本、提供的新版本、界面状态；如果失败，反馈错误原文，并仍保存本段记录。

本段只检查更新。助手核对提示和实际日志后，下一段再点击下载；下载 Ready 复核后才确认安装。保留原 Sandbox、Iris 实例、虚构服务与记录器。完整后续安排见 [分段升级清单](F:/Iris/tasks/T-039C.coordination/MANUAL_UI_UPGRADE_V027_20261105.md)。

首次 .NET 安装耗时问题继续保留，实际升级耗时另行记录。C/父 T-039 与 T-040/NFR 尚未结案。
