# T-038-R1 独立验收结案

2026-10-10。验收提交 `30177c590d8e05e2161b6e99d3fe6385d68e73fb`，父功能提交 `9c1ac9d0a044e2f844e82957c841b785da9d88e5`。

父单独立全量在新增外部语言合并用例失败：JSON已更新为en-US，单次ApplicationIdle返回时VM仍为zh-CN。实际夹具的Dispatcher分两段投递；Idle不能代表Changed发起的业务回调完成。R1以受控门闩重现这一顺序，保留原en-US、脏草稿、冲突、Reload、取消与固定运行语言断言；等待实际语言通知，并完整汇合其触发的Dispatcher任务。生产源码没有改动。

独立检查 `delivery-binding-check.json` 确认原断言保留，修改仅在 `LocalizationTests.cs` 与 `NetworkSettingsUiScope.cs`，源码及交付证据匹配提交。修正前确定性失败与首次CS4014构建失败保存在 `../T-038-R1.evidence`，父单首次独立全量失败保存在 `../T-038.review`。

提交后独立Release、定向回归、全量测试均退出0。定向原始摘要：

```text
测试运行摘要: 已通过!
  总计: 1
  失败: 0
  成功: 1
  已跳过: 0
  持续时间: 3s 248ms
```

完整原始结果在本目录 `final-release-1.log`、`regression-1.log`、`final-full-1.log` 及对应exit文件。`final-verification.json`绑定结果、生产零差异、最终实际图像与重新构建的程序集SHA。最终图像逐项匹配父单已实际查看版本；全部本单要求通过，父T-038与CCR-038一并结案。

真实桌面/系统对话框、多屏、联网和NFR不在本单实际验证范围。按用户要求，T-038/R1结案后停止推进，等待用户明确“继续”。
