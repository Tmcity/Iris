# 屏幕捕获与内存裁剪规范（T-025 / ARCH v1.39）

2026-10-05，Codex架构师签发；T-024B2-R1已验收。目标为 Platform 抓屏、原始帧、异步裁剪编码及完整退出底座；T-026 才做截图覆盖层/隐藏恢复 Iris 窗口，T-027 做 OCR 前处理，T-031 做动作入口。

## 1. 契约及依赖

CCR-023已批准：保留 IScreenCapturer.CaptureVirtualScreenAsync(CancellationToken)，将尚无调用方的同步 Crop 改成：

```csharp
Task<ImageData> CropAsync(CapturedFrame frame, PixelRect region,
    ImageFormat format = ImageFormat.Png, CancellationToken cancellationToken = default);
```

CapturedFrame 原构造/字段/解构保持，注释明确 top-down BGRA32、Opaque alpha、物理坐标、独立输出、调用结束前输入数组不可变；ToString 改固定安全摘要，不打印数组/显示器设备名。未新增公共 lifecycle 接口；服务/外部 GDI 适配/worker/编码器边界 internal，使用现有 IVT。同步 specs/CONTRACTS.cs 与 Core 文件，其他契约/default/JSON/schema/包零改动。

单例 GdiScreenCapturer 在 AddIrisPlatformWindows 注册 IScreenCapturer，同一具体实例提供 App 退出。构造、DI Resolve、未使用的 Stop/Dispose 不创建线程/DC/bitmap、不开设备、不枚举桌面；独立 Crop 只用内存，不创建 GDI worker。若已有捕获失败留下的 worker/session，后续 Crop 也须先经它重试旧清理，这是清旧责任而非新截图。复用已验收 IDesktopContext.GetMonitors，不复制或重写显示器枚举和前台读取。

## 2. 明确的预算与数据规则

内部常量，无新用户配置：单边 <=32768；原始帧/裁剪像素 <=67,108,864（64Mi pixels）；单帧源 byte[]/有效 stride footprint <=268,435,456（256MiB）；编码结果 <=285,212,672（272MiB）。这是接受数据的预算，不宣称包含 WinRT 内部缓冲的进程内存硬上限。编码后先检查 stream.Size 再分配托管输出，不悄悄改变格式、质量或缩小图片；超出返回 ImageTooLarge。后续 OCR/clipboard 有独立更小预算，由后续功能处理，不宣称每张合法大帧一定可复制或提交 OCR。

全部坐标端点、union、intersection、stride、byteCount、偏移先用 long/checked 验证后转换；不调用可能 int 溢出的 PixelRect.Right/Bottom。宽高 >0、stride >= width*4、buffer 至少覆盖最后有效行，VirtualBounds.Width/Height 与帧一致。超预算 ImageTooLarge；结构/枚举非法 ImageInvalid；region 非正尺寸或交集为空 CaptureFailed。调用已取消优先 OperationCanceledException，不能做 native/分配大数组。

Capture 输出紧密 stride=width*4，Monitors 是本次的独立只读快照，VirtualBounds 是其 Bounds 的 union（允许负坐标、显示器间空洞/镜像重叠）。Desktop 已负责设备筛选；本层仍拒绝空快照、空条目、非正尺寸和不可表示几何，不悄悄漏掉某块显示器。DPI 为元数据，不缩放物理像素；空洞黑色且 alpha=255，各屏像素 alpha 同样规范为255。不额外绘制硬件 cursor。CapturedAt 是成功像素读取阶段的 UTC 时间；多块 BitBlt 不是严格同一瞬间的桌面事务，不声称原子桌面快照。

Crop 输出是 region 与 VirtualBounds 的半开交集；逐行按输入真实 Stride 拷贝为独立紧密 BGRA，保留行序/BGR顺序，alpha 按不透明屏幕像素处理。输入 buffer 与输出编码 bytes 不共享可写存储，服务不保留已成功返回的 Frame 作为隐藏缓存。调用方在真实任务完成前不修改 frame 像素；大复制/编码不能发生在 UI 线程。

## 3. 同线程 GDI 流程

一个按需创建的专用后台线程拥有 capture session；线程启动/退出本身纳入任务责任。允许此专用线程阻塞等工作信号，禁止在 UI 或 ThreadPool 用 Task.Result/Wait 假异步。

在这个线程中完成临时 SetThreadDpiAwarenessContext(PER_MONITOR_AWARE_V2)、GetMonitors、GetDC(NULL)、CreateCompatibleDC、CreateDIBSection（BI_RGB、32bpp、负 biHeight top-down、DIB_RGB_COLORS，hSection=NULL）、SelectObject、清零 DIB、每显示器 BitBlt(SRCCOPY|CAPTUREBLT)、GdiFlush、复制托管数组、所有 GDI 清理和 DPI 恢复；中间不 await 迁移线程。对每个 monitor.Bounds 按 union 左上角偏移写入 memory DC；所有目标像素初始化，禁止把未初始化 DIB bytes 返回。

拿到 bitmap/bits 后验证两者；只要 bitmap 非零即取得删除责任，即使 bits 为零。保存 SelectObject 的旧对象，识别 NULL/HGDI_ERROR。只有 GdiFlush 成功后直接访问 DIB bits；本方案不使用 GetDIBits，不能把仍选入 DC 的 bitmap 传给它。DIB bits 随 DeleteObject(bitmap) 释放，禁止 GlobalFree 或额外 FreeHGlobal。

采样前后各取同样规则的 monitor 快照；比较设备身份、Bounds/WorkArea/DPI/Primary（忽略枚举顺序）。不一致则丢弃此次数据并完整清理后最多重新 capture 一次；再次变化 CaptureFailed，不发布混合拓扑。任一次清理失败禁止自动开始下一 capture。显示器枚举/原生失败均转换安全 CaptureFailed；不读取前台标题、用户配置或输入。

## 4. 所有权、失败与重试

每个 session 精确记录 screenDC、memoryDC、bitmap、oldObject、selected 与 originalDpiContext 的实际状态。每阶段成功才撤销责任；返回 false/0 和抛错同样纳入清理。

正常顺序：恢复原 selected object，删除 bitmap，删除 memory DC，ReleaseDC(NULL,screenDC)，恢复原线程 DPI。SelectObject 恢复失败时不能删除仍选入 memory DC 的 bitmap；可先成功 DeleteDC，再 DeleteObject。某个删除失败不能阻止其它独立且安全的清理，但必须保留剩余句柄和同一个线程。GetDC/ReleaseDC 必须同线程；CreateCompatibleDC 用 DeleteDC，不能 ReleaseDC。释放的成功标记不可重复；失败重试继续原 session，不创建替代资源掩盖旧责任。

失败后尝试一次完整清理；未清完时任务以安全错误结束并保留 worker/session，不能让任务假装成功或将 worker 终止。下一有效请求准入后先在原线程重试旧清理，全部完成才开始新 native capture/encode；内部 RetryCleanupAsync、StopAsync 提供明确路径，禁止无限自动重试。最终 Stop 失败时 App 不进入 Host/Dispatcher 成功退出，原实例可再次 Stop；Stop 成功必须确认 actual worker thread 已退出，而非只看退出信号。

错误使用 ErrorMessages 默认文案与固定 stage、HRESULT、API 明确提供的数值错误码；不得采集/拼接像素、标题、窗口文本、设备名、原始 Exception.Message/Data/InnerException。只对文档明示 GetLastError 的 API 读取错误码，否则用固定阶段+返回值或安全 HRESULT，不报告陈旧 LastError。主失败与所有清理失败均保留；取消但清理失败时以 CaptureFailed 报告并保留 cancelled 标志；只有完整清理后的取消才抛 OCE。

## 5. 准入、取消及退出

服务整体最多一个真正接受的 Capture 或 Crop，锁内同步原子准入；忙时立即安全 CaptureFailed(stage=BUSY)，不排队、不取消已有任务、不留无限 Semaphore waiter。这适用于尚无应用消费者的本端口；T-026 的截图组已有 Dispatcher 互斥。锁内不调用外部代码/不发通知/不执行 caller cancellation callback。

调用 token 与 stop 标志在阶段前后检查，尤其复制/编码完成、资源清理和最终发布前复核；不使用 WaitAsync(callerToken) 提前完成真实 native 责任。已经执行的同步 GDI 必须等返回才能清理，不宣称能强制中断 BitBlt。StopAcceptance 同步永久关闭入口；StopAsync 共享在途停止任务，要求当前操作取消并完整等其 native/编码/清理结束，再尝试遗留清理与线程退出。已停止调用安全拒绝，不再创建资源。失败可同实例重试，成功幂等；同步 Dispose 不假装异步退出已完成。

AppBootstrapper 最前面关闭此服务准入，在 Host/Dispatcher 销毁前排空；不因一个截图清理错误跳过现有鼠标/选区的独立收尾，也不能吞错误继续销毁依赖。保留既有停止阶段、原有错误诊断与重试；此次只插入截图底座职责，不重写其余退出流程。

## 6. WinRT 编码

使用当前 Windows TFM 内建 Windows.Graphics.Imaging.BitmapEncoder/InMemoryRandomAccessStream，PNG/JPEG/BMP 全保留，不添加 WPF/System.Drawing/图像包。PNG/BMP 无损；JPEG 显式 ImageQuality=0.9f，允许有损。设置 Bgra8、Ignore alpha、物理像素宽高、96x96 DPI（不让屏幕 DPI 改尺寸）。编码对象在后台创建/执行，大帧拷贝/SetPixelData/Flush 不在 UI。

CreateAsync/FlushAsync/读取异步操作必须观察真正终结，才可以释放其输入 buffer 和 stream/reader；不得将带 callerToken 的 AsTask 表面取消误认为 native 完成。可在阶段前后检查 caller/stop cancellation，已发出的操作完整 await（不带 caller 取消提前脱离）。FlushAsync 后先检查 Size，再 Seek(0) 读取全部字节，短读失败；所有内存流/reader/writer 确实完成处置。若注入处置失败，同样保留待清理责任并供同实例重试，不覆盖主错误。

真实编码库只处理合成像素，自动化不调用真正截屏。

## 7. 验证与真实验收边界

自动化用 actual service/native adapter/worker + fake external Win32/DC/bits/IDesktopContext/time 验证：单屏/负坐标/混合 DPI 元数据/空洞/重叠/各屏 offsets、flags、top-down、alpha、checked 几何/预算、拓扑一次重试及二次失败；GetDC 至 ReleaseDC 同线程；正常及每阶段失败、恢复选择失败与 DeleteDC fallback、bits为空但bitmap非零、多个清理错误、保留同资源重试；取消在 native entered 后任务仍等待、发布前取消、并发忙/Stop 同实例重试/真实线程退出/未使用零native。

Crop 使用合成 padded stride/负坐标/部分交集/空交集/极端端点/非法枚举与帧，不允许用用户文件作为 fixture。实际 WinRT 编解码校验 PNG/BMP 像素、JPEG 质量配置/尺寸和有损合理容差；fake external 编码异步门闩验证 create/flush/read/cleanup 的取消完整等待和失败保留。实际 AppBootstrapper + fake边界验证退出早关准入、等待捕获/编码、失败不释放Host、重试成功；构建Host不启动真实Iris。

测试闸门先确认 entered，finally 先放行再等待 actual tasks/threads，合并主与清理异常；不得新建任何真实桌面抓屏/选区/剪贴板/输入/全局 hook/用户配置/密钥/Run/HTTP 测试，不运行性能脚本或真实 Iris。保留 T-022A 两项已批准的只读 cursor/monitor smoke 及两项受 error183 限制的隔离剪贴板跳过，如实报告。

串行 Release 全解决方案 build/test。真实显示器画面对齐/混合DPI、HDR/安全桌面/受保护窗口、单4K抓取<=60ms 和截图覆盖层<=150ms 均未验证；这是后续真实验收清单，不能用合成像素或 fake 耗时充抵。

## 8. 已读取依据及准备证据

- [CreateDIBSection](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-createdibsection)：DIB 的直接 bits、hSection=NULL 所有权及访问前 GdiFlush。
- [ReleaseDC](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-releasedc)：与 GetDC 同线程，CreateCompatibleDC 用 DeleteDC。
- [SelectObject](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-selectobject)、[DeleteObject](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-deleteobject)：保存旧对象、不能删除仍被选中的 bitmap。
- [BitmapEncoder](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.imaging.bitmapencoder?view=winrt-26100)、[FlushAsync](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.imaging.bitmapencoder.flushasync?view=winrt-26100)：异步内存编码及完成后读取。

原文/manifest：系统临时目录 iris-t025-official-docs，共13页。合成2×2 codec 探针 iris-t025-codec-probe/result.log 实际输出 PNG 129 bytes/BMP 70 bytes 且解码像素一致，JPEG 680 bytes/尺寸正确且为有损。只支持技术选型，不充抵本单大帧、取消、完整退出和真实截屏验收。

质量参数补证：iris-t025-jpeg-quality-probe/result.log，64×32合成渐变，BitmapPropertySet["ImageQuality"]=BitmapTypedValue(0.9f,PropertyType.Single) 成功；质量0.9为947 bytes、RGB平均绝对差0.8298，质量0.2对照为722 bytes、平均差3.6912。不是生产实现/NFR证据，不读取桌面或用户文件。
