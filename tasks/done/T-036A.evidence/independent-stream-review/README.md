

## 开发中独立补充：响应流不可被包装层隐式缓冲

2026-10-07，属于本单运行时/生命周期验证范围，不扩生产实施范围。

独立复制当前PipelineChecks.cs等原型源码（未改CaptureHandler.LeaseContent），用首字节可用但EOF受控的StreamContent实例化原LeaseContent，再调用ReadAsStreamAsync。实际输出：

```text
Prototype response wrapper: ReadAsStreamCompletedBeforeEof=False; UnderlyingReadReachedEofGate=True; BuffersUntilEof=True
CLEANUP leaseReturned=True
```

原因：只覆盖SerializeToStreamAsync的HttpContent包装器，其默认ReadAsStreamAsync路径会缓存整个正文。当前空响应原型不能证明流式兼容；不得将此包装器原样落地为LLM/SSE网络层。

请在A内补充流转发原型和精确方案：适当覆盖CreateContentReadStreamAsync（及调用路径需要的同步/取消重载）或等价无缓冲转发；ReadAsStreamAsync不得为获取流先读完正文，首个SSE/首字节在EOF前可读取。确保转发Stream/响应Content处置、取消、读取错误仍正确归还代次责任，旧池不能在流消费前释放，重试上下文保持。验证非零持续正文，保留修正前反例与完整清理；无需改生产src或公开契约。

独立复现目录：C:/Users/28910/AppData/Local/Temp/iris-t036-stream-review，Program.cs及stream-2.log；仅复制原型测试，未网络/socket。stream-1为独立裁剪夹具的未赋值字段警告（未影响被测包装逻辑），后续只在复制项目抑制CS0649，未改复制业务源码。请把最终方案明确写入此限制和验证结果。

独立对照追加：stream-3.log在保留原缓冲反例的同时验证ForwardingComparison通过覆写CreateContentReadStreamAsync及取消/同步对应入口，获取流不读取正文，EOF前首字节可用，响应仍持有lease并在Dispose归还。这只是最小语义对照，不是完整生产wrapper（未宣称headers、任意失败/并发处置全部覆盖）。
