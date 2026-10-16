# 真实来宾验证的结果判定

2026-10-15。仅审查当前guest_install.ps1/run_guest.py，尚未据此宣称实际Iris场景通过或失败。源码原件及SHA见binding.json。本条落实原B的真实隔离验收与如实报告要求，不增加产品范围。

1. **关键false值目前不会使验证失败。** `Record`只记录值，`install-did-not-start-app`、`silent-uninstall-kept-data`、`uninstall-key-removed`为false时仍可能得到Failure=null并exit0。等待设置窗口超时后MainWindowHandle仍可能为0，脚本也只记录，没有拒绝；runtime-retained同样仅记录。运行器最终只检查Failure。应保留每个实际观察值，同时将本轮声称验证的必要条件做明确断言，缺失/false/未完成均不能记为通过。截图仍须实际查看，不能替代窗口确实出现的检查。

2. **卸载入口退出不是外部维护拥有者完成。** 当前脚本等待注册入口返回0，再仅轮询App文件消失及原App进程退出；外部Maintenance和native updater可能仍在完成注册项/事务/收尾。随后运行器就停止Sandbox，可能在拥有者尚未结束时终止来宾。应明确观察这次自有外部维护进程及最终事务/文件/注册/数据状态，在必要终态前不记成功；如果夹具必须因超时结束自有Sandbox，保留unfinished进程和实际失败，不把夹具终止当作产品清理。保留本轮及所有旧结果，使用新label重跑修正后的脚本。

这些检查可以继续与当前安装验证合并，不要求等待架构师或暂停构建/测试。初轮只覆盖的场景要如实列明，仍须完成原B的保留/删除/取消、两种语言、运行时取消/失败、本地两版本升级及恢复等完整矩阵。
