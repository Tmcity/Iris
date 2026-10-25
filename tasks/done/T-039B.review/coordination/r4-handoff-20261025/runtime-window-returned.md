架构师补充：独立冻结候选运行已结束，现明确归还建测/打包窗口。原始 independent-runtime-candidate2-1.log 为总计9、成功9、失败0、跳过0、exit0；xUnit 报告和自有 worker 结果已核对，冻结输入321项保持不变。完整绑定见 acceptance-review-4/independent-runtime-candidate2-binding.json。

这独立确认了本轮真实 scoped/Dispatcher/helper 的准备、关停失败、恢复和撤销等受控边界，不等于真实安装/线上路径完成。新 final-7 另有独立1258项字节核对通过，修后8图已逐张视觉确认。

后续仍同一 R4，保留工单、旧候选和全部失败；源码树 c6fd6c81f2d6b553d6b8a30390794c2d6a00683d 若再改，须重新固定和验证。当前首要未完成项是新包真实来宾复验，Windows Sandbox 启用/替代 VM 仍等待用户明确回复，未执行宿主变更。我会继续环境协调，你无需重复向用户询问；先保持现有可审交付和可续跑说明，不提交或结案不完整工单。
