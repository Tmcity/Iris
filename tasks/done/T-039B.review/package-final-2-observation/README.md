# PowerShell 5.1产物与载荷清单核对

2026-10-15。独立只读检查release-0.2.0-final-2和release-0.2.1-final-2，未安装或执行包。实际PS5.1打包日志表明已用Windows PowerShell 5.1.26100.9549 / Desktop完成这两个版本；此前C盘临时空间不足的两份失败保持。

本次检查确认两版的SHA256.json所有已列文件大小/哈希均匹配，外层安装器附加载荷的Magic、长度边界及SHA256匹配；共享载荷文件的实际包内字节与当前清单一致。详见result.json。

**payload-manifest.json不是实际包内清单**：两版均记录102项staging文件，实际lib/app载荷97项。vpk排除了8个PDB，新增Iris_ExecutionStub.exe、Squirrel.exe、sq.version；当前清单多列前者、漏列后者。应保留需要的发布输入清单，并另外从实际完整包生成准确的载荷清单及大小/SHA，或明确分开命名。最终交付中不能把打包前目录清单当作实际包内文件列表。这是交付元数据偏差，不据此宣称程序包内容被破坏。

最终源码/最终包绑定及运行验收仍待完成；不能仅凭这些校验结果结案B。
