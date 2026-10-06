# T-030B独立验收准备

当前B实施中，A已验收。本目录仅准备/后续独立验证，未运行产品。

make_vectors.py生成固定Normalizer期望，不从实现计算：Keep原空白/内部换行、CJK补充平面/韩文/混合、连字符/标点/列表、空行、严格行距阈值/全组平均高度/重叠/缺失/非法Bounds/安全算术。

后续接actual Service/Normalizer/Registry，fake图像预处理与Provider边界，遍历ErrorCode验证准确回退集合；主备不同Constraints必须各收原图，配置变化仍同快照；取消/停止须等待真实任务，失败快照重试同对象，GC有正对照。App检查OCR服务→预处理器→Registry→Host，保留A截图准备依赖保护。不复制Service算法或用草稿编译代替产品验证。

Boundaries.cs与BehaviorChecks.cs已准备：外部Registry/Snapshot/Provider/Preprocessor替身；实际Service构造及Normalizer通过待接的委托。ErrorTable遍历每个ErrorCode在constraints/prepare/recognize三阶段，明确五个可回退码；另断言原图、约束、LanguageHint和Raw。Normalizer读取27固定向量。ContractPreparation.csproj只引用独立CONTRACTS声明，preparation-build.log 0警告/错误；尚未接实际产品DLL或运行检查。后续需补取消/原快照重试/实际Registry/App等检查后统一最终验证。

LifecycleChecks已补实际阶段门闩下caller取消/Stop、停止等待者取消、主+快照释放故障安全错误、Retry与Stop共用原快照及抛错取消回调。preparation-build-2.log声明编译0警告/错误。随后Program/prepare.py引用开发当前Core.Tests产物，development-probe.log真实Service/Normalizer137项全部通过（退出0）。这是开发期预检，非最终交付；最终要独立Release/full后重绑。实际Registry/配置切换、App依赖退出、GC与额外选择边界仍需补齐。上方尚未运行系早期准备状态。

RegistryIntegrationChecks新增actual OcrProviderRegistry+Service同代回退和下一次新代；外部存储引用已归档A.review/Boundaries.cs，不复制产品算法。OwnershipChecks不用NSubstitute，清除测试自己的请求/快照队列后检查input/bytes/outcome/raw/snapshot和控制对象。development-probe-2.log 140项全通过，GC_REFERENCE_FLAGS全False，Service正对照存活。此为当前开发DLL预检；App退出/其它选择边界待补，最终仍重绑执行。

AppChecks/AppProgram/prepare_app.py已用actual DI OcrService/Normalizer、AppBootstrapper和WinRtImagePreprocessor，Registry/Provider/Host为受控边界，自有800×180 PNG沿用A.review。preparation-app-build.log零警告/错误，development-app-probe.log 8项全过/退出0：held OCR期间预处理仍能完成实际解码；快照失败不早停依赖/Host，重试后顺序正确且无识别重放。App输出目录有独立manifest，最后验收需统一重绑并重复正式运行。其它Provider选择/空结果边界待补。

SelectionChecks补12个选择边界：主未配置、未知/大小写ID、显式备用、空/空白结果、主备均缺失、备用未配置、同Profile/Instance/Provider引用、无备用。development-probe-3.log Core152项全通过（含原140），App8保留。正式交付后分别prepare.py/prepare_app.py重绑最终DLL，运行Review.csproj/AppReview.csproj并另存review日志；不再无故重复开发期检查。
