# T-028B架构师独立验收（2026-10-06）

最终提交fe05ccbf84913b8e23928031ed24cecf5b43f924。独立Release0警告/0错误（27.11秒），全量2893项2891成功/2既有跳过/0失败（1分25秒191毫秒）。独立实际Provider/Factory/HTTP命名管道+Mock边界65项全部通过，所有检查均由实际产品DLL执行，未复制产品状态机。

四模式/网关/凭据转义/表单及元数据预算、段落/位置/置信度/无配置/空结果；普通并发、短TTL53/54秒、Test真实鉴权与并发新receipt、获取者/等待者取消、同字符串新receipt的迟到旧错误；业务/HTTP表、畸形响应、499/500ms与混合预算；HTTP200/401原stream失败重试、20秒启动前耗时后的总期限、timer原对象重试、同步Dispose拒绝、body读取取消、存活实例输入/结果GC（全部False且对照False）均通过。

实际iris.ocr命名管道在HTTP503响应和抛传输异常两种情形都到达9次识别传输/2次Token，分别捕获284/278条格式化/结构化/scope日志记录，没有测试敏感标记；DTO ToString检查通过。只将最终primary handler替换为Mock并将测试传输延迟置0，生产retry/timeout策略未改。

17份交付源码/23份证据指纹一致，Core公开OCR声明不变，DI剔除批准Factory/安全handler增量后与基线一致。App/Platform/Core.Text/通用Http/Llm/包/default/schema零差异。开发阶段曾见的stream/timer/log_id/日志/总预算问题已按最终版本验证，不能据旧草稿返工；HTTP401流重试等原先检查线索也已实际通过。

preparation系列日志仅为提前编译，未执行；正式依据为review-build/full/probe.log和最终manifest。T-028/B及CCR-028验收结案，A保持结案。真实百度API上传/语言/方向/坐标兼容性与NFR未验证，无真实网络/用户图像/凭据，既有只读native smoke和error183跳过保留。未push。
