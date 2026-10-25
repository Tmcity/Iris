架构师补充：用户刚确认“已经启用Windows Sandbox”。我已独立核对：Feature InstallState=1、WindowsSandbox.exe及wsb.exe均存在，Sandbox Appx 0.8.107.0；实际 wsb list --raw exit0，列表为空。原文在 acceptance-review-4/host-isolation-enabled-1.json、sandbox-list-enabled-1.log/.command.json。宿主功能由用户启用，架构师未运行启用脚本。环境缺口已解除，current_task已追加最新恢复段，旧阻塞快照继续保留。

请接续同一 T-039B-R4，不重开聊天、不改模型、不重做已通过本地验证。先实际核对当前 HEAD、active_round.json 和 candidate-3/final-8 固定源/输入字节；如与冻结源有差异先反馈，不自行借用旧产物。最新源码树 d7ca8024ba8a7feea9fa75c61d7785b1edd77b5a，独立15/15、1258字节核对和本地化审计已通过。

继续唯一尚欠的真实来宾：新标签 guest-install-r4-2（若已存在改为另一个唯一新标签，不覆盖）验证干净Windows无.NET10时安装、真实Iris/UiaHost、运行中静默保留卸载及全部真正进程终态；结束自有实例后再用新标签 guest-update-r4-1 完成本地0.2.0→0.2.1 full应用、原进程真实退出屏障、单次重启、数据可读和work目录清理。runner按active_round.json使用final-8，只映射本单专用目录，不在宿主执行Iris安装/卸载，不处理别人的实例。Microsoft运行时下载允许。

下载或观察超时不能证明实际安装进程结束，须检查同一真实句柄/环境并保留原失败，不重复启动安装器。验证与截图/输入指纹/退出码如实归档；失败在原单定位修正，若改产品须重新固定建测与唯一新包，不能拿旧包证明新源码。

完成两项来宾后，inspect_delivery.py --guests、实际逐图查看、按观察/布尔断言/退出码区分回报，更新报告/交付绑定/最新工单原文（不覆盖旧快照）。全部必需项完成后才核对移除current_task并创建独立本地提交；不要批量git add整个工作区，不改BACKLOG/.codex，不push/reset/clean/rebase/amend。架构师无建测/来宾任务运行，窗口交给你，最终交付后我独立复审。父T-039/B仍未结案，C/T-040未签发，M6延期。

请简短确认接续的实际基线/环境与新来宾标签，然后继续，不再等待用户代传或重复询问已解决的环境。
