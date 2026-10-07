# T-036B-R1 evidence

最终：release-final.log、full-final.log、targeted-2.log、dispose-race-final.log、transport-regression.log。完整报告../T-036B-R1.md。

前置失败：dispose-race-before.log（原基线）；stop-first-before.log（第一次修正遗漏相反启动顺序的真实失败）。release.log/full.log、targeted-1.log、dispose-race-after.log仅属于中间实现，均保留。

原独立源码及断言位于相邻T-036B.review/transport-probe，未修改；执行用自有临时副本，三个源码文件哈希前后相同。baseline-probe-binding.json记录原基线，fixed-probe-binding.json记录中间修正，final-verification.json记录最终执行DLL与源码。

protected-source.json、received-review.json和final-verification.json记录范围保护与架构师原件；remaining-test-processes.json记录最终进程收尾。
