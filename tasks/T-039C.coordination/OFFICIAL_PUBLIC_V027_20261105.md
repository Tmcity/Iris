# 正式 0.2.7 公开发布复核

[正式 Release](https://github.com/Tmcity/Iris/releases/tag/v0.2.7)与[实际 CI 41976966535](https://github.com/Tmcity/Iris/actions/runs/41976966535)均已成功。attempt 1 的 package / publish jobs 分别为 `127077966898` / `127080878399`；发布结果原件显示 `published-and-listed`、Public=true、VisibilityVerification=completed。

原 annotated tag `51b6a3d3c334ce604e9479948ef9f10fafd7d7c5` 指向 `fc44a3a78c2729c25dfb91296c51602b843a94f9`，规范来源树 `81aea2fa6cad8a3dcde34b081185dd03aae9c31c`；main/codex 与所有旧标签保持。原匿名 API 限额前置失败保留于 receipt1，实际成功推送与公开复核为 receipt2。

| 核对项 | 实际结果 |
|---|---|
| CI 全量 | 原 XML 3759 总计 / 3759 成功 / 0 失败 / 0 跳过；Release 零警告、零错误 |
| CI 原件 | 验证 ZIP 506138 字节 / 官网 SHA256 `f74d5800df0525b1594f5793394c8d6992f35b442ed429d9a7504bd4389f27a9`；发布 ZIP 423 字节 / 官网 SHA256 `22e043efae52700f56676539b6c87848ac86d4b2bd71d7755129656454b72cb8` |
| 来源 | 721 提交 blob、SourceCommit / canonical tree / CI run / attempt / tag 匹配 |
| 资产 | 全部 18 项实际匿名下载；长度/清单/官网逐项 SHA256 匹配，含 SHA256.json 本身 |
| 载荷 | 实际 full 97 项与 payload-manifest 一致，FDD/SC/版本/外层与来源绑定核对通过 |
| 正式 Setup | 116767767 字节，SHA256 `d294341700e1d9155567fadc56b9c3f1883217bd3cf0ce23102c94f57c83d570` |
| 正式 full | 64627391 字节，SHA256 `f8f9de897b637f8c98a07d601420885bf65529701d014fc52b59f87ce75bace8` |
| 正式旧 full | 0.2.2 原 64627273 字节 / SHA256 `c86982f930aafcd142167bbd871d03b588630f7d1d625da968a811daa2dd5d14` 保持 |

正式下载目录为 `F:/Iris/artifacts/t039c/public-0.2.7-download-20261105-1`。[公开回读](F:/Iris/tasks/done/T-039C.evidence/publication-v0.2.7-20261105-2/public-download-20261105-1/review.json)与[原 CI/发布原件](F:/Iris/tasks/done/T-039C.evidence/ci-v027-original-41976966535-1/binding.json)已准备归档。官网 digest 与恢复后的官方 REST API 均已独立匹配两份原 ZIP 和全部18项下载资产；Release id434836973、公开状态和最终 run/job 均由官方 API 再次确认。

2026-11-05T06:14:26UTC 的实际只读复核仍为同一新 Sandbox 的原 0.2.2/PID7312、V3/PID8152、开关false及原三项数据哈希。正式新载荷验收辅助材料已部署到当前映射输入目录，PowerShell5.1解析通过；没有在宿主或来宾自动安装/升级 Iris。

用户下一段为[真实界面检查更新](F:/Iris/tasks/T-039C.coordination/MANUAL_OFFER_V027_20261105.md)，问题已发送，当前尚未收到 offer 反馈。复核 offer 后才下载，Ready 复核后才由用户确认安装；之后验证正常退出/native顺序/恰好一次重启、97项正式安装字节、旧设置/秘密/历史和原服务新翻译，再用户卸载默认保留数据。C/父T-039及T-040/NFR均未结案，首次安装性能问题仍待处理，M6延期。
