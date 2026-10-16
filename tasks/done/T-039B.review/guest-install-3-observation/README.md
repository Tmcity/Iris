# 实际安装来宾的独立观察

2026-10-15。架构师只读核对开发自有Sandbox的原始输出，并实际查看installed-settings.png；没有另行启动安装器/运行时/原生卸载器或并行测试。输入安装器、报告、实际查看截图的SHA绑定见binding.json。原始失败及当前开发源码保持。

这份固定来宾记录证明：安装前共享.NET运行时列表为空；Iris外层安装入口退出0后，共享Microsoft.NETCore.App和Microsoft.WindowsDesktop.App均为10.0.13；已安装元数据为Iris/0.2.0/win/win-x64/Tmcity/Iris，卸载注册指向Maintenance，安装没有自行启动主应用。

直接运行发布的UiaHost并关闭其stdin后exit0，只证明这一启动/EOF路径，不冒称完成真实UIA查询。随后实际Iris设置窗口出现（非零句柄），截图已独立查看：英文常规页显示0.2.0、未勾选启动检查和“Updates have not been checked”；这张截图不覆盖所有按钮、状态、语言和主题。

**整个场景失败**：真实报告Failure为`Uninstall did not remove the installed app.`。卸载注册入口exit0不等于外部维护拥有者完成，不据前面安装阶段成功结案B。该轮脚本在R4判定修正前派发，修正后的断言和完整维护进程观察仍需在新的实际轮次验证。环境为Windows 11 Enterprise 10.0.26100、来宾WDAGUtilityAccount，Administrator=true；不冒称验证了非管理员运行时安装。
