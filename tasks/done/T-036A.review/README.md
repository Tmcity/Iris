# T-036A独立方案验收


## 架构师独立复审（2026-10-07）

提交`97b8d02f8d9316632dc21ad6f78e3d3f15691dae`通过T-036A方案验证验收。受保护src/tests/CONTRACTS文件与基线逐字节一致，见T-036A.review/delivery-check.json；生产未实现新网络功能，未重复运行未改生产全套测试。

独立复制原型运行全部current/runtime/actual-coordinator/persistence/migration/pipelines/connectivity/streaming模式正常退出（all-1.log）。归档新增MigrationChecks条件删除实际导入旧键后，已同步独立副本并定向运行migration-final.log；其余模块不变，最终源码匹配。流式原反例已保留，修正后的获取流零预读、EOF前实际SSE事件、两命名重试链/取消/读错/响应处置完整验证通过。当前实际类型的运行时凭据缓存反例亦独立确认，未使用socket或真实用户内容。

拟冻结声明与现有完整CONTRACTS在临时项目独立build零警告/错误，原始输出见proposed-contract-build.log。A推荐方案经NETWORK_CONFIGURATION.md第11节明确生产期必须实现的容量、迟到代次、重试中间响应所有权、内部共享和future只读规则后冻结为CCR-036；不能把简化原型当生产验收。

A结案，父T-036继续进行；下一唯一T-036B真实配置/迁移/传输底座，C保留完整设置页与连通性测试。真实代理/socket、完整TLS/SOCKS/Windows认证、DPAPI及实机NFR仍未验证，按后续工单边界执行。不push。

原型源码使用已归档T-036A.evidence/prototype-final；delivery-check记录与独立副本的逐文件一致性。all-1对应迁移条件清理修正前，migration-final补测唯一变化。原始反例/对照源码和失败日志在A.evidence中完整保留。
