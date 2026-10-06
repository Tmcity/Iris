# T-030A独立验收准备（尚未执行产品检查）

基线T-029 6010f23a已通过；A签发后本目录仅准备，不改产品/测试。
正式实现后从Core/App.Tests输出引用实际DLL，以现有友元或公共DI解析实际Registry，禁止拷贝其状态机。

优先确定性检查：
1. Settings.Current/Secret.Get/Factory.Create计数证明Registry构造不读；重复AddIrisCore的OCR端口/观察者唯一。
2. fake Factory持线程门闩；Current从A切B且密钥集切代，旧构造释放后仅B发布，旧候选DisposeAsync可门闩/失败。另验证共享Refresh取消一个等候者，另一继续，未发放的Acquire无泄漏。
3. 老snapshot与新snapshot之间修改Active/Fallback/LanguageHint/TextLayout；快照配置与注册项各自同代，列表重排只改变顺序不换InstanceId；无关General更新同样复用。
4. 两份老租约跨重建，第一释放不处置，最后释放等待实际异步Dispose；同时Registry Retry/Stop只一个原实例处置。故障时保留对象，后续成功重试同对象，成功后GC；正对照故意保留一个Provider。
5. Transient Enabled=false仍创建，Create过程中取消/Stop后的候选完整收尾；不修改Current/Secrets，不触发Changed。
6. Refresh已发布新代后退休失败：当前新代保留、错误可见、旧对象可重试；其它退休对象仍处理。安全日志/异常中不含假凭据及原始Factory/Dispose文本。
7. StopAccepting不阻塞/不释放持租约实例；StopAsync取消等待者不取消实际stop；释放旧快照后stop完成。失败stop可重试，不以Task已faulted永久锁死。
8. 实际SettingsEditCoordinator屏障：触发观察者失败与RetryApply，设置已提交且旧引用保留；不实现OCR UI。实际Host/AppBootstrapper已有早期清理错误和持租约所有者分别测试，Host不得提前Dispose。
9. 实际Baidu/WindowsProvider通过Registry拥有，fakeHTTP/OCR为外边界；不能只测空IDisposable。Windows位图可复用T-029合成图边界。

所有scope异常路径先放行每个门闩，再观察在途任务、释放实际租约、Stop重试与释放fake资源。以状态/TCS为准，不通过任意delay推断到达。最终交付后独立Release/full及单独探针，各保留原始日志/退出码/来源指纹；当前尚无任何通过结论。

Boundaries.cs已准备受控Settings/SecretStore/Factory及优先异步释放的Provider：构造/读取可门闩，清理可挂起/失败并观察原对象次数/并发；Factory.ForgetCreated可去掉测试自身的GC根。Preparation第二次编译0警告/错误，第一次仅未赋值测试hook字段警告，原日志保留。

RegistryChecks.cs已按CCR-030写13项检查：懒构造、旧/新代一致、两租约释放与真实async等待、释放属性拒绝、同实例失败重试/安全异常、停止封口/等待者取消/最后租约。ContractPreparation.csproj只引用独立编译的CONTRACTS声明，编译0警告/错误；**未绑定/运行产品**。开发交付后Create委托须接实际Registry/DI，不用声明工程冒称验收。其余条件发布/凭据/字段/GC/实际Provider/Coordinator/Host检查仍需补齐。

PublicationChecks.cs补构造中配置失效+取消一个共享等待者、Stop中瞬态候选收尾、八组legacy/完整set/显式None/坏引用/缺失/重复字段/版本/缺字段。通用FinishAsync先放行全部门闩再逐一收尾，失败仍尝试其它租约/Retry/Stop并汇总。preparation-contract-build-3.log对全部当前草稿/声明编译0警告/错误；还未运行实际产品。B后续准备另在iris-t030b-preparation.md，不影响A范围。

FingerprintChecks.cs补列表顺序/请求设置复用、十种Profile字段修改、凭据变化及未引用密钥对照。preparation-contract-build-4.log对这些草稿编译0警告/错误。Program.cs/prepare.py已准备通过真实AddIrisCore解析Registry并绑定最终Core.Tests输出DLL，尚未编译或执行这一实际DI程序；待产品接口落地后进行。程序当前只覆盖Core Registry，不能以其替代实际Provider/Coordinator/Host专项验收。

OwnershipChecks.cs补存活Registry下退休Provider弱引用（去除Factory测试根并保留活跃Provider正对照）和两个清理故障的并发开始/错误可见/同原实例重试。preparation-contract-build-5.log对全部草稿0警告/错误。Program/prepare已纳入，但实际DI版本仍未执行；需要最终DLL后验证是否存在harness根或产品保留，不能先判GC缺陷。

开发期预检更新：开发development-build-2全解决方案已通过后，prepare.py绑定当前Core.Tests产物，执行实际Registry/DI程序。第一次development-probe.log在Coordinator组失败：独立草稿的Delete计划缺必填TranslationOrder，产品正确返回Uncommitted，因此没有pending receipt；非产品缺陷。补齐BaseIds/空TargetIds后development-probe-2.log退出0，51项全部通过，包含新增实际Coordinator屏障/旧凭据保留/Retry不重提交与唯一OCR观察者。development-probe-2-manifest.json保留本次代码/实际复制DLL指纹；尚非最终交付验收，交付后需独立Release/full及重绑。实际Provider和App/Host组合检查仍须补齐。上文“未运行”是准备阶段历史。

AppChecks/AppProgram/prepare_app.py已接actual AppBootstrapper+Core Registry，外部窗口owner、hotkey capture及IHost为受控替身，没有创建实际窗口。development-app-probe.log 13项全通过/退出0：正常等租约、owner失败不死等/重试成功、早期capture故障仍排空OCR但不执行Host终结、恢复后完整退出。app-manifest.json绑定本次App.Tests产物。此结果不代替真实Generic Host DI disposal/实际Provider资源及最终交付验证；后续统一重绑，勿混用旧DLL。

扩展实际资源组合：WindowsChecks使用实际Factory/Provider/WindowsOcrImage，只替换OCR引擎与故障边界，800×180合成PNG来自T-029独立fixture；BaiduRegistryChecks通过真实AddIrisProviders/Factory/Provider，HTTP为本地受控handler/原stream失败，无网络。HostChecks使用真实Generic Host（DisableDefaults），在Registry排空后同步处置，未Start任何桌面服务。development-app-probe-2仅harness缺System.IO编译失败；补齐后-3为19项全过；加入百度及Host后-4为24项全过/退出0。正式交付仍须重绑并运行最终Core51+App24；不再为无变化情况重复开发期预检。
