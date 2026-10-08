

## 开发期独立复核：取消回调失败不能永久阻止清理重试

2026-10-08，属于C单次会话取消/真实清理失败重试范围。实际ProxyConnectivityTester/Session，窄Source在传入token上注册一次抛错回调，返回有效direct快照；窄HTTP handler正常返回HEAD 200。首次Dispose的CancelAsync报告回调失败后，即使取消注册已经移除，第二次Dispose仍await同一个faulted `_cancelWork`，handler与session一直保留，无法通过原入口恢复。

实际输出：

```text
CANCEL_CALLBACK firstFailed=True; retryFailed=True; heldSessions=1; handlerDisposed=False; sends=1
```

请区分“取消回调已完成但报告失败”和“回调仍在执行”：首次失败保留可观察结果，之后的原session Dispose可继续剩余资源清理，不能永久重放同一faulted Task；不能通过吞掉首次错误或再Run/HEAD解决。覆盖调用方取消、主动Dispose/全局Stop以及一次失败后的原实例重试，仍须等待真正非合作工作退出。

独立目录C:/Users/28910/AppData/Local/Temp/iris-t036c-independent-tester，Program.cs，运行`--cancel-callback`，cancel-callback-1.log和binding.json。创建无副作用、HTTP 200/401/407/302分类、无Origin认证、Run与Dispose分离四组已通过。

诊断最终仅为安全释放夹具，在记录第二次真实失败后反射将已经观察完的faulted取消Task替换为CompletedTask，再经实际Dispose关闭资源；日志明确标FIXTURE ONLY，绝不算产品恢复通过。没有修改生产文件/真实用户配置或发出socket请求。修复后必须无需反射即可原入口成功清理。
