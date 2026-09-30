# T-022A 补充准备：设备筛选与最近显示器验证

> 2026-09-30，Codex；补充 DESKTOP_CONTEXT_DRAFT.md。
> **候选规则及验证记录，尚未签发工单或批准契约变更。** 当前 T-021A-R1 继续原范围；本轮仅新增本文件及独立临时探针。

## 1. 官方依据确认的设备筛选

EnumDisplayMonitors 不只枚举可供用户摆放窗口的显示器。官方原文包含：

> including invisible pseudo-monitors associated with the mirroring drivers

DISPLAY_DEVICE_MIRRORING_DRIVER 表示用于远程绘制等用途的伪设备，关联不可见伪显示器。EnumDisplayDevices 文档另外要求：要查询桌面中的显示设备，应使用具有 DISPLAY_DEVICE_ATTACHED_TO_DESKTOP 标志的项。

建议在正式 T-022A 中采用以下流程：

1. `EnumDisplayDevicesW(NULL, index, ...)` 取得当前会话的 adapter/view 设备表；每次初始化 cb，以 DeviceName 做 OrdinalIgnoreCase 索引。
2. `EnumDisplayMonitors(NULL, NULL, ...)` + `GetMonitorInfoW` 取得物理 Bounds/WorkArea 和 szDevice。
3. 用 MONITORINFOEX.szDevice 关联第一步的 DISPLAY_DEVICE.DeviceName，而不是把监视器子设备名或友好描述当作同一标识。
4. 只保留 **ATTACHED_TO_DESKTOP 已设置且 MIRRORING_DRIVER 未设置**的项。不要通过描述字符串猜测“实体显示器”，也不要直接剔除远程/虚拟设备；RDP 或虚拟显示器可以是用户真正使用的桌面。
5. 显示器名称在设备表中缺失、同名信息冲突、有效项为空或中途查询失败，按不一致快照处理：丢弃本轮、最多重采一次，仍失败再安全报错。不能把关联失败当作“确定是伪设备”并静默返回部分结果。
6. 检查几何与 DPI，再按主屏优先、DeviceName Ordinal 稳定输出。此前草案中的隐藏 PerMonitorV2 窗口 DPI 路径继续适用。

这两个枚举不是系统提供的原子快照；有限重采只能处理观测到的不一致，不能保证返回以后硬件不会变化。定位窗口的调用方仍需处理显示器随后消失的情况。

本机的设备表条目数不等于物理显示器数量，也不能据此推导多屏验证已完成。

## 2. 最近显示器的候选确定规则

原契约的“最近”没有规定距离或并列规则。建议在未来 CCR 中明确为**物理像素点之间的距离**，不宣称在所有并列情况下都与 MonitorFromPoint 的内部选择相同。

设有效 Bounds 为 `(X,Y,Width,Height)`：

- 包含关系仍为 `[X,X+Width) × [Y,Y+Height)`。
- 该矩形包含的最后一个整数像素为 `right=X+Width-1`、`bottom=Y+Height-1`。
- 横向距离 dx：点在左侧时为 X−x，在右侧时为 x−right，横向落在范围内为 0；dy 同理。
- 按 `dx²+dy²` 选择最小值；多个范围覆盖该点时也属于距离为 0 的并列情况。
- 并列先主屏，再按 DeviceName Ordinal。不得让调用方传入集合的偶然顺序改变结果。

所有端点加减先转 long；平方和使用 Int128。两个 int 坐标的差最多为 4,294,967,295，其平方已超出 long，两个方向的平方和还会超出 ulong。仅把最终结果强转为 long/Int128 而让乘法先在较窄类型中执行仍会溢出。

几何输入先验证：宽高正数，long 计算的右/下边界仍可表示为 int；DpiScale 有限且大于 0。这样既有 PixelRect.Right/Bottom 在正常平台输出上不会溢出。本准备不要求扩展或重写 Core.Geometry 的公共类型。

## 3. 独立验证方式

项目：`C:\Users\28910\AppData\Local\Temp\iris-t022a-geometry-prep\Probe.csproj`；源码为同目录 Program.cs。没有仓库 ProjectReference，不参与 Iris.slnx，也不争用返工中的编译输出。

纯算法分支验证：

- 七个命名场景：左侧负坐标、上方显示器、共用边界、屏幕间空隙、主屏并列、名称并列、重叠范围。
- 固定随机种子 2201，生成 10,000 组三矩形小布局。参考算法实际枚举每个矩形中的所有整数像素取最短距离，不复用候选算法的区间公式；另外反转输入顺序，确认结果不变。
- 对 int.MinValue 到 int.MaxValue 的两个方向距离，用 BigInteger 独立核对 Int128 结果。
- 拒绝零/负尺寸、右/下边界溢出、NaN、Infinity、零 DPI。
- 检查 attached、mirror、remote 标志组合，确认 attached remote 项不会仅因远程属性被排除。

原生分支仅枚举设备/显示器并关联标志，使用同步的线程 DPI context 作用域，finally 恢复。没有创建或显示窗口，没有访问前台标题、剪贴板、图像或用户文件，也没有修改显示设置。

执行命令：

```powershell
dotnet run --project C:\Users\28910\AppData\Local\Temp\iris-t022a-geometry-prep\Probe.csproj -c Release
dotnet run --project C:\Users\28910\AppData\Local\Temp\iris-t022a-geometry-prep\Probe.csproj -c Release --no-build -- native
```

实际输出：

```text
BOUNDARY_CASES passed=7
BRUTE_FORCE_LAYOUTS checked=10000 mismatches=0 order-independent=True
EXTREME_DISTANCE actual=36893488130239234050 exceeds-Int64=True big-integer-match=True
INVALID_INPUTS rejected=7
DEVICE_FLAG_CASES passed=5 active-remote-retained=True
MONITOR_MAP name=\\.\DISPLAY1 adapter-match=True attached=True mirror=False keep=True
NATIVE_MAP adapters=35 monitors=1 matched=1 retained=1
```

本机原生结果仍只有一块显示器。伪设备、真实 RDP/虚拟显示器、多屏/混合 DPI、热插拔的运行行为本轮没有实机验证；标志组合与多布局部分是候选规则的合成验证。以上不是尚未实现的 DesktopContext 的产品测试结果。

## 4. 可直接带入正式工单的断言

| 输入/交错 | 必须断言 |
|---|---|
| adapter attached + mirror | 不进入可定位显示器列表 |
| adapter attached + remote、未标 mirror | 保留，不按“远程”一概排除 |
| szDevice 与设备表仅大小写不同 | 正确关联；使用与返回 DeviceName 的规则保持一致 |
| 枚举到 monitor，但关联不到 adapter | 有界重采/失败，不能默默丢失真实桌面 |
| 两次返回的集合或原始 native 缓冲被修改 | 已发布快照不随之改变 |
| 负坐标/共边界/空隙/重叠/并列 | 使用明确的半开区间、距离和稳定并列规则 |
| 极端坐标、颠倒输入顺序 | 不溢出，选择结果不依赖枚举顺序 |
| 获取任何 native 信息失败 | 恢复线程 DPI context，不制造主屏或 1.0 DPI 占位值 |

正式工单仍需结合 R1 验收后的代码风格，补上原生故障注入、集合不可变、资源清理与组合根检查。前台 HWND/PID 竞态、固定错误文案与标题读取策略沿用上一份草案，签发时统一定稿。

## 5. 本轮实际读取的官方资料

- [EnumDisplayMonitors](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumdisplaymonitors)：包含 invisible pseudo-monitors；NULL HDC 时使用虚拟屏幕坐标。
- [DISPLAY_DEVICEW](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-display_devicew)：MIRRORING_DRIVER 的含义，以及 adapter 与 monitor 两类 DeviceName 的区别。
- [EnumDisplayDevicesW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumdisplaydevicesw)：当前会话、ATTACHED_TO_DESKTOP、NULL lpDevice 与子设备查询规则。

T-021A-R1 的工单、契约、生产代码和测试均未被本轮准备工作修改。无需在返工期间插入新的开发任务；待其复审通过后，将本记录与 DESKTOP_CONTEXT_DRAFT.md 合并为正式设计。
