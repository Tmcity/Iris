# 当前手工步骤：正式0.2.2安装与旧版就绪

第0项已完成。正式Release：https://github.com/Tmcity/Iris/releases/tag/v0.2.2；实际Actions：https://github.com/Tmcity/Iris/actions/runs/41293681371，构建零警告/错误、全量3755/3755成功/0失败/0跳过、发布draft读回/公开成功。16个实际公开资产已无登录下载/逐项校验；不使用本地候选哈希作为正式包依据。

## 只执行本段第1、2项

1. 双击宿主 `F:\Iris\artifacts\t039c\manual-verification-v022-20261101-1\Iris-Manual-Acceptance.wsb` 开新Sandbox。该配置没有自动安装命令，只映射本单input/output，剪贴板重定向关闭。只在来宾进行本轮Iris实际安装/升级/卸载。
2. 在来宾PowerShell开始本单记录并保存干净状态：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\IrisAcceptanceInput\manual-start-evidence.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\IrisAcceptanceInput\manual-capture-state.ps1 -Stage clean
```

确认 `C:\IrisAcceptanceOutput\session-...\provider-status.json` 与 `watcher-status.json` 为Ready=true。`clean`快照应显示未有.NET10 Desktop Runtime、下载缓存不存在。不要先装.NET或填缓存。

3. 在来宾浏览器从正式Release下载：https://github.com/Tmcity/Iris/releases/download/v0.2.2/Iris-0.2.2-Setup.exe

正式安装器：`Iris-0.2.2-Setup.exe`，116767621字节；SHA256：

```text
ddae984ba4bcc65e5398ecd7a036afe0485966c5d61fe9b1603cc9bb487e7a0c
```

若浏览器保存到默认Downloads，可在来宾核对：

```powershell
Get-FileHash -LiteralPath "$env:USERPROFILE\Downloads\Iris-0.2.2-Setup.exe" -Algorithm SHA256
```

4. 正常运行已下载外层安装器，记录系统提示、运行时提升要求和实际安装结果。安装器未签名；遇到阻断/取消/失败则保留当前状态反馈。成功后启动真实Iris，从托盘“设置/Settings”查看当前版本0.2.2；保存installed快照与版本图：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\IrisAcceptanceInput\manual-capture-state.ps1 -Stage installed
```

5. 在设置→常规取消“启动时检查更新 / Check for updates at startup”并保存。按完整清单创建专用合成OpenAI兼容服务：名称`T039C Synthetic`、Base URL `http://127.0.0.1:18039/v1`、Model `iris-fixture`、API Key `iris-evidence-not-a-real-key`（虚构值），启用并设为当前。由本单回环服务提供合成翻译，更新始终走生产GitHub源。
6. 托盘→输入翻译，输入`Iris acceptance check`并翻译；应得到`Iris 公共升级验证`。从托盘→历史打开该记录，确认Key已保存、设置/历史可用。保存old-ready快照：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\IrisAcceptanceInput\manual-capture-state.ps1 -Stage old-ready
```

7. 反馈本段结果：安装成功/未成功，显示版本，运行时/提升提示，合成翻译与历史是否成功，输出会话目录和截图文件名。保持这个旧0.2.2实例与数据，等本主会话复核后再单独确认发布0.2.3，随后才做检查→下载→Ready→确认安装/单次重启/数据保留与卸载。

输出会在宿主 `F:\Iris\artifacts\t039c\manual-verification-v022-20261101-1\output`。完整步骤和辅助记录说明见`MANUAL_ACCEPTANCE_V022_20261101.md`。脚本只做过PowerShell5.1语法检查，此次真实安装/合成服务实际执行由用户验证；结果不预记通过。
