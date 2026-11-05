# 原最终 0.2.8 CI 与 0.2.9 修正范围

用户仅授权最终 0.2.8。其正常推送标签保持指向 0c9b0a1；run 42015639513 attempt 1 的建测失败、publish skipped 保持，未有正式安装资产。原完整全量 3771/3769成功/2失败/0跳过；两项失败均在 Host.Dispose→SelectionIconController.DisposeAsync→UI 清理阶段，被包装为 HookInstallFailed。控制器并未实际启动鼠标监听。

原程序集 WPF 夹具初始化仅 CompletedTask，测试依赖其他并行类懒启动自有 STA/WPF。独立只运行 Host_ResolvesRequiredServices 复现原相同失败。测试程序集夹具现在在任何测试之前等待自有 WpfTestRunner 初始化；仍在程序集 teardown 中关闭并 join 同一线程。修后同一单项和整个 host 类 13 项通过；所有 composition 断言和生产代码保持。

第一本地 source f2526ab 的全量记录保持为 3771/3768成功/1失败/2既有跳过；唯一新增失败在 ThemeServicePreviewTests.VisualThemeApplier_IsInvoked_WhenThemeChanges。该测试从后台调用真实主题服务，等待 Application.Current 的共用 dispatcher 回调；其他类的同步 UI 夹具/渲染工作同轮占用了该队列。临时受控诊断显式占住同一线程、调用原测试，实际复现 line141 的 TimeoutException，finally 放行并等待自身 blocker 和剩余队列完成。原诊断源码/失败和两个编译校正记录保留，临时诊断文件已从最终源移除。

主题组 8 项现在使用独立集合安排执行，其后台调用、五秒超时、主题/owner/警告脱敏断言原始方法体逐字节保持。其余测试集合仍按原并行安排执行，完整测试不筛选、不转 skip。最终 0b3267f 的完整全量 3771/3769成功/0失败/2原 error183 环境跳过；原报告时间区间确认主题组不与其他 App 测试重叠，App879项全部通过。

最终仅两个测试文件、发布计划和说明变化；src/workflow/依赖/schema保持最终028的限流发现与维护工作目录修正。最终0.2.9候选及来源/字节独立核对见 next-source-v029-final-20261105-1；所有原失败证据绑定保持。原 CI ZIP 与官网完整 digest 已核对，匿名 REST 补验仍403，其未验证状态明确保留。只有本地全量/候选通过，0.2.9和下一版本尚未获得发布授权。
