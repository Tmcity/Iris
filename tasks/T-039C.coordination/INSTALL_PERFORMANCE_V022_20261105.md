# 首次安装耗时：新 Sandbox 的正式 0.2.2

用户实际反馈：“安装成功，版本0.2.2，但安装过于耗时（10min）”。对应新 Sandbox `2fc7c168-2f34-437a-8319-8974e6f592b2`；[原始证据与量化结果](F:/Iris/tasks/done/T-039C.evidence/manual-first-install-v022-20261105-1/binding.json)已冻结。

| 阶段 | 实际日志窗口 |
|---|---|
| .NET 10.0.14 安装 | 来宾本地时间 12:57:19 → 13:05:26，487 秒；退出 0，无重启要求 |
| DotNetHost MSI 签名策略核验 | 120.227 秒 |
| DotNetHostFxr MSI 签名策略核验 | 120.177 秒 |
| DotNetRuntime MSI 签名策略核验 | 120.394 秒 |
| WindowsDesktopRuntime MSI 签名策略核验 | 120.464 秒 |
| Iris 部署 | Velopack 13:05:26 开始解包 → 13:05:27 安装完成；App 开始 UTC 05:05:27.928745 |

四次停顿均位于 MSI 原日志第 25 → 26 行，从 `SOFTWARE RESTRICTION POLICY: Verifying package ... against software restriction policy` 到 `... has a digital signature`，合计 481.262 秒。已核对正式 Setup 和 97 项载荷；下载文件时间窗口远小于上述运行时安装窗口，不能据浏览器文件时间推定完整下载性能。

证据证明长等待发生在 Windows Installer 的签名/软件策略核验阶段。CAPI2 Operational 详细日志当时未启用；尚不能认定具体 CRL/AIA 地址、代理或网络请求超时，也不能把此问题归因为 Iris 解包。没有更改系统策略、证书配置或共享运行时。

此首次安装性能问题保持待处理。完成 T-039C 的真实更新/卸载闭环后，在 T-040/NFR 纳入首次安装阶段耗时和签名核验网络诊断；若用户允许另一个干净环境的诊断安装，先准备可审操作清单，再由用户执行。当前已安装运行时的生产升级单独计时，不据现有观察预先宣称升级性能通过。

2026-11-05T10:36:30UTC 补充只读诊断：实际.NET安装器和四个MSI使用同一.NET/Microsoft Code Signing PCA 2024公开叶证书。其CRL与AIA两个原HTTP地址及对应HTTPS地址均当前返回200，耗时138–768ms；这排除了“现在该叶证书的两个地址不可达”的推断，未证明首次安装时的完整签名链/时间戳链等待原因。详细原件在 [certificate network binding](F:/Iris/tasks/done/T-039C.evidence/runtime-public-certificate-network-20261105-1/binding.json)。尚需下一次用户控制的干净安装中记录CAPI2完整链事件，不能据当前可达性关闭安装性能问题。
