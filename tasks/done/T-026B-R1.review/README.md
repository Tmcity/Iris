# T-026B-R1 独立复审（2026-10-06）

绑定提交7e6687d0a17206edb613354a0022cb1618a18686。独立Release构建0警告/0错误（13.02秒），全量2634/2632成功/2既有跳过/0失败（44秒225毫秒）。原34项探针全通过，扩展建立期同伴Window.Close后35项34通过/1超时，STA退出。仅签发T-026B-R2处理建立期关闭；原六组和C26B-1成果保留。

权威最终探针输出review-probe-corrected.log；review-probe.log是首次扩展探针finally取消覆盖主异常的中间结果，review-probe-final.log是探针枚举命名空间错误导致的编译失败。修正仅使探针完整记录主/清理异常、正确声明失败清理的caller取消并引用真实Presentation枚举，不修改产品。最终输出证明三窗中第二窗真实Closed/Handle归零后Task等待超时，caller取消后allClosed=True、cleanupErrors=0。

prepare.py适配R1仓库已提供的外部钩子：允许精确已有钩子、确认实际Foreground回调并另加AfterActivate。产品主体仍从实际最终DLL引用；Geometry/Lifecycle/Visual原34项断言保留，新StartupCloseChecks仅操纵fake Show接受点内的真实自有Window.Close。不读取用户内容，不发真实输入。

已实际查看22张standard图的6张汇总及长屏原尺寸裁剪、6张R1图（其中两张before与对应selection字节相同）。Pillow重构汇总/裁剪逐像素一致；viewed-render-binding.json记录28张原图散列，最终提交复核无变化。独立visuals沿用实际产品重新渲染；真实桌面、混合DPI/HDR/焦点/NFR未验证。

原交付source 8份、testSource 7份、evidence 97份散列均匹配，见delivery-binding-check.json。独立编译探针有未赋值fake字段CS0649警告，不能称零警告；全解决方案构建零警告。
