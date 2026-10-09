# T-038 独立窗口与主实例语言探针

使用已复制并固定SHA的实际程序集，在自有WPF进程和临时JSON上验证语言联合保存、当前运行不切换、重新加载配置、冲突/Reload、外部合并、Core非法值拒绝、实际预设菜单与添加按钮可命中，以及真实Program/DesktopStartupRuntime生产作用域覆盖Host、日志器、Mutex清理失败。清理失败探针没有额外测试语言作用域，并检查生产调用返回后恢复环境中文。

`runtime-final-candidate-3.log` 记录完整通过及STA线程退出，候选依赖与源绑定见同级 `runtime-final-candidate-result.json` 和本目录 `dependency-inputs.json`。此处不包含DLL；实际固定依赖在临时 `iris-t038-independent/runtime-probe-final/deps`，最终交付还需重新绑定。

前两次所有业务断言通过，但自有STA退出超时，未计为验证通过。原日志为 `runtime-final-candidate-1.log`、`runtime-final-candidate-2.log`，原夹具分别保留在 `runtime-final-before-source`、`runtime-final-timeout-2-source`。排除菜单的诊断运行全部通过且正常退出，见 `runtime-diagnostic-without-menu.log`。最终夹具明确等待自有弹出菜单开关动画完成后才关闭Owner，并显式关闭自有Application，随后完整运行正常退出；产品源码未因此修改。等待只针对WPF显示动画，不用于模拟业务操作完成。
