# T-039C 新源码发布检查点（本地材料）

2026-11-01，本主会话直接实施与复核。用户最新要求实际操作逐项手工确认、验证后反馈；本检查点整理具体待确认材料，尚未执行新push/tag/Release动作。

发布源码 `dfc68ccba422c735edeab42635c19b7767b9f2c2`，源树 `396b90efadba83b7caa53789d6c9364fd4c54539`，720份提交源码逐项匹配两版provenance。Release零警告/错误，全量3754/3752成功/0失败/2个原有error183跳过，维护39/39，发布工具16/16。原始日志、XML、失败和旧候选保留。

| 版本 | 前版 | 本地完整候选 | 发布资产 | 外层SHA256 |
|---|---|---|---|---|
| 0.2.1 | null | release-0.2.1-direct-candidate-3 | 16 | d086e9b8937b269080eb5388c2bf393669bc50af68fd7e773c1f22e68cac9e51 |
| 0.2.2 | 0.2.1 | release-0.2.2-direct-candidate-3 | 18 | cc05b9c7ae988192bed0667e6b54da8b56b765d885a9a5a024282023c6f9771e |

完整新0.2.1在smoke13的新Sandbox（acfc6bc3-094b-4876-96e4-1838d5bdfde0）初始无.NET10/无缓存，真实外层code0、标准Setup安装Desktop10.0.14，97/97最终安装字节匹配，无正常App启动，实际stop/list-after通过。SDK安装映射Iris_ExecutionStub.exe→根Iris.exe、Squirrel.exe→根Update.exe与固定1.2.161原源码、实际日志及文件hash一致；其余95项在current。smoke11的PS5.1数组夹具失败、smoke12的SDK路径夹具失败均保留，不改记成功。smoke10的混合诊断外层不是正式候选，未用于此检查点。

拟正常推送本检查点归档所在提交到 `refs/heads/main` 与 `refs/heads/codex/t039c-public-release`；远端当前两者为3bb52042184b4c4aef8ad75dfab9bf0f40c1f666，该提交是发布源祖先，保留两段既有历史。首个新annotated `v0.2.1`仅指向上述发布源码，经用户第0项确认后才创建并推送；Actions完成/草稿读回/实际公开字节核对后交用户做正式源新来宾验收。

旧0.2.1正常运行、合成数据/持久化开关和用户反馈就绪后，再按第3项确认创建/推送指向同一发布源码的 `v0.2.2`。不一起推两个tag，不覆写已有tag或Release。v0.2.0 annotated对象ebe194564c5ada606744b4e484b09f3182e04eae/source dcdfdded311fa00671dfd9747bb2101cbf8964f3、run40193113435失败保持。

CI使用同一固定vpk1.2.161、FDD App/UiaHost、SC Maintenance、full更新；本地与CI是分别构建，各自按实际provenance/清单校验，不推断字节相同。Git仅用此前授权的SSH配置，发布job仅使用临时GITHUB_TOKEN；本机/客户端不读取或嵌入密钥。当前包未签名，vpk入口识别/未签名警告如实保留。

当前结论：本地新source/建测/两版完整候选/无缓存安装检查点通过；新远端动作待用户手工确认，正式公开源安装、真实设置UI升级/单次重启/数据保留/更新后卸载未完成，C及父T-039不结案。T-040全功能/NFR与M6延期规则保持。具体用户操作见MANUAL_ACCEPTANCE_20261101.md。
