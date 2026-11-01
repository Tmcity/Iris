# T-039C 0.2.2新发布检查点

当前第一版0.2.1实际失败已归档。原tag b9f33fd9755684c889a948dafd79dfd53c15caf8/source dfc68cc、run41289210712、原ZIP/digest和全部原始失败保持；原v0.2.0同样保持。没有重跑或覆写。用户仅授权过0.2.1，新的远端操作尚未授权。

新source `fb9f46f247e2ed4f6b2ee849e67b4b7d9afbdb11`，源树 `60422257ce3c47f3eb83b291b02a84d4c75add8a`，720项源码与两版provenance逐项匹配。仅修正Core并发测试的独立快照租约/握手/迟启动回归及发布版本计划、说明；产品src、公共契约/schema及固定依赖不变。实际Release0警告/0错误，全量3755/3753成功/0失败/2既有error183跳过，Core983/983、维护39/39、发布工具16/16。

| 版本 | 前版 | 唯一候选 | 发布资产 | 外层SHA256 |
|---|---|---|---|---|
| 0.2.2 | null | release-0.2.2-direct-candidate-4 | 16 | ddbcd4ac5779a82a534b478cbe20b5466289516c03e2d22156d69fe9fbe3048d |
| 0.2.3 | 0.2.2 | release-0.2.3-direct-candidate-1 | 18 | ab88550182f72de8f06e3ce9e0649ba00599e21bc5d0150435dba98d1f3e956b |

两版各97个实际载荷，固定vpk1.2.161、FDD App/UiaHost、SC Maintenance、未签名、full应用。vpk入口扫描及未签名警告在日志中保留；本地与CI分开构建，各自按实际provenance验证，不能推断字节相同。

计划在用户新确认后正常推送本检查点归档提交至main/codex分支（当前远端dcc9ccc是新source祖先），只创建/推送指向上述新source的annotated v0.2.2；实际CI成功、draft下载读回与所有正式公开字节校验后交用户做新Sandbox默认安装，旧版本和合成数据/UI就绪后再单独确认v0.2.3。现在两个新tag均未创建或推送；原0.2.1授权不扩展到新版本。

此前source dfc68cc的完整0.2.1/smoke13默认引导、97/97最终安装字节、no-start及实例结束已证；新source的产品src未改，仍保持该边界。新0.2.2实际正式源/无缓存引导由用户按更新手工清单验证，未执行、不据旧轮宣布新包通过。正式匿名生产UI检查→下载→Ready→确认安装/真实退出/native顺序/恰好一次重启/数据可读及更新后卸载仍必须完成。C/父T-039未结案，T-040全功能/NFR与M6延期规则保持。

操作材料：MANUAL_ACCEPTANCE_V022_20261101.md；独立来源/文件字节审查：next-source-review-v022-20261101-1/review.json；原失败：ci-v021-original-41289210712-1/binding.json。实际用户界面仍由用户操作，辅助记录脚本只是准备材料。
