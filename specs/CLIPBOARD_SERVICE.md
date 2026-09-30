# Windows 剪贴板服务

> 2026-09-30，Codex；T-021A / CCR-008。实现既有 IClipboardService，不包含选区快照恢复、键盘模拟或业务 Handler。

## 1. 平台与线程边界

`Win32ClipboardService` 位于 Platform.Windows/Clipboard，单例注册。可由 UI/STA 或后台/MTA 调用；不依赖 WPF、WinForms、OLE Clipboard 或 App 的 IUiDispatcher。原生 Win32 剪贴板本身不要求 STA。异步方法通过内部 SemaphoreSlim 串行化整个操作，等待可取消；原生调用在后台执行，不能在 UI 上 Open/Get/Set 或轮询重试。

优先使用每次原生尝试的短生命周期资源帧：在同一个后台同步委托内创建写入 owner HWND、Open、读取/发布、Close、销毁 HWND，中间不 await、不换线程。写入使用不可见的 message-only HWND（可用系统 STATIC 窗口类）；不得显示窗口、激活应用或创建 Application。所有数据立即呈现，不使用 SetClipboardData(format, NULL) 的延迟呈现；因此写入完成后不需要常驻 owner 窗口或额外 UI 线程。读取可用 OpenClipboard(NULL)。不新建全局工作队列、常驻 Dispatcher 或任务框架。

异步重试延迟在原生帧结束、没有打开剪贴板时进行。一个原生调用尚未退出时不能通过 WaitAsync 超时/取消放弃等待并释放串行门；GetClipboardData 可能等待其它应用的延迟呈现，Open 重试预算不能被描述成整个操作的硬超时。无需在本单解决其它进程完全无响应的系统级终止问题。

## 2. 重试、取消与写入提交点

- 先检查取消，再验证参数/等待串行门；进入实际操作及每次原生尝试前再检查取消。
- OpenClipboard 最多尝试 **10 次总计**：首次立即尝试，每次失败后若仍有下一次则异步等待 30 ms；只有 9 个间隔，最后一次失败不再睡眠。延迟注入 TimeProvider，不使用 Thread.Sleep。取消优先于重试耗尽的 ClipboardBusy。
- Open 成功即拥有 Close 责任，任何错误/取消都走 finally；未成功 Open 不得调用 Close。只有 Open 失败才按占用重试；其它 API 错误不得冒充 ClipboardBusy 或重新开始一次破坏性的写入。
- 写入前完成验证、图像解码、目标格式构造及 HGLOBAL 分配/填充。准备失败或 Open 重试耗尽时不得 EmptyClipboard。
- 写入的最后取消检查位于 Open 成功后、EmptyClipboard 前。Empty 成功后进入提交阶段，继续 Set、Close 和本地清理，不因迟到取消抛 OCE；Set 失败仍报告真实失败。不得在清空后因取消跳过 Set，也不得宣称 Win32 写入具有事务回滚能力。
- Get 在复制/Close 后再次检查取消；每个异步方法只有实际原生工作和清理退出后才释放串行门。

## 3. 文本与序列号

SetTextAsync 接受空串，保留原有换行和 Unicode，不 trim 或翻译预处理。null 或内嵌 U+0000 为 InvalidArgument，超过 **4,194,304 个 UTF-16 code unit** 为 InputTooLong；验证失败不修改剪贴板。以 CF_UNICODETEXT 写 UTF-16LE 与一个 NUL 终止符。

GetTextAsync 先判断 CF_UNICODETEXT 是否可用（系统允许从 CF_TEXT 隐式转换）。无该格式返回 null；合法空文本返回空串。借用 GetClipboardData 的句柄，用 GlobalSize/GlobalLock 有界读取并立即复制到托管字符串；禁止不带长度上限的 PtrToStringUni。数据块小于 2 字节、奇数长度或在块内找不到 NUL 视为 InvalidResponse；大于文本上限加终止符对应的 **8,388,610 字节**，或实际文本超过上限，视为 InputTooLong，不静默截断。不得读取句柄边界之外的内存。

GetSequenceNumber 直接返回 Win32 的 uint，不打开剪贴板、不与异步方法排队。0 原样返回，不擅自映射为异常；序列号可能回绕，外部使用相等/不等判断变化，不能用递增大小判断来源。它不是写入方身份证明，本单不实现选区恢复。

## 4. 图像

接受契约中的 PNG/JPEG/BMP。使用 Windows.Graphics.Imaging 的 BitmapDecoder/SoftwareBitmap，或等价的 Windows 原生解码路径；不新增图像 NuGet 包，不引入 WPF/WinForms/System.Drawing。解码在后台完成，第一帧、原始像素方向（忽略 EXIF 旋转），无缩放；请求的 Format 必须匹配实际编码，PixelWidth/PixelHeight 必须为正且匹配解码器原始尺寸。null/空字节、非法格式、尺寸矛盾或损坏内容为 ImageInvalid。

内部固定预算：编码字节最多 **33,554,432**；宽/高各最多 **32,768**；像素总数最多 **33,554,432**。先检查编码长度，再读取解码头校验真实尺寸和 checked long 算术，之后才分配像素缓冲；超限为 ImageTooLarge，不裁剪或静默缩小。调用方在任务完成前不得修改传入 ImageData.Bytes；实现不在完成后保留该数组，所有流、SoftwareBitmap 等资源及时释放。

输出为一个完整的 **CF_DIBV5** 内存对象，含 124 字节 BITMAPV5HEADER 和像素，不含 BMP 文件的 14 字节文件头。采用 top-down（负高度）、32bpp、BI_BITFIELDS、BGRA **straight alpha**，通道掩码 00FF0000/0000FF00/000000FF/FF000000，sRGB；无 alpha 输入按完全不透明处理。正确处理实际源 stride，不假定解码缓冲天然紧密排列。本单不额外发布自定义 PNG 格式、CF_BITMAP 或 CF_DIB 副本；面向实际应用的图像粘贴兼容性在截图/UI 集成时继续验收。

## 5. 原生所有权与错误

SetClipboardData 的内存必须由 GlobalAlloc(GMEM_MOVEABLE) 分配；GlobalLock 填充后解锁再发布。Set 成功后所有权属于系统，本地不能再写入或 GlobalFree；Set 失败则仍由本地释放。借来的读取句柄永不 GlobalFree，且不在 Close 之后访问。使用窄的 internal 原生适配器/格式帮助类型便于故障注入；不能用 Marshal.AllocHGlobal 代替要求可移动的剪贴板内存。

非占用的原生失败使用 IrisException(StorageFailure) 和固定文案；允许安全 Detail 包含 API 名及数值错误码，不携带原始异常链/消息。图像与格式错误按上节分类。已有主失败优先于清理失败，清理失败安全记录；无主失败的 Close 等失败也不能被描述成成功。日志/异常/ToString 不包含文本、图像字节或其内容摘要。每个本地 HGLOBAL、HWND、锁、串行门均有明确的成功/失败清理路径。

## 6. 实机验证环境

原生集成测试在**独立辅助进程、新建非交互窗口站**中执行，禁止修改当前用户的交互剪贴板，也不能改成“备份几个格式后恢复”。启动时先以普通 MTA 入口执行 CreateWindowStationW(NULL, CWF_CREATE_ONLY, ...) → SetProcessWindowStation → CreateDesktop/SetThreadDesktop，再初始化可能创建线程窗口的 COM/STA/图像框架。仅管理员能指定新窗口站名字；普通进程用 NULL，由系统生成；CWF_CREATE_ONLY 保证已有窗口站时失败，禁止复用其它进程的窗口站。创建/切换失败立即停止该原生验证，不退回 WinSta0；父测试明确失败或按环境跳过并在报告标为未验证，不把跳过计入通过证据。辅助进程串行运行，有退出超时；仅可终止自己启动的超时子进程。

测试数据完全合成。结束前关闭剪贴板和 owner HWND，恢复原线程桌面/进程窗口站，关闭自己创建的句柄。父测试确认退出码与协议结果，不仅看 stdout 中是否有“通过”。不要在通用测试宿主中 SetProcessWindowStation，以免影响同进程其它测试。

架构师本机探针：`C:\Users\28910\AppData\Local\Temp\iris-clipboard-design-probe\Probe.csproj`。2026-09-30 实际运行输出：

```text
PRIVATE_WINDOW_STATION unicode-roundtrip-after-owner-destroy=True
INTERACTIVE_CLIPBOARD sequence-unchanged=True
```

上述输出来自早期 flags=0 的探针，只验证非交互窗口站内的原生文本、短生命周期 owner 和交互剪贴板序列号未变化；**不能证明该窗口站是新创建或由探针独占**，成功时 GetLastError 也不足以证明独占。因此正式测试必须使用 CWF_CREATE_ONLY。最终探针加入该标志后的实际结果为：

```text
Unhandled exception. System.Exception: create station error=183
```

本机当前默认生成的窗口站已存在，正式隔离条件未满足；T-021A 不得复用它继续写入。可在独立测试账户/虚拟机满足新建条件后执行原生集成测试；当前环境无法创建时明确跳过并报告未验证，仍完成全部可运行的单元/格式/故障注入检查。此前有名字的窗口站创建返回错误 5；入口加 STAThread 时切换桌面返回错误 170。最终独占创建失败发生在 OpenClipboard 之前。以上均不是未开发服务或图像的验收证据。

## 7. 本轮实际查阅的官方依据

- [OpenClipboard](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-openclipboard)：每次成功 Open 必须 Close；以 NULL 打开再 Empty 会令 owner 为 NULL，导致 SetClipboardData 失败。
- [SetClipboardData](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setclipboarddata)：GMEM_MOVEABLE；成功后系统取得所有权，不得再释放或写入。
- [GetClipboardData](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getclipboarddata)：剪贴板是不可信输入；立即复制，不能释放借用句柄或 Close 后继续访问。
- [Standard Clipboard Formats](https://learn.microsoft.com/en-us/windows/win32/dataxchg/standard-clipboard-formats)：CF_UNICODETEXT、CF_DIBV5 的数据格式。
- [GetClipboardSequenceNumber](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getclipboardsequencenumber)：序列号按窗口站维护，没有 WINSTA_ACCESSCLIPBOARD 权限时返回 0；延迟呈现有时序限制。
- [Window Stations](https://learn.microsoft.com/en-us/windows/win32/winstation/window-stations) / [CreateWindowStationW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-createwindowstationw)：每个窗口站有自己的剪贴板；命名权限与 NULL 名字的生成规则。

BitmapDecoder.GetSoftwareBitmapAsync 页面本轮请求超时，未将其列为已查阅来源；图像解码、色彩/alpha 和真实 CF_DIBV5 结构须由 T-021A 的实际编译与合成图验证确认，不以本文的设计规则代替验证。
