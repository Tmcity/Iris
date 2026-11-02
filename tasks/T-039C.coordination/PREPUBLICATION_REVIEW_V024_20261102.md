# T-039C：0.2.4 新发布检查点

用户已授权仅第二版 0.2.3，该标签已正常推送，但实际 CI 建测失败，打包/发布跳过。原标签、原失败及全部候选保持；0.2.4 尚未创建标签、推送或授权。新版本发布需要单独确认。

## 原失败与修正

原 v0.2.3 annotated 对象 `b4b8be76b6f4f08f62067d2a73f3e443fc439481` → source `fb9f46f247e2ed4f6b2ee849e67b4b7d9afbdb11`，实际 [CI41397874525](https://github.com/Tmcity/Iris/actions/runs/41397874525) 建测失败，publish skipped。原 ZIP504414 字节/SHA256 `9a33ebe306fb94f929e99ec32cad0caa01f7fff765978a2806abb209f51cb61c` 精确匹配官方 artifact13120282071 digest，51 份原件已冻结。原全量3755/3754成功/1失败/0跳过，唯一失败为网络设置测试将所有秘密读取合计计入代理预算，预期8而实际12；独立受控反例复现同样错误。

修正仅涉及三个测试文件、版本计划和发布说明：网络计数/变更钩子限定当前代理键，新增真实并发其他键读取的回归，保留精确8次/4轮预算及其他值不被修改的断言；自启测试等待异步重试状态通知；剪贴板测试先确认取消回调进入，再完成直接返回的外部写入promise，保留回调未退出时收尾不得完成的断言。

新源第一轮本地全量3756/3752成功/2失败/2既有error183跳过及期间两个仍失败的异步writer夹具改写均保留，未改记通过。最终重新固定源、Release和全量之后才生成下列候选。产品src、公共契约、schema及依赖没有变化。

## 精确候选与验证

| 项目 | 结果 |
|---|---|
| 发布源码 | `942b0ddeb62d9eb3e4f244574c0c093ab2942ffc` |
| 规范来源树 | `0146030d392a0dfef7e481e2e467c3fdd67bf8f6` |
| 唯一候选 | `artifacts/t039c/release-0.2.4-public-base-candidate-1` |
| 前版 | 实际正式0.2.2，旧full 64627273字节/SHA256 `c86982f930aafcd142167bbd871d03b588630f7d1d625da968a811daa2dd5d14` |
| 来源/资产/载荷 | 720/720源码提交blob和工作字节匹配；18项发布资产、97项实际载荷，feed/full/外层/provenance通过 |
| 本地外层SHA256 | `047d1acf375a169f5bf07aa009e7be898bee80235d013616a669c5afc0d8cea7` |
| 本地full SHA256 | `964621da098da7c2fa2b00caa36024d23bc24557f09a32153c24558bf857a3dc` |
| 最终Release | 0警告/0错误 |
| 最终全量 | 3756总计 / 3754成功 / 0失败 / 2既有error183隔离站跳过，原原因保留 |
| 分项 | Core984/984、App866/866、维护39/39；发布工具16/16 |

独立审阅：`tasks/done/T-039C.evidence/next-source-v024-20261102-1/review.json`；原CI：`ci-v023-original-41397874525-1/binding.json`；原本地失败：`local-v024-validation-failed-20261102-1/`。固定vpk1.2.161；App/UiaHost FDD、Maintenance SC，未签名；full应用，delta仅生成。CI会单独构建并按自己的实际字节核对，本地SHA不作为未来正式CI包SHA。

## 旧版与实际用户路径

同一用户Sandbox590f8848-f6b4-4bdb-a151-85c4a4f19305保持。最新只读快照 `2026-11-02T07:03:17.7132861Z` 仍为原正常0.2.2/PID6604（开始04:25:01.2204360Z），仅有正式旧full，settings/secrets/history三项哈希与原old-ready一致；V2观察器6956 Ready、heartbeat07:03:17.2375383Z。开关已保存为false，虚构Key的真实POST200及旧历史已独立核对。未读取秘密内容。

发布前会再次核对同一旧实例/数据、观察器以及远端版本占用。保留该Sandbox及Iris运行。实际产品UI仍由用户按[0.2.4手工升级清单](F:/Iris/tasks/T-039C.coordination/MANUAL_UPDATE_V024_20261102.md)逐段操作；新保存入口C:\IrisAcceptanceUpgrade及原output会话继续适用。

## 请求确认的动作

用户明确确认后，仅创建 annotated `v0.2.4`，指向上述发布源码 `942b0dd…`，正常推送这个标签到 Tmcity/Iris，由既有Actions发布；不推送main/codex分支，也不重跑或覆写v0.2.3。当前本地已正常整合新源码与原审阅历史，标签固定源码提交，不指向后续整合/报告提交。

实际CI成功、草稿下载读回和公开可见性/18项正式资产全部核对后，再交用户先执行“检查更新”并保存offer；之后才下载→Ready→确认安装/完整退出/native顺序/恰好一次正常重启/保留设置Key旧历史及更新后卸载。任何失败保留原件，再审具体修正与唯一版本，不把启动workflow或候选通过当成正式发布通过。

C/父T-039、真实UI升级/单次重启/更新后卸载与T-040/NFR未结案，M6/T-033延期。
