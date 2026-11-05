# 手工安装正式 0.2.9 修正版

正式 [0.2.9 Release](https://github.com/Tmcity/Iris/releases/tag/v0.2.9) 的 CI42028799541/attempt1 成功，完整3771项中3770成功、0失败、1项既有error183剪贴板环境跳过。原CI ZIP及发布回执与官网完整digest一致，19正式资产全部匿名下载并逐项匹配官网摘要，来源721项/97载荷/外层/feed/history均通过。本地候选和正式包各自记录，不混用哈希。

在当前同一 Sandbox 中手工执行：

1. 在来宾 `C:\IrisAcceptanceFixed029` 双击 `Iris-0.2.9-Official-Download.url`，或者用浏览器打开[正式0.2.9安装器](https://github.com/Tmcity/Iris/releases/download/v0.2.9/Iris-0.2.9-Setup.exe)。下载后运行，按安装器提示确认安装/覆盖。现有合成配置、凭据和历史继续保留；共享.NET运行时已经安装。
2. 完成后从Iris托盘打开设置，确认实际显示0.2.9。在来宾同目录双击 `Capture-Fixed-V029-View.cmd`，它会在10秒后截图；这10秒内切回Iris的版本设置页面并保持可见。
3. 打开原历史，确认 `Iris acceptance check` 及结果 `Iris 公共升级验证` 仍可查看。沿用保存的合成服务翻译 `Iris acceptance check after fixed install`，确认返回结果；不需要重新填写Key或服务。
4. 双击 `C:\IrisAcceptanceFixed029\Save-Fixed-V029-State.cmd` 保存。它只读采集当前数据/日志/进程并核对正式97项安装文件；如果报错，保留错误原文。

请反馈：安装成功/失败、实际版本、原历史和新翻译是否成功、大致安装用时，以及系统提示或错误原文。保留当前Sandbox、Iris和合成服务；本段不卸载。

正式Setup：116768884字节 / SHA256 `69ab96bb1f700b21fa430d69eab85d6d4a5c606c377424222f0f97af0e7f2891`。正式full：64628500字节 / SHA256 `42c32435e5883608c77f097befd2b9a7f91845b35942a09966018361c40a779e`。已核对的正式payload manifest SHA256为 `3f629d7717f9673a4739da8102b781ee9cd5e242d25930f8262ee3f96ba0ad6a`。

输出仍为原共享会话 `F:/Iris/artifacts/t039c/manual-verification-v022-20261105-1/output/session-14d55ff50fd94d3abe8d4474aafd5365`。新入口只共享本单input，只读；没有新增宿主工作区或秘密共享。记录器和合成服务沿用现有会话，不重新运行Begin-Manual-Evidence。

这次正式安装建立已修正的客户端基线。原0.2.2→0.2.7 UI升级失败及后续手工修复保持为各自的实际结果；本次手工安装也不记作成功UI升级。029安装及数据复核完成后，再准备下一递增正式版并单独请求其发布授权，按真实UI检查/下载Ready/确认安装/native顺序与一次重启/数据/卸载验收推进。C/父T039、T040/NFR、首次.NET安装性能与M6延期保持。
