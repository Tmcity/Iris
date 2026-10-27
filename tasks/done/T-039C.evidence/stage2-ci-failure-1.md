# T-039C 第二阶段首次 CI 失败与待核准方案

2026-10-25。同一 C 继续。没有重推/重用 tag、重跑 workflow、删除草稿、覆写 Release、修改发布源或开始第二版。工单与父 T-039 保持进行中。

## 已实际完成的批准动作

- 原子普通 push 将 `3bb52042184b4c4aef8ad75dfab9bf0f40c1f666` 推到 `refs/heads/main` 与 `refs/heads/codex/t039c-public-release`，main 从初始 `877491e1421197ed6394f53d7f4d6ef57ab22e5e` 正常 fast-forward。原始命令/退出码见 `stage2-push-branches-1`，exit0。
- 新建并仅推送 annotated `v0.2.0`。tag object=`ebe194564c5ada606744b4e484b09f3182e04eae`，解引用严格为已审 source=`dcdfdded311fa00671dfd9747bb2101cbf8964f3`。原文见 `stage2-create-tag-v0.2.0-1` / `stage2-push-tag-v0.2.0-1`；远端事后 refs 单独核对。未建立或推送 v0.2.1。
- 真实 Desktop release run：https://github.com/Tmcity/Iris/actions/runs/40193113435 ，event=push，head_branch=v0.2.0，head_sha=上述 source。run completed/failure。
- package job：https://github.com/Tmcity/Iris/actions/runs/40193113435/job/121168156968 ，completed/failure。失败发生在 `Release build, full tests and offline publishing regressions`。打包、发布资产上传均 skipped；保留失败日志的 artifact 上传成功。publish job 121169436982 为 skipped，未执行发布脚本。
- 公开 tag Release API 实测 404（`stage2-public-observations/v020-release-tag-api-1`）。GitHub 的 tag HTML 页本身可能返回 200，不能把 tag 页面存在当成 Release 已公开。匿名身份不能枚举私有草稿；这里只声明此次 job 没有执行创建/公开，而不是由 404 推断所有账户均不存在草稿。

## 原始 CI 结果

`stage2-ci-failure-v020-original-1/iris-validation/release-build.log`：

```text
已成功生成。
    0 个警告
    0 个错误

已用时间 00:01:45.16
```

发布工具离线回归：16 个，exit0。`full-tests.log`：

```text
  总计: 3728
  失败: 1
  成功: 3726
  已跳过: 1
  持续时间: 3m 19s 276ms
```

唯一失败为 `Iris.Platform.Windows.Tests.Clipboard.Win32ClipboardIsolatedIntegrationTests.IsolatedWindowStation_NativeClipboardIntegration_ExecutesInDedicatedProcess`。真实 helper stdout：

```text
[ISOLATED_CLIPBOARD] Isolated window station & desktop successfully established.
[ISOLATED_CLIPBOARD] FAIL: Expected cancellation/busy when occupied.
```

该轮已经成功建立隔离站并实际进入测试，因此这是实测失败，不能把它并入此前宿主 error183 的环境跳过。另一个既有 ClipboardSnapshot 用例仍因 error183 跳过；不放宽该规则，不过滤失败测试。

失败 artifact id=12599196026，name=`iris-validation-40193113435-1`，大小504029字节，GitHub API digest=`sha256:92ea446dc1ac476513110559fa7d90b0cad5b79edea4d870ae653b78062d904b`。直接匿名下载 API 为403；随后通过 nightly.link 的匿名入口获得官方 Blob 存储 archive，整份 ZIP 的 SHA256 严格匹配从 GitHub API 独立读取的 digest，才解出51份文本/JSON/XML/PNG证据。未向 relay 或来宾传入凭据，未记录签名跳转查询串。完整下载来源与原字节绑定见 `stage2-ci-failure-artifact-binding-1.json`；ZIP只留 artifacts，不提交二进制包。其原始 log/command 中的大小/SHA与解包文件逐项对应。

## 只读源码定位与不确定性

`tests/Iris.Clipboard.IsolatedHelper/Program.cs` 第247行起，在主隔离 MTA 线程 `OpenClipboard(0)` 后，使用50ms CancellationToken同步等待真实 `Win32ClipboardService.GetTextAsync`；服务通过 Task.Run 执行原生读和异步重试。失败说明该调用实际正常返回，没有发生预期取消/Busy。

目前不能仅由这些日志区分：占用条件本身无效、同步等待/任务调度导致线程复用、后台线程未显式绑定同一隔离桌面，或真实服务取消/Busy边界缺陷。源码对照另显示 `SnapshotValidation` 已显式绑定专用工作线程的桌面，而早期 Win32ClipboardService helper 没有同等级的身份诊断。本地该用例一直 error183 跳过，不能用宿主绿色替代 CI 原失败，也不能在宿主直接运行可能进入交互剪贴板的改写探针。

## 建议的具体下一步（尚未实施，待架构师核准）

1. 允许在本单最小扩展测试范围：`tests/Iris.Clipboard.IsolatedHelper/` 和对应 Clipboard 集成测试。先保存修前 source/程序集指纹，再在自有干净 Sandbox 内运行未改产品的定向诊断（若需要共享 .NET，只为测试工具显式准备，绝不记为 C 默认引导验证），或在获批的独立 CI build-only 诊断轮执行；不读取宿主剪贴板。
2. 记录占用线程/服务原生调用线程的实际TID、window-station/desktop身份、owner HWND、每次Open/Close返回值、令牌取消接受与实际工作结束。占用者改为明确、独立、完整join的专用线程或子进程，先验证Open成功再发ready、服务测试完成才release；所有参与者必须绑定同一自有隔离站/桌面，失败仍断言失败，绝不改成skip或仅加长sleep。
3. 若实证证明是夹具问题，仅修夹具、保留真实产品操作与取消/Busy断言，增加确定性协议/清理回归。若显示产品实现真的违反取消/原生线程责任，先向架构师提交具体最小产品改动，不在本次诊断中顺手修改。
4. 新 source 必须独立本地提交、适当定向与Release/full验证、重新生成两版唯一候选并绑定。已用 v0.2.0 保持原失败、原tag/source不可重写，首阶段候选与此轮artifact原件均不改。
5. 版本建议为新 source 的 `0.2.1`（作为首个真实公开版，PreviousVersion=null）→`0.2.2`（仅在旧版公开资产、无缓存默认运行时引导、真实旧App/合成数据已就绪后）。这只是待批准方案，不自行改 release-plan、建tag或push；架构师也可明确核准另一对未占用版本与精确refs。
6. 若采用新版本，先通过架构师对新source/refs/version/CI操作的核准，再执行唯一新tag；不重跑或覆盖已失败v0.2.0，不上传本地包绕过CI。C生产设置UI与默认运行时引导要求保持完整，不以缓存条件或GuestUpdateHost替代。

当前没有运行中的本单构建/测试/打包/来宾，工作区 source 与 checkpoint 不变。等待的是工单规定的 CI失败后架构师范围/新source及版本协调，不是要求用户重复确认仓库或传话。
