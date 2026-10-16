# 单文件附加载荷可行性探针

此为架构师自有最小探针，与Iris主应用、生产维护程序和真实安装器无关。用于验证.NET10 self-contained单文件在追加受限载荷/尾部索引后仍可装载，以及载荷校验能拒绝损坏。不会执行载荷、请求网络、改注册项或访问Iris数据。

只在任务专用临时目录发布和运行；子进程的bundle解压根及DOTNET_ROOT指向自有目录。观察实际coreclr模块路径是否属于该解压根，避免只从publish参数推断使用了私有运行时。宿主仍安装了.NET，因此不能把该探针当成干净Windows安装验收；后者仍须按T-039B执行。

本目录先保存源码，只有实际运行后的日志/result记录能够证明结果。尾部格式仅为探针数据，不要求生产沿用。

## 2026-10-13实际结果

`sandbox-v2/output/result.json`：干净Windows11企业版10.0.26100，网络禁用，只映射本次三个exe/脚本（只读）与空输出目录（可写）。运行前后共享.NET列表均为空；三个exe实际进入.NET10.0.12入口，RuntimeDirectory为来宾输入目录。正常载荷exit0/Verified，损坏exit2/HashOrContentMismatch，越界exit2/InvalidBounds，全部通过。控制器完整停止自有沙箱，list-after为空。未运行Iris或真实安装器。

`v2/result.json`另有宿主三个载荷行为通过；发布只有单文件exe与不参与运行的pdb。二进制和输入SHA、只对子进程设置的DOTNET_ROOT/bundle目录及实际命令均留证；二进制仅在自有Temp/artifacts。

首轮失败不覆盖：原宿主result.json的三个用例因错误要求“必须能枚举到独立coreclr.dll”而失败，虽然载荷行为符合预期；单文件中该模块未被单独列出。修订后明确运行时归属留给干净来宾验证，没有把null改称已经观察到私有DLL。第一轮来宾sandbox/output/result.json已执行载荷，但PowerShell Start-Process/WaitForExit对象的ExitCode均为null，故整轮失败；改为直接持有System.Diagnostics.Process及句柄、完整读取输出和退出码后，第二轮真实通过。v1脚本/源码和全部原始结果均保留。

结论仅为.NET10 self-contained单文件附加载荷的可行性、边界/哈希检查和缺少共享.NET10时能够执行；不替代Maintenance实际实现、桌面运行时自动安装、交互UI、主应用运行或卸载验证。
