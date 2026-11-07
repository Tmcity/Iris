# T-040同一实例的底层鼠标注册状态

在6040实例上通过已安装CLI启用ShowIcon后，自有ClrMD只读探针读取到MouseSelectionMonitor：Running=true、RunPresent=true、hook句柄1442753、Unhooked=false、owner线程5356。随后用同一CLI恢复Off，读取Running=false、RunPresent=false、句柄0、线程0。

源代码NativeMouseAdapter.Install将WH_MOUSE_LL的SetHook返回值保存到该Handle，非零句柄与运行态/线程身份一致。此证据闭合底层注册与恢复状态，不能代替物理鼠标手势交互。探针未暂停目标、未请求GC、未修改工作集，仅读取代码定义的状态字段，没有导出用户文本或凭据。

原始JSON和字节/SHA256绑定见binding.json；上一轮30秒CPU峰值0%及模式恢复记录保持，物理手势、热键录入与其余完整验收未完成。
