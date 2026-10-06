# T-029 独立验收准备

当前T-029开发中，尚未交付。已准备16组固定Unicode输入/预期：汉字、补充平面、假名、韩文/Jamo保留、双空格/tab/NBSP/首尾/中西混合/emoji及项目既定全角范围。

fixtures/hello-123.png为Pillow用系统Arial字体生成的800×180白底黑字，内容只有HELLO 123；fixture-manifest.json记录来源和散列。不是用户截图，没有执行OCR。正式工单允许只读能力/语言查询及真实SDK合成图片smoke；实际执行仍需按最终产物和环境如实记录，缺语言包不安装、不改包身份。

待实际外部接口落地后，准备actual Provider/Factory + fake OCR引擎、真实WinRT位图的语言优先级、Bounds、取消/排队/原资源重试/GC检查。正式交付后独立Release/full并绑定源码/DLL；不把样本准备当作运行通过。

后续SDK前置事实：相邻iris-t029-sdk-smoke的独立程序已用真实SDK处理本合成图，退出0，MaxImageDimension10000，已安装en-US/zh-Hans-CN，选en-US，结果HELLO 123且匹配。仍未执行产品Provider。该原始日志/源码应在最终报告明确区分API环境预检与实际产品验收，不因本机成功取消其它环境的诚实跳过声明。

OcrBoundary.cs已按开发当前外部接口准备：只替换能力/语言/engine输出，图像owner内部仍调用实际WindowsOcrImage生成SoftwareBitmap。可门闩控制原生识别并观察释放与并发；没有复制Provider选择/转换/收尾状态机。尚未编译或运行，需按最终接口接实际Factory/Provider。

ProviderScope/BehaviorChecks已接当前实际Factory签名，准备能力懒创建、16组实际Provider rune输出、分数词框向外取整、语言优先级/缺包、Test不识别和长补充平面文字的批次取消检查。仍未编译/执行。后者针对可能跳过固定UTF-16下标检查点的边界，仅为检查线索，不能据此先判最终代码缺陷。

LifecycleChecks补充识别在途caller/Stop保持真实SoftwareBitmap可用、原bitmap+stream与主错误组合后同owner重试、真实WaiterCount达到后的排队取消/串行引擎使用。preparation-build.log已将当前全部草稿编译通过0警告/0错误，未运行Provider检查，正式交付仍需重绑最终DLL。

注意相邻iris-t029-sdk-smoke目录内：result.log/Program.cs是架构师早先直接SDK预检；开发随后在同目录写result.json，是其actual WindowsOcrProvider处理640×160合成图的测试结果。两者不是同一次运行，不覆盖/混称。开发sdk-smoke-first.txt已通过1项；正式独立Provider验收尚未执行。

ResultChecks补能力上限/无效能力、native null/NaN/负值/溢出/空结果和同一存活Provider的输入/结果/engine/SoftwareBitmap弱引用检查。preparation-build-2.log为当前全部草稿0警告/0错误；仍未执行。GC前会移除fake native结果引用，且不调用Stop/Retry/下一请求，避免把测试支撑引用误判为产品缓存。

Program/prepare.py已串起全部检查（含active+queued总预算、实际Platform DI和实际Provider的800×180合成SDK路径）。preparation-build-3.log因准备工程没有完整Logging包却调用AddLogging而失败；该DI检查仅解析本地Factory及已替换边界，无需日志依赖，移除后preparation-build-4.log零警告/错误。尚未运行完整程序。正式交付后先独立Release/full，再prepare.py重绑最终Platform.Tests输出DLL，dotnet run --project Review.csproj -c Release另存日志。
