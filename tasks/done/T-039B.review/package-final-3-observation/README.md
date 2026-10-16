# 载荷清单修正后的独立复核

2026-10-15。对release-0.2.0-final-3和release-0.2.1-final-3只读复核：payload-manifest.json与实际full包lib/app的97项文件集合完全相同，每项解压大小和SHA256均匹配。打包前清单另存staging-manifest.json，原final-2偏差及记录未覆盖。

两版顶层SHA256清单分别核对7项和9项，全部一致；外层安装器附加载荷的Magic、长度边界和SHA256亦一致。原始清单副本及结果在本目录，具体包和安装器指纹见result.json。

此结果关闭已发现的载荷清单元数据问题，只证明所列静态产物校验，不替代最终来源绑定、真实安装/升级或整个B验收。
