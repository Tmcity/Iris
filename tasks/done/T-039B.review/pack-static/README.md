# Windows PowerShell 5.1 静态解析

`../pack-powershell51-parse-2.log`实际使用Windows PowerShell 5.1.26100.9549 / Desktop解析build/pack.ps1，ParserErrors为空，退出0，脚本SHA见原日志。只解析语法，**没有执行打包脚本**，不能据此替代PowerShell 5.1完整打包的执行证据。

初轮包装脚本在Get-FileHash处报未识别命令，退出1，原日志及parse-powershell51.initial.ps1.txt保留。第二轮在自己的PowerShell进程显式导入系统Microsoft.PowerShell.Utility模块后成功；没有改宿主配置或生产打包代码，也不将包装器错误归为产品打包失败。
