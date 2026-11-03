# T-039C 当前接续状态（2026-11-03）

**2026-11-03 当前有效状态**：T-039C仍是唯一活跃工单。用户已确认仅v0.2.5，tag95c9c344…→ee2d711已正常推送；实际CI41461057191建测失败，打包/publish skipped，无正式0.2.5资产。原ZIP506060字节/SHAe3916ed568433386720fbacf81ab41c2c33016f30792fd0c32680ffbd5d69f0f与官方artifact13148997572一致，51原件和三个修前源码已冻结。唯一失败是自启跨保存/Reload测试过早断言后台查询状态；受控查询阻塞反例实际复现同一True/False失败（筛选零测试和初次CA1859编译失败原记录也保持）。修正观察PropertyChanged通知，新增保存已完成但查询仍被显式阻塞的WPF用例；相邻两个自启外部意图用例保留全部断言、等待状态通知并在finally显式放行/完整观察工作。定向18/18通过，固定sourcec32fcb283e417ab11715c46207fdc8cfde4baedf，仅一个测试文件、release-plan和RELEASING变化，生产/契约/schema/依赖不变。完整Release0/0，全量3758/3756成功/0失败/2既有跳过；来源721项、18资产/97载荷及正式旧0.2.2full独立通过。唯一candidate1和PREPUBLICATION_REVIEW_V026已齐备，下一步仅在用户新确认后创建、推送v0.2.6，不推分支。原V2事件清理错误及停止原件保留；V3真实队列缺失清理与辅助native启动/退出0已独立通过。2026-11-03T14:13:32.5032553Z旧应用仍原PID6604、开关false与原三项数据哈希，V3/PID4604 Ready。 0.2.6未授权/创建/推送，原失败标签、候选和CI不重写/重跑。main/codex远端仍285df3a；实际正式前版仅0.2.2。用户Sandbox590f8848…及原合成数据保持，发布前重新查询实际进程/记录器和数据，不能据历史PID断言实时状态。产品UI由用户分段手工执行。C/父T-039、真实UI升级/退出/native顺序/恰好一次重启/数据保留/更新后卸载、T-040/NFR尚未结案，M6延期。不改.codex，不重开工程师聊天或子代理，不在宿主安装Iris。

工作树：F:/Iris/artifacts/t039c/worktree-v026-ci-fix-20261103-1，分支codex/t039c-ci-v025-fixture。

原CI：tasks/done/T-039C.evidence/ci-v025-original-41461057191-1/binding.json；发布receipt：publication-v0.2.5-20261102-1/state.json。

完整验证：artifacts/t039c/local-validation-v026-20261103-1；唯一候选：artifacts/t039c/release-0.2.6-public-base-candidate-1。

正式前版：artifacts/t039c/public-0.2.2-download-20261101-1；full SHA256 c86982f930aafcd142167bbd871d03b588630f7d1d625da968a811daa2dd5d14。

旧来宾入口C:\IrisAcceptanceUpgrade，输出artifacts/t039c/manual-verification-v022-20261101-1/output/session-1c8333f923744572ac2c71bab9576af5。

后续：新版本实际CI成功与正式公开字节/来源完整核对后，才依次让用户检查更新保存offer→下载保存Ready→确认安装及新版本/旧历史/原服务新翻译保存updated→独立复核升级→手工卸载保留数据保存uninstalled并停止记录器。C通过后才推进T-040。
