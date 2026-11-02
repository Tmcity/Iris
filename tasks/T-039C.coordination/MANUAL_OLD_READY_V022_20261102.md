# 正式0.2.2安装已核对，准备升级前合成数据

用户反馈安装成功、版本0.2.2。只读证据：同一Sandbox590f8848-f6b4-4bdb-a151-85c4a4f19305的clean快照初始无.NET10/无缓存；installed显示NETCore/Desktop10.0.14、真实Iris.App PID6604/version0.2.2、正常安装SDK日志；下载器116767621字节/SHA256 ddae984ba4bcc65e5398ecd7a036afe0485966c5d61fe9b1603cc9bb487e7a0c，正式完整包SHA c86982…，97/97最终安装文件与公开manifest匹配。实际正式源默认引导已获得此轮证据。

本次只读采集修复了辅助记录脚本catch中误用$_导致快照失败的问题；产品未改，原脚本在既有Git归档中保留。原进程记录器文件目前为空，不能据此证明后续退出/重启次数；升级前须另核对该证据能力。

用户下一段实际操作：

1. Iris设置→常规，取消“启动时检查更新 / Check for updates at startup”，保存。
2. 设置→翻译服务，新建OpenAI兼容服务，填写下表，启用并设为当前服务，保存（凭据类型选API Key）。

| 字段 | 值 |
|---|---|
| 名称 | T039C Synthetic |
| Base URL | http://127.0.0.1:18039/v1 |
| Model | iris-fixture |
| API Key | iris-evidence-not-a-real-key（专用虚构值） |

3. 托盘→输入翻译，输入“Iris acceptance check”并翻译；应出现“Iris 公共升级验证”。托盘→历史打开这条记录。
4. 在来宾C:\IrisAcceptanceInput双击Save-Old-Ready-State.cmd保存状态。反馈“开关已保存、翻译成功/错误原文、历史可打开/打不开”。保留当前0.2.2运行实例。

该本地合成服务已有Ready记录，只用于制造数据并检验保存的虚构Key；更新源仍为正式Tmcity/Iris。未收到这段实际反馈前，DPAPI秘密/合成历史保留条件未记通过，0.2.3不创建或推送。C/父T-039与T-040/NFR未结案，M6延期。
