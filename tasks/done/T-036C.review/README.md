# T-036C 独立验收

2026-10-08，架构师验收提交 `11263780f2a34dd9ee4dc998d80dfdf605dacaf0`。结论：通过，T-036C、父 T-036 与 CCR-036 可结案。

## 最终验证

- `final-build.log`：实际 `dotnet build Iris.slnx -c Release`，0 警告、0 错误。
- `final-reload-regression.log`：原 `SettingsDraftAndSaveRegressionTests.Scenario4_StaleResults_RejectedOnSwitch_EndpointChange_Edit_Delete_OrReload` 定向 1 项通过；原测试文件及旧结果断言没有修改。
- `final-independent-tester.log`：原独立实际 Tester/Session 的四种状态、纯 Create、HEAD/无 Origin 认证、Run 与 Dispose 所有权分离，以及真实 Sockets 内存流的代理认证、重定向、16KB 头预算检查通过。快照源是窄边界替身，不以该探针单独证明完整 Core 集成。
- 取消回调原反例最终输出为 `CANCEL_CALLBACK firstFailed=True; retryFailed=False; heldSessions=0; handlerDisposed=True; sends=1`。首次故障可观察，原会话重试完整释放，未执行 `FIXTURE ONLY` 反射分支。
- `final-independent-ui.log`：实际 Window/VM/Session、临时 JsonSettingsService、Core 与 Tester 的粘贴/测试/联合保存，以及手动清理最后 UI 发布阻止保存越过的两项探针通过。
- `final-full.log`：实际 `dotnet test Iris.slnx -c Release`，3553 总计、3551 成功、0 失败、2 项既有 window-station error183 环境跳过，持续 3m 34s 891ms。

上述构建与测试串行执行。独立探针使用已归档的 `independent-tester`、`independent-ui` 源码；最终检查确认临时执行源与归档相同，探针引用的 7 个产品程序集副本均与本次重建产物相同。

## 产物与视觉

`final-binding.json` 绑定当前提交、608 项源码/测试（与开发最终源码清单全部一致）、13 个重建程序集、5 份最终原始日志及 48 张图。提交后重建的程序集版本标记为 `0.1.0+11263780f2a34dd9ee4dc998d80dfdf605dacaf0`；其 DLL 哈希与提交前开发清单不同，本验收绑定并执行的是提交后的重建版本，不声称两次 DLL 字节相同。

独立全量输出的 `final-visual` 全部 48 张与已查看的交付图逐字节一致。交付图中 44 张继承先前原尺寸检查，4 张变化图已使用 view_image 单独查看：Light-600-transport-failed、Light-600-http-response、Light-760-transport-failed、Dark-600-transport-failed。取消按钮禁用、HTTP 401 说明、长标签换行及明暗文字均可读；记录为 `visual-review/delivery-reviewed.json`。

提交相对 B/R1 基线的 Core 网络/设置、Infrastructure、NetworkResponseContent/NetworkTransportManager、CONTRACTS、默认设置及包版本均无差异。C 仅增加声明内的设置页、真实 Tester、必要组合与验证。

## 原始证据及限制

保留 `recovery-full.log` 的原 Reload 失败及 `independent-tester/cancel-callback-1.log` 的真实重复清理失败，连同其仅用于安全释放夹具的反射诊断说明；所有早期失败、警告和候选视觉均未覆盖。

未访问用户配置、凭据、代理、外网、剪贴板或输入，未启动真实 Iris 主应用，未运行性能脚本。真实代理/TLS/SOCKS/PAC/Windows 集成身份、物理桌面与 NFR 未验证，仍属 T-040；已有获准只读 native/合成 SDK smoke 保持。
