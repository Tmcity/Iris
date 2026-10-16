# 首轮本地更新外围结果

2026-10-15独立只读核对。`artifacts/t039b/sandbox/2ed0bc7d-2497-4e88-9f4e-e000b9105fde/output/update-1/engine/result.json`记录真实引擎完成0.2.1、ParentExit=0、LastNativeExitCode=0，Failure=null且Cleanup为空。外围result.json却在exactly-one-restart=false处失败，整轮不能写为通过。

同目录process-diagnostic.json曾实际记录新Iris.App PID3088（父PID2756、标准安装路径、无启动参数）。开发上一轮随后发送了真实`Iris.App.exe --action Exit`作诊断，再取得外围失败。因此“外围观察时没有进程”不能直接推导为“产品从未重启”。必须区分原始重启、诊断关闭和最终正常流程验证。

待验证的夹具线索：guest_common.ps1的Run-Child在子进程已经退出后，还同步等待重定向stdout/stderr的ReadToEndAsync完成；GuestUpdateHost又通过产品StartApplication启动长期存活的新App。需检查新进程是否继承这些写句柄，造成外围实际等待后代关闭输出而非等待更新拥有者退出。本记录未独立执行该句柄假设，不把它写成已证明根因；任何诊断干预与流收尾结果都单列，修正后用新标签跑完整路径。
