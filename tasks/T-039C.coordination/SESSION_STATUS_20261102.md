# T-039C 当前接续状态（2026-11-02）

**最终更新**：下方“验证正在运行/下一步打包”为过程记录，均已完成。source942b0dd/tree0146030d的最终Release0/0/full3756/3754成功/0失败/2既有error183跳过；Core984/App866/维护39及发布工具16通过。0.2.4 public-base-candidate-1打包exit0，独立18资产/97载荷/720来源与正式旧0.2.2full全部匹配；外层SHA047d1acf…、full964621da…。新source分支已正常合并到本主分支9e4ddfd，无远端写入。原用户Sandbox07:03UTC仍为0.2.2/PID6604、V2记录器6956 Ready及原设置/秘密/历史哈希。新PREPUBLICATION_REVIEW_V024_20261102.md与MANUAL_UPDATE_V024_20261102.md已完成；原CI及本地失败/中间夹具失败完整保持。下一步仅在用户新确认后创建并推送v0.2.4→942b0dd，不推分支；当前该版本未授权/创建/推送，C/父T-039/T-040/NFR及M6范围保持。

用户授权仅发布第二版 0.2.3。已正常创建并推送 annotated v0.2.3（b4b8be76b6f4f08f62067d2a73f3e443fc439481→fb9f46f247e2ed4f6b2ee849e67b4b7d9afbdb11），没有推送 main/codex 分支。实际 CI41397874525 的建测失败，打包/发布跳过；0.2.3 没有正式资产，原标签和原始失败保持，不重跑。

原 ZIP504414 字节/SHA256 9a33ebe306fb94f929e99ec32cad0caa01f7fff765978a2806abb209f51cb61c 与官方 artifact13120282071 digest 匹配，51 个原件已归档 ci-v023-original-41397874525-1。原 full3755/3754成功/1失败/0跳过；唯一失败是 NetworkSettingsTests 的全局秘密读取计数8/12。独立受控反例相同失败已保留（仅夹具注入其他键读取，生产代码不变）。修后按当前代理键计数、仅修改该键并新增真实并发干扰回归；网络27/27通过。

本地下一版计划0.2.4（前版为实际正式0.2.2）；仅本地准备，未授权任何新标签或分支推送。源分支 codex/t039c-ci-v023-fixture，工作树 F:/Iris/artifacts/t039c/worktree-v024-ci-fix-20261102-1；原FB工作树和所有候选保留。第一源3372f79的Release0/0，全量3756/3752成功/2失败/2既有error183跳过。两个UI夹具失败已最小修正：等待异步自启重试状态通知；先确认取消回调进入，再完成直接返回的外部写入promise，保留“写入完成但回调未退出时必须等待”断言。第一次异步writer改写及ConfigureAwait(false)版本仍失败，原输出保留；直接外部promise版本实际1/1通过，其余相关36项在上一轮通过。

当前固定发布源码942b0ddeb62d9eb3e4f244574c0c093ab2942ffc，仅三个测试文件、release-plan及发布说明变化；src/契约/schema/依赖未改。最新全量验证 local-validation-v024-20261102-2 正在运行（源工作树中的 release_pipeline.py validate；此前exec session32938）。该目录结果存在后先读取实际summary/原日志，不重复或并行建测。

全量通过后：从此源执行release_pipeline.py package，version0.2.4，唯一release-0.2.4-public-base-candidate-1 / package-0.2.4-public-base-candidate-1，validation为上述-2目录，previous为F:/Iris/artifacts/t039c/public-0.2.2-download-20261101-1，tool-source为F:/Iris/artifacts/t039b/tool-feed。独立核对新来源/18资产/97载荷/正式旧包、保存全部本轮原件和更新发布/手工材料，正常合并此源分支回主工作分支、仅本地归档。准备完成后才请用户另行授权v0.2.4→942b0dd；先复查版本占用，未占用版本方案可能调整，不直接发布。

用户的同一Sandbox590f8848-f6b4-4bdb-a151-85c4a4f19305保留；旧应用0.2.2/PID6604、已保存开关/虚构Key/旧历史此前独立通过。旧provider4440、旧watcher3572、V2watcher6956，输出session-1c8333f923744572ac2c71bab9576af5。源后续动作前刷新真实进程/状态，不把本段PID当实时断言。新保存入口C:\IrisAcceptanceUpgrade映射同一本单input只读，输出映射原output；产品UI由用户手工执行。等待新的正式版成功发布及全部字节复核后才交用户检查→下载→Ready→确认安装/单次重启/保留数据/更新后卸载。C/父T-039/T-040/NFR未结案，M6延期；不改.codex、不发旧工程师聊天、不创建新线程/代理，不在宿主安装Iris或控制用户产品窗口。
