# 亚克力界面重构 · 阶段 1（主题层、材质服务、主题切换修正）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 建立共用主题层 `src/Iris.App/Themes/`（样式 + 浅深两套自有颜色键 + 纯色回退覆盖），在 `App.xaml` 与两处测试资源环境合并同一份；新增窗口材质服务，对设置窗口、翻译弹窗、历史窗口应用亚克力，其余窗口保持纯色；修正 `ThemeService` 切换主题时默认带 Mica 且误改“碰巧第一个创建”的窗口的背景。

**Architecture:** 主题层是纯资源字典，不含代码：`Iris.Styles.xaml` 放样式与数值，`Iris.Light.xaml` / `Iris.Dark.xaml` 只放 WPF-UI 没有的颜色键（由 `ThemeService` 随主题替换 `MergedDictionaries` 里的那一项），`Iris.SolidFallback.xaml` 是给「系统不支持亚克力」备好的不透明覆盖值（**阶段 1 只建文件、不合并**，理由与裁决请求见 Task 10 报告第 20 条）。材质由 `WindowMaterialService` 独占管理：XAML 里三个窗口只设 `ExtendsContentIntoTitleBar="True"`（WPF-UI 强制要求），**不设** `WindowBackdropType`；服务在句柄创建后、且系统支持时才把 `WindowBackdropType` 设为 `Acrylic`。主题切换会重置窗口 `Background`，所以 `ThemeService` 切完要回调服务重新透明化。

**Tech Stack:** C# / .NET 10 / WPF、WPF-UI 4.3.0、xunit.v3 + Shouldly + NSubstitute、Microsoft.Testing.Platform runner、`dotnet test Iris.slnx -c Release`、Windows PowerShell 5.1（本机无 `pwsh`）。

---

## 已实测的事实（本计划的依据，不必重查）

1. **`ui:FluentWindow` 总会套一层 `WindowChrome`**，`CaptionHeight=0`、`UseAeroCaptionButtons=false`，系统标题栏高度被压成 0。所以现有 `ui:TitleBar` 放在 `Grid.Row="0"` 本来就正常，**不需要** `ExtendsContentIntoTitleBar` 也不会出现两套标题栏。
2. **`ExtendsContentIntoTitleBar="True"` + `WindowBackdropType="Acrylic"` 实测**：不抛异常；`Background` 变 `#00FFFFFF`；DWM `SYSTEMBACKDROP_TYPE=3`（Acrylic）；亚克力可见（高对比背景透过模糊可见）；圆角 `CORNER_PREFERENCE=2`（Round）；`ui:TitleBar` 高 48，内容从 Y=48 起**不被压住**，不需要额外 Margin/高度；三个标题栏按钮 `Visible/Enabled/IsHitTestVisible=True`，命中为 HTCLOSE / HTMAXBUTTON / HTMINBUTTON，可点。
3. **`ExtendsContentIntoTitleBar=false` 时设 `WindowBackdropType` 会抛 `InvalidOperationException`**（`Cannot apply backdrop effect if ExtendsContentIntoTitleBar is false.`），即使先设属性、后创建句柄也会在 `OnSourceInitialized` 抛。
4. **只调静态 `WindowBackdrop.ApplyBackdrop(window, Acrylic)` 而不改 `Background`，亚克力看不见**：DWM 读回是 3，但窗口 `Background` 仍是不透明的 `#FFFAFAFA`，把亚克力盖住。要看见必须同时清透明（`WindowBackdrop.RemoveBackground(window)` 或把 `Background` 设为透明）。
5. **`Hide()` 再 `Show()` 不丢亚克力**（`SYSTEMBACKDROP_TYPE=3` 与 `Background=#00FFFFFF` 都保持）。
6. **`ApplicationThemeManager.Apply(theme, None)` 既会把窗口 `Background` 重置成不透明的主题色**（浅色 `#FFFAFAFA`、深色 `#FF202020`），**也会把已应用的 DWM 亚克力拆掉**（`DWMWA_SYSTEMBACKDROP_TYPE` 读回 3 → 1）；所以切主题后**必须重新上背板 + 重新透明化**，只清背景不够。实现在 Task 7 Step 3（`WindowBackdrop.ApplyBackdrop` + `RemoveBackground`），实测该修法把读回值恢复到 3。**原先这一条写的是「不会移除已应用的 DWM 亚克力（读回仍是 3）」——那是裸探针进程里的假象**：在 `Application.Current.Resources` 没合 WPF-UI 主题字典的进程里，`Apply(theme, None)` 观测不到任何效果（背板与背景都没变，连背景重置都没发生），因此那类进程走不到 `WindowBackgroundManager.UpdateBackground`；在合了字典的进程里（仓库测试宿主、真实应用）两件事都会发生。两边的原始读数见 Task 7 报告第 23 条。切 DPI 是否丢失**未验证**（本机单显示器 100%）。
7. 本机前台有全屏程序拦截合成输入，**拖拽窗口和双击最大化未能验证**（只验证了 `NCHITTEST` 返回 `HTCAPTION`）。
8. `Wpf.Ui.Markup.ThemesDictionary` 换主题时会用**新的 `ResourceDictionary` 实例**替换 `Application.Current.Resources.MergedDictionaries` 里的那一项。
9. **没有测试按位置索引 `MergedDictionaries`，也没有测试断言字典数量**；所有相关测试都用 `.OfType<Wpf.Ui.Markup.ThemesDictionary>().FirstOrDefault()`。所以往 `App.xaml` 加合并字典是安全的。
10. WPF-UI 的 `ControlsDictionary` **已经定义了 `CheckBox` 的隐式样式**（`DefaultCheckBoxStyle`，21 个 setter，含 `MinWidth=120`、`MinHeight=32`、`Padding=11,5,11,6`、`Cursor=Hand`）。从兄弟合并字典里写 `BasedOn="{StaticResource {x:Type CheckBox}}"` 能解析到它（App.xaml 模式与代码合并模式都验证过），但**必须显式覆盖 `MinWidth` / `MinHeight` / `Padding` 为 0**，否则开关所在的行会被撑到 120 宽、32 高。
11. 自定义 `ControlTemplate` 里的普通 `<ContentPresenter>` **仍然会应用调用方的 `ContentTemplate`**，也遵守 `HorizontalContentAlignment="Stretch"`。所以现有用 `ContentTemplate="{StaticResource WrappingCheckBoxContent}"` 的换行勾选框，换成开关样式后仍然换行（实测 wrap=Wrap、宽 256）。
12. `ui:SymbolIcon` / `SymbolRegular` 用的是包内自带的 `FluentSystemIcons` 字体（`pack://application:,,,/Wpf.Ui;component/Resources/Fonts/#FluentSystemIcons-Regular`），**不依赖 Windows 11 的 Segoe Fluent Icons**。`{ui:SymbolIcon SymbolRegular.Copy24}` 这类标记扩展在 `Wpf.Ui.Markup` 里存在，构造签名为 `(SymbolRegular)`、`(string)`、`(SymbolRegular, bool filled)`。计划里用到的 29 个符号名全部存在。
13. `AccentFillColorDefaultBrush` / `SecondaryBrush` / `TertiaryBrush` 由 `ApplicationAccentColorManager` 直接写在 `Application.Resources` 上，**会随 `Apply` 变化**，必须用 `DynamicResource`。`SystemAccentColorPrimaryBrush` 与 `AccentFillColorDisabledBrush` 是静态的，`Apply` 不会改。
14. **现有测试没有任何一处断言精确颜色值**，全部用通道阈值或对比度比值；**没有测试断言 `TextFillColorTertiaryBrush`**。`UpdateRecoveryRenderingTests.cs:41-45` 会把 `app.Resources` 整个换成只有主题字典 + 控件字典 → 我们的字典不能是该测试的对比度前提。
15. 对比度辅助方法是各类里各自私有的（`UpdateRecoveryRenderingTests.cs:117-134` 的 `Paint` / `Contrast`），**新测试要自己带一份**。

**为什么这个计划要单开一段“已知事实”：** 第 3、4、6 条决定了本计划的形状——XAML 里不能设 `WindowBackdropType`，服务必须在句柄创建后设，且主题切换后必须重新透明化。任何偏离这三条的写法都会实测失败。

---

## 前置条件

- 分支 `codex/t041-acrylic-ui`，`git log --oneline -1` 应为 `T-041 phase 0: ...`（`513ab5d`）。**动手前先跑一次 `git status --short`**：这个仓库的提交曾在本会话之外发生，提交前必须重新确认 HEAD 与工作区。
- 只改 `src/Iris.App/` 和 `tests/Iris.App.Tests/`；不碰 `specs/`、`tasks/`、`.codex`、`specs/CONTRACTS.cs`。
- `src` 里不出现中文：新增 XAML 的注释一律用英文（照 `Resources/EditingMenus.xaml` 的做法），不留中文字面量。
- 不新增依赖包、不升级 WPF-UI；`Iris.App.csproj` 的 `ProjectReference` 集合不改（`tests/Iris.ArchitectureTests/DependencyRulesTests.cs:86` 钉死了它）。
- 新增 `src/Iris.App/Themes/*.xaml` 会被 WPF 的默认 `Page` 通配自动编译，**不需要改 csproj**（`Resources/EditingMenus.xaml` 同样没有 csproj 条目却编译了，证据是 `src/Iris.App/obj/Release/.../Iris.App.g.resources` 里有它的 baml）。
- 每个阶段一次提交；本阶段结束时跑 `dotnet build Iris.slnx -c Release`（零警告）、`dotnet test Iris.slnx -c Release`、`powershell -NoProfile -ExecutionPolicy Bypass -File build/audit-localization.ps1`。
- **本阶段只有一个本地提交**（工单第 23、27 行：「之后每阶段一个提交」，并明确说明这与「一张工单一个提交」的习惯不同）。每个任务末尾那一步只做 `git add`，真正的提交在 Task 10 Step 6。不要额外提交，不要 amend 或 rebase（工单第 19 行禁止）。

---

### Task 1: 建 `Themes/Iris.Styles.xaml`（数值与基础样式）

**Files:**
- Create: `src/Iris.App/Themes/Iris.Styles.xaml`
- Create: `tests/Iris.App.Tests/Themes/IrisThemeResourcesCollectionDefinition.cs`
- Modify: `tests/Iris.App.Tests/TestSupport/WpfTestRunner.cs`（`EnsureApplication`）
- Test: `tests/Iris.App.Tests/Themes/IrisStyleResourceTests.cs`

- [ ] **Step 1: 让测试宿主先加载 Iris 字典，再建 collection 与失败测试**

**先做这一半，否则后面的测试永远拿不到新资源。** 已实测：测试宿主里的 `Application.Current` **不是** `App.xaml`，而是 `WpfTestRunner.EnsureApplication()`（`tests/Iris.App.Tests/TestSupport/WpfTestRunner.cs:223-235`）自建的一个普通 `Application`，它只合并 `ThemesDictionary` + `ControlsDictionary` + `EditingMenus.xaml`，**从不加载 `App.xaml`**。所以「把字典合并进 `App.xaml`」在测试里完全不生效，`TryFindResource`/`FindResource` 一律返回 null。**App.xaml 里合并了什么，测试宿主就得在这里补什么。**

把 `EnsureApplication` 改成：

```csharp
    private static void EnsureApplication()
    {
        if (Application.Current == null)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Light });
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
            AddIrisDictionaries(app.Resources.MergedDictionaries);
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Iris.App;component/Resources/EditingMenus.xaml", UriKind.Absolute)
            });
        }
    }

    // App.xaml 的合并字典在测试宿主里不生效：这里的 Application 是自建的，从不加载 App.xaml。
    // App.xaml 里合并了什么，就要在这里补什么。顺序与 App.xaml 一致。
    private static void AddIrisDictionaries(System.Collections.ObjectModel.Collection<ResourceDictionary> dictionaries)
    {
        dictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Iris.App;component/Themes/Iris.Styles.xaml", UriKind.Absolute)
        });
    }
```

`Iris.Light.xaml` 要到 Task 4 才存在，**现在不要加它**（现在加会因 pack URI 不存在而抛异常）。Task 4 会往 `AddIrisDictionaries` 里插在 `Iris.Styles.xaml` **之前**。也不要给 `EnsureApplication` 加别的调用点，Task 4 和 Task 5 会在此基础上继续。

下面这一半是 collection 定义与失败测试。

本阶段所有新增测试类都挂同一个 `[Collection("IrisThemeResources")]`，并且这个 collection 定义要 `DisableParallelization = true`。理由有实测依据：既有 `UpdateRecoveryRenderingTests` 会把 `Application.Current.Resources` **整个换成**只含主题字典 + 控件字典的精简版（`:38-44`，`finally` 里还原 `:111`），它靠 `[Collection("Localization", DisableParallelization = true)]`（`LocalizationTests.cs:30`）避免与其他 collection 并行。我们的新测试要读全局资源里的 Iris 键，同样不能和这类夹具并行。

`tests/Iris.App.Tests/Themes/IrisThemeResourcesCollectionDefinition.cs`：

```csharp
using Xunit;

namespace Iris.App.Tests.Themes;

// 这些测试读 Application.Current.Resources 里的 Iris 键、并离屏渲染窗口；
// 其他夹具会临时替换全局资源字典，所以本 collection 不与其他 collection 并行。
[CollectionDefinition("IrisThemeResources", DisableParallelization = true)]
public sealed class IrisThemeResourcesCollectionDefinition;
```

**类名不能以 `Collection` 结尾**（`CA1711` 在 `TreatWarningsAsErrors=true` 下会直接让构建失败），照仓库既有惯例（`TestSupport/AppShutdownerSharedStateCollectionDefinition.cs`）用 `...CollectionDefinition`。`[CollectionDefinition("IrisThemeResources")]` 里的**字符串**才是 Task 2-9 引用的名字，不要改动它。

**Task 2、3、4、6、7、8、9 新增的测试类同样挂 `[Collection("IrisThemeResources")]`**，不再逐个重复说明。

`tests/Iris.App.Tests/Themes/IrisStyleResourceTests.cs`：

```csharp
using System.Threading.Tasks;
using System.Windows;
using Iris.App.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Themes;

[Collection("IrisThemeResources")]
public sealed class IrisStyleResourceTests
{
    // 只覆盖被样式真正消费的 token。Type 就是这些 token 的全部契约，所以断言类型；
    // 不断言具体数值——那等于把 XAML 抄第二遍。IrisPillCornerRadius / IrisCardGap /
    // IrisControlGap 本阶段无消费者，故不在此列。
    [Theory]
    [InlineData("IrisCardCornerRadius")]
    [InlineData("IrisRowCornerRadius")]
    [InlineData("IrisTagCornerRadius")]
    public Task CornerRadiusResources_AreDefined(string key) => WpfTestRunner.RunAsync(() =>
    {
        Application.Current.TryFindResource(key).ShouldBeOfType<CornerRadius>();
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("IrisCardPadding")]
    [InlineData("IrisRowPadding")]
    [InlineData("IrisRowGap")]
    public Task ThicknessResources_AreDefined(string key) => WpfTestRunner.RunAsync(() =>
    {
        Application.Current.TryFindResource(key).ShouldBeOfType<Thickness>();
        return Task.CompletedTask;
    });
}
```

（写成 `void` 方法直接返回 `WpfTestRunner.RunAsync(...)` 会触发 `CS4014`（未 await）而编译失败——同样在 `TreatWarningsAsErrors` 下是错误。返回 `Task` 或 `async Task` 都行，不要用 `void`。）

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~IrisStyleResourceTests"`
Expected: 6 个用例（两个 theory 各 3 个）全部 FAIL，断言 `ShouldBeOfType<CornerRadius>()` / `ShouldBeOfType<Thickness>()` 失败（资源键还不存在）。

- [ ] **Step 3: 建文件**

`src/Iris.App/Themes/Iris.Styles.xaml`：

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml">

    <!-- Spacing and corner radii. Values are DIP; the same numbers are used by
         the settings rows, the translation popup cards and the history panes.
         Rule: only metrics shared across windows are tokens here; padding and margins
         internal to a single control stay literal values in that style. -->
    <CornerRadius x:Key="IrisCardCornerRadius">8</CornerRadius>
    <CornerRadius x:Key="IrisRowCornerRadius">6</CornerRadius>
    <CornerRadius x:Key="IrisTagCornerRadius">4</CornerRadius>
    <Thickness x:Key="IrisCardPadding">16</Thickness>
    <Thickness x:Key="IrisRowPadding">16,12</Thickness>
    <Thickness x:Key="IrisRowGap">0,0,0,8</Thickness>

    <!-- Reserved for later phases of this redesign; no style consumes them yet:
         IrisPillCornerRadius -> the settings nav selection pill (phase 2),
         IrisCardGap -> vertical gap between stacked cards,
         IrisControlGap -> left gap before a row's control. -->
    <CornerRadius x:Key="IrisPillCornerRadius">12</CornerRadius>
    <Thickness x:Key="IrisCardGap">0,0,0,12</Thickness>
    <Thickness x:Key="IrisControlGap">12,0,0,0</Thickness>

    <!-- Translucent card surface. IrisAcrylic* come from Iris.Light/Iris.Dark; the opaque
         overrides in Iris.SolidFallback are not wired up in phase 1 (a Dark variant has to land
         first), so these translucent fills are what the cards actually use. -->
    <Style x:Key="IrisCard" TargetType="Border">
        <Setter Property="Background" Value="{DynamicResource IrisAcrylicCardFillBrush}" />
        <Setter Property="BorderBrush" Value="{DynamicResource IrisAcrylicCardStrokeBrush}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="CornerRadius" Value="{StaticResource IrisCardCornerRadius}" />
        <Setter Property="Padding" Value="{StaticResource IrisCardPadding}" />
    </Style>

    <!-- One setting per row: title and description on the left, the control on the right. -->
    <Style x:Key="IrisRowCard" TargetType="Border" BasedOn="{StaticResource IrisCard}">
        <Setter Property="Padding" Value="{StaticResource IrisRowPadding}" />
        <Setter Property="Margin" Value="{StaticResource IrisRowGap}" />
    </Style>

    <Style x:Key="IrisRowTitle" TargetType="TextBlock">
        <Setter Property="FontSize" Value="14" />
        <Setter Property="FontWeight" Value="SemiBold" />
        <Setter Property="TextWrapping" Value="Wrap" />
        <Setter Property="Foreground" Value="{DynamicResource TextFillColorPrimaryBrush}" />
    </Style>

    <Style x:Key="IrisRowDescription" TargetType="TextBlock">
        <Setter Property="FontSize" Value="12" />
        <Setter Property="Margin" Value="0,3,0,0" />
        <Setter Property="TextWrapping" Value="Wrap" />
        <Setter Property="Foreground" Value="{DynamicResource TextFillColorSecondaryBrush}" />
    </Style>

    <!-- Section title: same type as a row title, just spaced above its group. -->
    <Style x:Key="IrisSectionHeader" TargetType="TextBlock" BasedOn="{StaticResource IrisRowTitle}">
        <Setter Property="Margin" Value="0,0,0,8" />
    </Style>

    <!-- Small status chips: "favourite", "result truncated", "stale". -->
    <Style x:Key="IrisTag" TargetType="Border">
        <Setter Property="Background" Value="{DynamicResource SubtleFillColorSecondaryBrush}" />
        <Setter Property="CornerRadius" Value="{StaticResource IrisTagCornerRadius}" />
        <Setter Property="Padding" Value="6,2" />
        <Setter Property="Margin" Value="0,0,6,0" />
        <Setter Property="VerticalAlignment" Value="Center" />
    </Style>

    <Style x:Key="IrisTagText" TargetType="TextBlock">
        <Setter Property="FontSize" Value="11" />
        <Setter Property="Foreground" Value="{DynamicResource TextFillColorSecondaryBrush}" />
    </Style>

    <!-- Navigation entry. The selected state is applied in code-behind by the settings window. -->
    <Style x:Key="IrisNavItem" TargetType="Border">
        <Setter Property="Padding" Value="10,8" />
        <Setter Property="Margin" Value="0,0,0,2" />
        <Setter Property="CornerRadius" Value="{StaticResource IrisRowCornerRadius}" />
        <Setter Property="Background" Value="Transparent" />
        <Setter Property="Cursor" Value="Hand" />
    </Style>
</ResourceDictionary>
```

- [ ] **Step 4: 合并进 `App.xaml`**

`src/Iris.App/App.xaml` 的 `MergedDictionaries` 目标形状是（本步骤只加 `Iris.Styles.xaml` 一行，`Themes/Iris.Light.xaml` 那行是 Task 4 才加）：

```xml
            <ResourceDictionary.MergedDictionaries>
                <ui:ThemesDictionary Theme="Light" />
                <ui:ControlsDictionary />
                <ResourceDictionary Source="Themes/Iris.Styles.xaml" />
                <ResourceDictionary Source="Resources/EditingMenus.xaml" />
            </ResourceDictionary.MergedDictionaries>
```

**顺序必须在 `ui:ControlsDictionary` 之后**：Task 2 会往 `Iris.Styles.xaml` 里加 `BasedOn="{StaticResource {x:Type CheckBox}}"` 的开关样式，那个 `StaticResource` 要能解析到控件字典里的隐式 `CheckBox` 样式。注意在**本任务**内部这一点还无法用行为差异验证（本任务的 `Iris.Styles.xaml` 里没有任何 `CheckBox` 样式，且对 WPF-UI 键的引用都是惰性解析的 `DynamicResource`），所以现在只做静态顺序核对。

（`Iris.Light.xaml` 在 Task 4 创建；本步先只加 `Iris.Styles.xaml` 一行，Task 4 再加 `Iris.Light.xaml`。这样每一步都能编译。）

- [ ] **Step 5: 跑测试确认通过**

Run: `dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~IrisStyleResourceTests"`
Expected: 6 个用例（两个 theory 各 3 个）全部 PASS。

- [ ] **Step 6: 暂存改动（本阶段不提交）**

```bash
git add src/Iris.App/Themes/Iris.Styles.xaml src/Iris.App/App.xaml tests/Iris.App.Tests/Themes tests/Iris.App.Tests/TestSupport/WpfTestRunner.cs
# 设计 §10 与工单第 23 行：每个阶段只有一个提交，见 Task 10 Step 6。此处只暂存，不提交。
# git commit -m "Add shared Iris style layer and merge it into the application resources"
```

---

### Task 2: 开关外观的 `CheckBox` 样式

控件类型仍是 `System.Windows.Controls.CheckBox`，只换 `ControlTemplate`。模板取自 WPF-UI 4.3.0 `ToggleSwitch.xaml`，**用静态 setter 版本**（不带 `Storyboard`）。理由：实测带故事的版本在 `IsChecked` 翻转后**立即渲染仍是旧状态**（`EventTrigger Loaded` 的 `SeekStoryboard` 需要故事已经启动），而离屏渲染测试都是立即取图，会把旧状态拍进证据图片。

**Files:**
- Modify: `src/Iris.App/Themes/Iris.Styles.xaml`
- Test: `tests/Iris.App.Tests/Themes/IrisSwitchStyleTests.cs`

- [ ] **Step 1: 写失败测试**

`tests/Iris.App.Tests/Themes/IrisSwitchStyleTests.cs`：

```csharp
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Iris.App.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Themes;

[Collection("IrisThemeResources")]
public sealed class IrisSwitchStyleTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task SwitchStyle_RendersCheckedStateImmediately(bool isChecked) => WpfTestRunner.RunAsync(() =>
    {
        var checkBox = new CheckBox { Style = (Style)Application.Current.FindResource("IrisSwitchCheckBox") };
        var host = new Window { Content = checkBox, Width = 300, Height = 60, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            checkBox.IsChecked = !isChecked;
            host.Show();
            checkBox.IsChecked = isChecked;
            host.UpdateLayout();
            var knob = (System.Windows.Shapes.Rectangle)checkBox.Template.FindName("ToggleKnob", checkBox);
            var transform = (System.Windows.Media.TranslateTransform)knob.RenderTransform;
            transform.X.ShouldBe(isChecked ? 9d : -9d);
            checkBox.MinWidth.ShouldBe(0d);
            checkBox.MinHeight.ShouldBe(0d);
        }
        finally
        {
            host.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task SwitchStyle_KeepsWrappingContentTemplate() => WpfTestRunner.RunAsync(() =>
    {
        // SettingsWindow.xaml:16 的 WrappingCheckBoxContent 是**窗口级**资源，全局取不到，
        // 所以这里就地解析一个与它等价的模板。要验证的是自定义 ControlTemplate 是否仍然尊重
        // 调用方给的 ContentTemplate，与模板从哪来无关。
        var wrappingTemplate = (DataTemplate)XamlReader.Parse(
            "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><TextBlock Text=\"{Binding}\" TextWrapping=\"Wrap\" /></DataTemplate>");
        var checkBox = new CheckBox
        {
            Style = (Style)Application.Current.FindResource("IrisSwitchCheckBox"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ContentTemplate = wrappingTemplate,
            Content = "a long enough sentence to wrap onto a second line inside the row",
        };
        var host = new Window { Content = checkBox, Width = 200, Height = 80, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            host.Show();
            host.UpdateLayout();
            var presenter = (ContentPresenter)checkBox.Template.FindName("ContentPresenter", checkBox);
            presenter.ActualWidth.ShouldBeGreaterThan(40);
            presenter.ContentTemplate.ShouldBeSameAs(wrappingTemplate);
        }
        finally
        {
            host.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task SwitchStyle_OffStateUsesTheOffStateKeys() => WpfTestRunner.RunAsync(() =>
    {
        // 这三个键写在 ControlTemplate **体内**元素的属性上（Iris.Styles.xaml:96/97/99），不在任何
        // Setter.Value 上，所以 IrisStyleKeyResolutionTests 的遍历到不了它们：把键名打错，那条守卫
        // 照样全绿，而关态开关会变成没有底槽、没有描边的透明块。这里从消费端断言，顺便锁住"用的是
        // OFF 那一组键"而不是 ON 那一组。
        var checkBox = new CheckBox
        {
            Style = (Style)Application.Current.FindResource("IrisSwitchCheckBox"),
            IsChecked = false,
        };
        var host = new Window { Content = checkBox, Width = 120, Height = 60, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            host.Show();
            host.UpdateLayout();
            var track = (System.Windows.Shapes.Rectangle)checkBox.Template.FindName("ToggleRectangle", checkBox);
            var knob = (System.Windows.Shapes.Rectangle)checkBox.Template.FindName("ToggleKnob", checkBox);
            track.Fill.ShouldBeOfType<SolidColorBrush>().Color.ShouldBe(BrushColor("ToggleSwitchFillOff"));
            track.Stroke.ShouldBeOfType<SolidColorBrush>().Color.ShouldBe(BrushColor("ToggleSwitchStrokeOff"));
            knob.Fill.ShouldBeOfType<SolidColorBrush>().Color.ShouldBe(BrushColor("ToggleSwitchKnobFillOff"));
        }
        finally
        {
            host.Close();
        }

        return Task.CompletedTask;
    });

    // 拿已合并字典里那个键的值当基准，而不是写死颜色：断言的是"这个属性由那个键驱动"，
    // 颜色改了不用动测试。键名打错时 FindResource 会直接抛，比断 null 更响亮。
    private static Color BrushColor(string key) =>
        Application.Current.FindResource(key).ShouldBeOfType<SolidColorBrush>().Color;
}
```

（`checkBox.IsChecked = !isChecked;` 那行是**先设相反值再翻转**。不这么做的话 `isChecked=false` 那一支是空操作——`CheckBox` 默认就是 `false`——那个分支就测不到"触发器退出后立即渲染"这条路径，而本用例的全部意义正在这里。）

第二个用例 `SwitchStyle_KeepsWrappingContentTemplate` 守的是换掉 `ControlTemplate` 时最容易弄丢的东西：**调用方自己给的 `ContentTemplate`**。`SettingsWindow.xaml:16` 的 `WrappingCheckBoxContent` 是窗口级资源，测试里取不到，所以就地用 `XamlReader.Parse` 解析一个等价模板（模板从哪来与结论无关）。它断言 `ContentPresenter` 确实把该模板透传下去（`ContentTemplate` 是同一个实例），并且 `ActualWidth > 40`——内容真的在里面排开并折行。

第三个用例 `SwitchStyle_OffStateUsesTheOffStateKeys` 补的是一条**通用守卫覆盖不到的地方**：关态底槽的填充、底槽的描边、滑块的填充这三个键写在 `ControlTemplate` **体内**元素的属性上（`Iris.Styles.xaml:96/97/99`），`IrisStyleKeyResolutionTests` 那种"遍历 Style 对象图"的写法收不到它们（模板体内的引用在 BAML 延迟内容里，实例化前不是 `DynamicResourceExtension`）。所以在这里从消费端断言，顺便锁住用的是 OFF 那一组键而不是 ON 那一组。将来往任何模板体里加 `{DynamicResource}`，都要照这样补一条消费端断言。

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~IrisSwitchStyleTests"`
Expected: 2 个用例 FAIL —— `Application.Current.FindResource("IrisSwitchCheckBox")` 抛 `ResourceReferenceKeyNotFoundException`。

- [ ] **Step 3: 加样式**

在 `Iris.Styles.xaml` 的 `IrisNavItem` 之前插入。**这是实测通过的模板**（探测工程 `ProbeToggleBasedOn`），只改了样式键名与注释：

```xml
    <!-- Switch-looking CheckBox. The control type stays CheckBox; only the template changes.
         Setters are static (no Storyboard) so an offscreen render taken right after IsChecked
         flips shows the new state; the cost is that the switch snaps instead of sliding.
         BasedOn keeps the stock focus visual and hand cursor. MinWidth/MinHeight undo the stock
         120x32 floor; Padding is zeroed defensively (this template never reads it).
         Three-state is not supported: an indeterminate IsChecked renders exactly like unchecked,
         so do not bind this style to a bool?. -->
    <Style x:Key="IrisSwitchCheckBox" TargetType="{x:Type CheckBox}" BasedOn="{StaticResource {x:Type CheckBox}}">
        <Setter Property="MinWidth" Value="0" />
        <Setter Property="MinHeight" Value="0" />
        <Setter Property="Padding" Value="0" />
        <Setter Property="Background" Value="{DynamicResource ToggleSwitchFillOn}" />
        <Setter Property="Foreground" Value="{DynamicResource ToggleSwitchContentForeground}" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="{x:Type CheckBox}">
                    <Grid Background="Transparent">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="Auto" />
                            <ColumnDefinition Width="*" />
                        </Grid.ColumnDefinitions>
                        <Grid x:Name="ToggleGrid" Grid.Column="0" Width="40" Height="20">
                            <Rectangle x:Name="ToggleRectangle" Width="40" Height="20" HorizontalAlignment="Center" VerticalAlignment="Center"
                                       Fill="{DynamicResource ToggleSwitchFillOff}" RadiusX="10" RadiusY="10"
                                       Stroke="{DynamicResource ToggleSwitchStrokeOff}" StrokeThickness="1" />
                            <Rectangle x:Name="ToggleKnob" Width="12" Height="12" HorizontalAlignment="Center" VerticalAlignment="Center"
                                       Fill="{DynamicResource ToggleSwitchKnobFillOff}" RadiusX="7" RadiusY="7" StrokeThickness="0">
                                <Rectangle.RenderTransform>
                                    <TranslateTransform X="-9" />
                                </Rectangle.RenderTransform>
                            </Rectangle>
                        </Grid>
                        <ContentPresenter x:Name="ContentPresenter" Grid.Column="1" Margin="8,0,0,0"
                                          HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                          VerticalAlignment="{TemplateBinding VerticalContentAlignment}"
                                          RecognizesAccessKey="True"
                                          TextElement.Foreground="{TemplateBinding Foreground}" />
                    </Grid>
                    <ControlTemplate.Triggers>
                        <Trigger Property="Content" Value="{x:Null}">
                            <Setter TargetName="ContentPresenter" Property="Margin" Value="0" />
                        </Trigger>
                        <Trigger Property="Content" Value="">
                            <Setter TargetName="ContentPresenter" Property="Margin" Value="0" />
                        </Trigger>
                        <Trigger Property="IsChecked" Value="True">
                            <Setter TargetName="ToggleRectangle" Property="Fill" Value="{Binding Background, RelativeSource={RelativeSource TemplatedParent}}" />
                            <Setter TargetName="ToggleRectangle" Property="StrokeThickness" Value="0" />
                            <Setter TargetName="ToggleKnob" Property="Fill" Value="{DynamicResource ToggleSwitchKnobFillOn}" />
                            <Setter TargetName="ToggleKnob" Property="RenderTransform">
                                <Setter.Value>
                                    <TranslateTransform X="9" />
                                </Setter.Value>
                            </Setter>
                        </Trigger>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="ToggleKnob" Property="Height" Value="14" />
                            <Setter TargetName="ToggleKnob" Property="Width" Value="14" />
                        </Trigger>
                        <MultiTrigger>
                            <MultiTrigger.Conditions>
                                <Condition Property="IsMouseOver" Value="True" />
                                <Condition Property="IsPressed" Value="False" />
                                <Condition Property="IsChecked" Value="False" />
                                <Condition Property="IsEnabled" Value="True" />
                            </MultiTrigger.Conditions>
                            <Setter TargetName="ToggleRectangle" Property="Fill" Value="{DynamicResource ToggleSwitchFillOffPointerOver}" />
                            <Setter TargetName="ToggleRectangle" Property="Stroke" Value="{DynamicResource ToggleSwitchStrokeOffPointerOver}" />
                            <Setter TargetName="ToggleKnob" Property="Fill" Value="{DynamicResource ToggleSwitchKnobFillOffPointerOver}" />
                        </MultiTrigger>
                        <MultiTrigger>
                            <MultiTrigger.Conditions>
                                <Condition Property="IsMouseOver" Value="True" />
                                <Condition Property="IsChecked" Value="True" />
                                <Condition Property="IsEnabled" Value="True" />
                            </MultiTrigger.Conditions>
                            <Setter TargetName="ToggleRectangle" Property="Fill" Value="{DynamicResource ToggleSwitchFillOnPointerOver}" />
                            <Setter TargetName="ToggleKnob" Property="Fill" Value="{DynamicResource ToggleSwitchKnobFillOnPointerOver}" />
                        </MultiTrigger>
                        <!-- Pressed states. Without these the OFF hover highlight above drops away
                             on mouse-down (its IsPressed=False condition stops matching), which reads
                             as the control going dead mid-click. -->
                        <MultiTrigger>
                            <MultiTrigger.Conditions>
                                <Condition Property="IsPressed" Value="True" />
                                <Condition Property="IsChecked" Value="False" />
                                <Condition Property="IsEnabled" Value="True" />
                            </MultiTrigger.Conditions>
                            <Setter TargetName="ToggleRectangle" Property="Fill" Value="{DynamicResource ToggleSwitchFillOffPressed}" />
                            <Setter TargetName="ToggleRectangle" Property="Stroke" Value="{DynamicResource ToggleSwitchStrokeOffPressed}" />
                            <Setter TargetName="ToggleKnob" Property="Fill" Value="{DynamicResource ToggleSwitchKnobFillOffPressed}" />
                        </MultiTrigger>
                        <MultiTrigger>
                            <MultiTrigger.Conditions>
                                <Condition Property="IsPressed" Value="True" />
                                <Condition Property="IsChecked" Value="True" />
                                <Condition Property="IsEnabled" Value="True" />
                            </MultiTrigger.Conditions>
                            <Setter TargetName="ToggleRectangle" Property="Fill" Value="{DynamicResource ToggleSwitchFillOnPressed}" />
                            <Setter TargetName="ToggleKnob" Property="Fill" Value="{DynamicResource ToggleSwitchKnobFillOnPressed}" />
                        </MultiTrigger>
                        <MultiTrigger>
                            <MultiTrigger.Conditions>
                                <Condition Property="IsChecked" Value="False" />
                                <Condition Property="IsEnabled" Value="False" />
                            </MultiTrigger.Conditions>
                            <Setter TargetName="ToggleRectangle" Property="Stroke" Value="{DynamicResource ToggleSwitchStrokeOffDisabled}" />
                            <Setter TargetName="ToggleRectangle" Property="Fill" Value="{DynamicResource ToggleSwitchFillOffDisabled}" />
                            <Setter TargetName="ToggleKnob" Property="Fill" Value="{DynamicResource ToggleSwitchKnobFillOffDisabled}" />
                            <Setter Property="Foreground" Value="{DynamicResource ToggleSwitchContentForegroundDisabled}" />
                        </MultiTrigger>
                        <MultiTrigger>
                            <MultiTrigger.Conditions>
                                <Condition Property="IsChecked" Value="True" />
                                <Condition Property="IsEnabled" Value="False" />
                            </MultiTrigger.Conditions>
                            <Setter TargetName="ToggleRectangle" Property="Fill" Value="{DynamicResource ToggleSwitchFillOnDisabled}" />
                            <Setter TargetName="ToggleKnob" Property="Fill" Value="{DynamicResource ToggleSwitchKnobFillOnDisabled}" />
                            <Setter Property="Foreground" Value="{DynamicResource ToggleSwitchContentForegroundDisabled}" />
                        </MultiTrigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~IrisSwitchStyleTests"`
Expected: 2 个用例全部 PASS。

- [ ] **Step 5: 加一条“换行内容不被模板破坏”的测试**

在同一个测试类里加（守住第 11 条实测事实：自定义模板仍尊重调用方的 `ContentTemplate` 与 `HorizontalContentAlignment`）：

```csharp
    [Fact]
    public Task SwitchStyle_KeepsWrappingContentTemplate() => WpfTestRunner.RunAsync(() =>
    {
        // SettingsWindow.xaml:16 的 WrappingCheckBoxContent 是**窗口级**资源，全局取不到，
        // 所以这里就地解析一个与它等价的模板。要验证的是自定义 ControlTemplate 是否仍然尊重
        // 调用方给的 ContentTemplate，与模板从哪来无关。
        var wrappingTemplate = (DataTemplate)XamlReader.Parse(
            "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><TextBlock Text=\"{Binding}\" TextWrapping=\"Wrap\" /></DataTemplate>");
        var checkBox = new CheckBox
        {
            Style = (Style)Application.Current.FindResource("IrisSwitchCheckBox"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ContentTemplate = wrappingTemplate,
            Content = "a long enough sentence to wrap onto a second line inside the row",
        };
        var host = new Window { Content = checkBox, Width = 200, Height = 80, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            host.Show();
            host.UpdateLayout();
            var presenter = (ContentPresenter)checkBox.Template.FindName("ContentPresenter", checkBox);
            presenter.ActualWidth.ShouldBeGreaterThan(40);
            presenter.ContentTemplate.ShouldBeSameAs(wrappingTemplate);
        }
        finally
        {
            host.Close();
        }

        return Task.CompletedTask;
    });
```

（`XamlReader` 在 `System.Windows.Markup` 下，需要 `using System.Windows.Markup;`。**不要**去动 `SettingsWindow.xaml`。）

Run: `dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~IrisSwitchStyleTests"`
Expected: 3 个用例全部 PASS。

（`presenter.ContentTemplate.ShouldBeSameAs(wrappingTemplate);` 钉的是"自定义 `ControlTemplate` 仍把调用方的 `ContentTemplate` 交给 `ContentPresenter`"。**不要**改回去断言 `LoadContent()` 出来的那个 `TextBlock` 的 `TextWrapping`——那段模板字符串是测试自己写的，恒真，属于自证。上一行 `ActualWidth.ShouldBeGreaterThan(40)` 是真的在验证 `HorizontalContentAlignment=Stretch` 被尊重，保留。）

- [ ] **Step 6: 暂存改动（本阶段不提交）**

```bash
git add src/Iris.App/Themes/Iris.Styles.xaml tests/Iris.App.Tests/Themes/IrisSwitchStyleTests.cs
# 设计 §10 与工单第 23 行：每个阶段只有一个提交，见 Task 10 Step 6。此处只暂存，不提交。
# git commit -m "Add a switch-looking CheckBox style with a static template"
```

---

### Task 3: 图标按钮样式

**Files:**
- Modify: `src/Iris.App/Themes/Iris.Styles.xaml`
- Test: `tests/Iris.App.Tests/Themes/IrisIconButtonStyleTests.cs`

- [ ] **Step 1: 写失败测试**

`tests/Iris.App.Tests/Themes/IrisIconButtonStyleTests.cs`：

```csharp
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Iris.App.Tests.TestSupport;
using Shouldly;
using Wpf.Ui.Controls;
using Xunit;

namespace Iris.App.Tests.Themes;

[Collection("IrisThemeResources")]
public sealed class IrisIconButtonStyleTests
{
    [Fact]
    public Task IconButton_RendersIconOnlyAndKeepsStringContent() => WpfTestRunner.RunAsync(async () =>
    {
        var icon = new SymbolIcon { Symbol = SymbolRegular.Copy24 };
        var button = new Wpf.Ui.Controls.Button
        {
            Style = (Style)Application.Current.FindResource("IrisIconButton"),
            Content = "CopySourceFull",
            Icon = icon,
        };
        var host = new Window { Content = button, Width = 200, Height = 100, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            host.Show();
            host.UpdateLayout();
            button.Content.ShouldBe("CopySourceFull");
            button.ToolTip.ShouldBe("CopySourceFull");
            AutomationProperties.GetName(button).ShouldBe("CopySourceFull");
            button.ActualWidth.ShouldBe(32d);
            button.ActualHeight.ShouldBe(32d);
            // 契约是"界面只画 Icon"。少了下面这几条，把 Template 换回默认的（会画 Content 文字）
            // 也照样能过——那时按钮里会出现字符串，而这条测试必须挡住它。
            // TextBlock 必须写全限定名：本文件同时 using 了 Wpf.Ui.Controls 与 System.Windows.Controls，
            // 两个命名空间都有 TextBlock，裸写会 CS0104 编译失败。写成基类 System.Windows.Controls.TextBlock
            // 同时也是更宽的覆盖——默认模板渲染字符串生成的就是它。
            var presenter = button.Template.FindName("ControlIcon", button).ShouldBeOfType<ContentPresenter>();
            presenter.Content.ShouldBeSameAs(icon);
            presenter.ActualWidth.ShouldBeGreaterThan(0d);
            // 下面这条防空转断言不能删：ShouldNotContain 只有在树里确实存在 TextBlock 时才验得动
            // （默认模板渲染字符串生成的正是它）。若 WPF-UI 哪天换掉文本画法，ShouldNotContain 会
            // 静默恒真、测试照样绿；这条会报红，逼人更新测试，而不是让守护悄悄失效。
            Descendants<System.Windows.Controls.TextBlock>(button).ShouldNotBeEmpty();
            Descendants<System.Windows.Controls.TextBlock>(button).ShouldNotContain(text => text.Text == "CopySourceFull");
            var clickCount = 0;
            button.Click += (_, _) => clickCount++;
            var peer = new ButtonAutomationPeer(button);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            // ButtonAutomationPeer.Invoke() 用 Dispatcher.BeginInvoke(Input) 异步派发 Click；
            // 这里必须先把队列里优先级高于 Background 的操作跑完，否则断言读到的是旧值。
            await button.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            clickCount.ShouldBe(1);
            // 这条守护第一轮修掉的那个 bug：按下反馈必须只以 IsPressed 为条件。改回
            // IsMouseOver+IsPressed 的话，Tab 聚焦后按空格就没有任何按下反馈，
            // 而上面所有断言仍然全绿——所以必须直接断模板结构。
            button.Template.Triggers.OfType<Trigger>().Any(t => t.Property == ButtonBase.IsPressedProperty).ShouldBeTrue();
        }
        finally
        {
            host.Close();
        }
    });

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T value) yield return value;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~IrisIconButtonStyleTests"`
Expected: FAIL，`IrisIconButton` 未找到。

- [ ] **Step 3: 加样式**

在 `Iris.Styles.xaml` 的 `IrisSwitchCheckBox` 之后插入。**这是实测通过的模板**（探测工程 `ProbeIconButtonStyle`）：`Content` 保持字符串作为辅助名称与测试定位依据，界面只渲染 `Icon`，鼠标提示取 `Content`。

```xml
    <!-- Icon-only ui:Button. Content stays a string (accessible name, tooltip and test anchor);
         only Icon is rendered. Callers set Icon="{ui:SymbolIcon SymbolRegular.Copy24}".
         No Appearance setter: Appearance is only consumed by the base style's template triggers,
         and this style replaces the template. Background stays Transparent on purpose - a null
         background would leave only the glyph hit-testable instead of the whole 32x32 square. -->
    <Style x:Key="IrisIconButton" TargetType="{x:Type ui:Button}" BasedOn="{StaticResource {x:Type ui:Button}}">
        <Setter Property="Background" Value="Transparent" />
        <Setter Property="BorderBrush" Value="Transparent" />
        <Setter Property="BorderThickness" Value="0" />
        <Setter Property="MouseOverBackground" Value="{DynamicResource SubtleFillColorSecondaryBrush}" />
        <Setter Property="PressedBackground" Value="{DynamicResource SubtleFillColorTertiaryBrush}" />
        <Setter Property="Foreground" Value="{DynamicResource TextFillColorPrimaryBrush}" />
        <Setter Property="PressedForeground" Value="{DynamicResource TextFillColorSecondaryBrush}" />
        <Setter Property="Padding" Value="0" />
        <Setter Property="Width" Value="32" />
        <Setter Property="Height" Value="32" />
        <Setter Property="ToolTip" Value="{Binding Content, RelativeSource={RelativeSource Self}}" />
        <Setter Property="AutomationProperties.Name" Value="{Binding Content, RelativeSource={RelativeSource Self}}" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="{x:Type ui:Button}">
                    <Border x:Name="ContentBorder"
                            Padding="{TemplateBinding Padding}"
                            Background="{TemplateBinding Background}"
                            BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}"
                            CornerRadius="{TemplateBinding CornerRadius}">
                        <ContentPresenter x:Name="ControlIcon"
                                          HorizontalAlignment="Center"
                                          VerticalAlignment="Center"
                                          Content="{TemplateBinding Icon}"
                                          Focusable="False"
                                          TextElement.Foreground="{TemplateBinding Foreground}" />
                    </Border>
                    <ControlTemplate.Triggers>
                        <MultiTrigger>
                            <MultiTrigger.Conditions>
                                <Condition Property="IsMouseOver" Value="True" />
                                <Condition Property="IsPressed" Value="False" />
                            </MultiTrigger.Conditions>
                            <Setter TargetName="ContentBorder" Property="Background" Value="{Binding MouseOverBackground, RelativeSource={RelativeSource TemplatedParent}}" />
                        </MultiTrigger>
                        <!-- Keyed on IsPressed alone, not on IsMouseOver+IsPressed: pressing Space
                             on a focused button sets IsPressed with no pointer over it, and that
                             must look pressed too. (Enter does not: ButtonBase.OnKeyDown calls
                             SetIsPressed(false) then OnClick.) Mutually exclusive with the hover
                             trigger above, so the two never fight over Background. -->
                        <Trigger Property="IsPressed" Value="True">
                            <Setter TargetName="ContentBorder" Property="Background" Value="{Binding PressedBackground, RelativeSource={RelativeSource TemplatedParent}}" />
                            <Setter Property="Foreground" Value="{Binding PressedForeground, RelativeSource={RelativeSource TemplatedParent}}" />
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter Property="Foreground" Value="{DynamicResource TextFillColorDisabledBrush}" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
```

注意：探测工程里 `AutomationName == Content` 是靠调用方设 `AutomationProperties.Name` 得到的，本计划把它写进样式的 setter，等价且不必每个调用点重复。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~IrisIconButtonStyleTests"`
Expected: PASS。两处实测结论（2026-11-12 实现子代理在本机取得；不要凭猜改动）：
- `AutomationProperties.Name` 的 `{Binding Content, RelativeSource=Self}` 写在 Style setter 里**是生效的**（WPF-UI 4.3.0 + net10.0），**不需要**改成 `Border` 上的 `{TemplateBinding Content}`。
- `ButtonAutomationPeer.Invoke()` 内部是 `Dispatcher.BeginInvoke(DispatcherPriority.Input, …)`，**异步**派发；`WpfTestRunner.RunAsync` 在同一个 dispatcher 回调里同步跑完测试体，所以 `Invoke()` 之后必须 `await …InvokeAsync(() => { }, DispatcherPriority.Background)` 才看得到 `Click`（这就是 Step 1 里那一行的原因，`Background` 低于 `Input` 因而安全，不要删、不要改成 `Normal`）。

- [ ] **Step 5: 暂存改动（本阶段不提交）**

```bash
git add src/Iris.App/Themes/Iris.Styles.xaml tests/Iris.App.Tests/Themes/IrisIconButtonStyleTests.cs
# 设计 §10 与工单第 23 行：每个阶段只有一个提交，见 Task 10 Step 6。此处只暂存，不提交。
# git commit -m "Add an icon-only button style that keeps the string Content"
```

---

### Task 4: 自有颜色键与纯色回退

**Files:**
- Create: `src/Iris.App/Themes/Iris.Light.xaml`
- Create: `src/Iris.App/Themes/Iris.Dark.xaml`
- Create: `src/Iris.App/Themes/Iris.SolidFallback.xaml`
- Modify: `src/Iris.App/App.xaml`
- Modify: `tests/Iris.App.Tests/TestSupport/WpfTestRunner.cs`（`AddIrisDictionaries`）
- Test: `tests/Iris.App.Tests/Themes/IrisColorKeyTests.cs`

只有 WPF-UI 没有的键放这里。四张卡面画刷就是全部：WPF-UI 的 `CardBackgroundFillColorDefaultBrush` 是给 Mica 窗口设计的，深色下只有 `#0DFFFFFF`，压在亚克力上偏薄，所以自有四个键，值按主题给。

- [ ] **Step 1: 写测试**

`tests/Iris.App.Tests/Themes/IrisColorKeyTests.cs`：

```csharp
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Iris.App.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Themes;

[Collection("IrisThemeResources")]
public sealed class IrisColorKeyTests
{
    [Theory]
    [InlineData("IrisAcrylicCardFillBrush")]
    [InlineData("IrisAcrylicCardStrokeBrush")]
    [InlineData("IrisAcrylicRowFillBrush")]
    [InlineData("IrisAcrylicRowStrokeBrush")]
    public Task IrisColorKeys_AreTranslucent(string key) => WpfTestRunner.RunAsync(() =>
    {
        var light = Application.Current.FindResource(key).ShouldBeOfType<SolidColorBrush>();
        light.Color.A.ShouldBeLessThan((byte)255);
        // 深色字典也必须自己验值，不能只靠下面那条"键名与数量"的覆盖：把 Iris.Dark.xaml 里某个键
        // 改成不透明，或 alpha 打错一位（26 写成 13），9 个用例会全绿。
        var dark = new ResourceDictionary { Source = new Uri("pack://application:,,,/Iris.App;component/Themes/Iris.Dark.xaml", UriKind.Absolute) };
        dark[key].ShouldBeOfType<SolidColorBrush>().Color.A.ShouldBeLessThan((byte)255);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("IrisAcrylicCardFillBrush")]
    [InlineData("IrisAcrylicCardStrokeBrush")]
    [InlineData("IrisAcrylicRowFillBrush")]
    [InlineData("IrisAcrylicRowStrokeBrush")]
    public Task SolidFallbackKeys_AreOpaque(string key) => WpfTestRunner.RunAsync(() =>
    {
        var fallback = new ResourceDictionary { Source = new Uri("pack://application:,,,/Iris.App;component/Themes/Iris.SolidFallback.xaml", UriKind.Absolute) };
        var brush = fallback[key].ShouldBeOfType<SolidColorBrush>();
        brush.Color.A.ShouldBe((byte)255);
        return Task.CompletedTask;
    });

    [Fact]
    public Task SolidFallback_OverridesEveryIrisColorKey() => WpfTestRunner.RunAsync(() =>
    {
        var light = new ResourceDictionary { Source = new Uri("pack://application:,,,/Iris.App;component/Themes/Iris.Light.xaml", UriKind.Absolute) };
        var dark = new ResourceDictionary { Source = new Uri("pack://application:,,,/Iris.App;component/Themes/Iris.Dark.xaml", UriKind.Absolute) };
        var fallback = new ResourceDictionary { Source = new Uri("pack://application:,,,/Iris.App;component/Themes/Iris.SolidFallback.xaml", UriKind.Absolute) };
        // Source 加载的字典取 Keys 得到的是基础字典的快照（ResourceDictionary.Keys 的 Remarks）。
        // 若快照为空，下面的循环会一条都不跑而假通过，所以先断非空——这两行不能删。
        var lightKeys = light.Keys.Cast<object>().ToList();
        var darkKeys = dark.Keys.Cast<object>().ToList();
        lightKeys.ShouldNotBeEmpty();
        darkKeys.ShouldNotBeEmpty();
        lightKeys.Count.ShouldBe(darkKeys.Count);
        // 数量相等本身不够——两份字典键名不同但数量相同也能过。这里显式断言"深色的每个键名
        // 浅色也有"（配上数量相等即集合相等）。下面 fallback.Keys.Count 那条与两个 foreach
        // 连在一起其实也恰好能推出集合相等，但那个保证藏在三条断言之间、读代码看不出来，
        // 删掉任意一条就静默消失，所以这里把它写明白。
        foreach (var key in darkKeys)
        {
            lightKeys.ShouldContain(key);
        }

        foreach (var key in lightKeys)
        {
            fallback.Contains(key).ShouldBeTrue($"{key} is missing from Iris.SolidFallback.xaml");
        }

        foreach (var key in darkKeys)
        {
            fallback.Contains(key).ShouldBeTrue($"{key} is missing from Iris.SolidFallback.xaml");
        }

        fallback.Keys.Count.ShouldBe(lightKeys.Count);
        return Task.CompletedTask;
    });
}
```

（三个用例都包在 `WpfTestRunner.RunAsync` 里，因为 `pack://application:,,,` 这种 URI 要靠一个已存在的 `Application` 才能解析；测试方法返回 `Task`，不要写成 `void`——`TreatWarningsAsErrors` 下 `CS4014` 是错误。）

**唯一被授权的判断点（用测试结果决定，不要凭猜）**：`ResourceDictionary.Keys` 的文档说它返回"base dictionary 中键的快照"，Source 加载的字典理应可用；但若本机实测 `light.Keys` 抛异常或为空（表现为 `lightKeys.ShouldNotBeEmpty()` 失败），就把两个 `foreach` 改成对上面两个 theory 的那四个键逐个断言 `fallback.Contains(key).ShouldBeTrue(...)`，并删掉 `lightKeys`/`darkKeys`/集合相等/`fallback.Keys` 这四组断言（此时"每个 Iris 颜色键都有回退"这条性质退化为"这四个键都有回退"，要在报告里写清）。**必须在报告里贴出改动前的真实异常/失败输出。**

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~IrisColorKeyTests"`
Expected: 全部 FAIL（三个字典文件还不存在）。

- [ ] **Step 3: 建 `Iris.Light.xaml`**

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <!-- The only colours Iris owns. They sit on top of the acrylic backdrop, so they are
         translucent. Iris.SolidFallback.xaml holds opaque equivalents for systems without
         acrylic, but nothing merges it yet - phase 1 only creates it (see Iris.Styles.xaml). -->
    <SolidColorBrush x:Key="IrisAcrylicCardFillBrush" Color="#66FFFFFF" />
    <SolidColorBrush x:Key="IrisAcrylicCardStrokeBrush" Color="#14000000" />
    <SolidColorBrush x:Key="IrisAcrylicRowFillBrush" Color="#4DFFFFFF" />
    <SolidColorBrush x:Key="IrisAcrylicRowStrokeBrush" Color="#0F000000" />
</ResourceDictionary>
```

- [ ] **Step 4: 建 `Iris.Dark.xaml`**

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <SolidColorBrush x:Key="IrisAcrylicCardFillBrush" Color="#1AFFFFFF" />
    <SolidColorBrush x:Key="IrisAcrylicCardStrokeBrush" Color="#1FFFFFFF" />
    <SolidColorBrush x:Key="IrisAcrylicRowFillBrush" Color="#14FFFFFF" />
    <SolidColorBrush x:Key="IrisAcrylicRowStrokeBrush" Color="#19FFFFFF" />
</ResourceDictionary>
```

- [ ] **Step 5: 建 `Iris.SolidFallback.xaml`**

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <!-- Opaque equivalents of the IrisAcrylic* keys, so the cards keep their separation on a flat
         background.
         Not merged in phase 1: nothing wires this dictionary up yet, and it holds one set of values
         only - dark theme without acrylic would render white cards, while TextFillColorPrimaryBrush
         is white in dark theme, so the card text would disappear along with the row descriptions
         (design section 3.2 asks for a single merged dictionary; not merging it in phase 1 is
         recorded as a deviation in the phase report). A Dark variant has to land before this
         dictionary is merged, and the merge must come AFTER the theme dictionary: later entries in
         MergedDictionaries win, and these values must win over the translucent set.
         The two strokes are alpha 31 and alpha 26 composited over white
         (#1F000000 -> #E0E0E0, #1A000000 -> #E5E5E5), deliberately stronger than the light acrylic
         set (#14000000 -> #EBEBEB, #0F000000 -> #F0F0F0), for the same reason that set is faint:
         a 1px line disappears against a flat fill. -->
    <SolidColorBrush x:Key="IrisAcrylicCardFillBrush" Color="#FFFFFFFF" />
    <SolidColorBrush x:Key="IrisAcrylicCardStrokeBrush" Color="#FFE0E0E0" />
    <SolidColorBrush x:Key="IrisAcrylicRowFillBrush" Color="#FFF9F9F9" />
    <SolidColorBrush x:Key="IrisAcrylicRowStrokeBrush" Color="#FFE5E5E5" />
</ResourceDictionary>
```

- [ ] **Step 6: 在 `App.xaml` 与测试宿主两处合并浅色字典**

`App.xaml`：在 `ui:ControlsDictionary` 与 `Themes/Iris.Styles.xaml` 之间加一行：

```xml
                <ResourceDictionary Source="Themes/Iris.Light.xaml" />
```

（`Iris.Dark.xaml` 不在 `App.xaml` 里合并——它由 `ThemeService` 在切主题时替换，见 Task 9。`Iris.SolidFallback.xaml` **本阶段只建不合并**：它只有一套（浅色）值，深色主题 + 无亚克力时纯白卡面配白色文字会把卡面文字整个吃掉，所以合并要等 Dark 变体落地，作为阶段 2 的准入条件；这与设计 §3.2「不支持亚克力时覆盖成不透明色」有偏差，已记入 Task 10 报告第 20 条，Task 8 不合并它。）

测试宿主同理，**测试宿主从不加载 `App.xaml`**（见 Task 1 Step 1），所以这里要手动补上，插在 `Iris.Styles.xaml` **之前**（与 `App.xaml` 的顺序一致），即在 Task 1 建的 `AddIrisDictionaries` 里加：

```csharp
        dictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Iris.App;component/Themes/Iris.Light.xaml", UriKind.Absolute)
        });
```

本步做完后 `AddIrisDictionaries` 里应是 `Iris.Light.xaml`、`Iris.Styles.xaml` 两项。

- [ ] **Step 7: 跑测试确认通过**

Run: `dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~IrisColorKeyTests"`
Expected: 9 个用例全部 PASS。

- [ ] **Step 8: 暂存改动（本阶段不提交）**

```bash
git add src/Iris.App/Themes/Iris.Light.xaml src/Iris.App/Themes/Iris.Dark.xaml src/Iris.App/Themes/Iris.SolidFallback.xaml src/Iris.App/App.xaml tests/Iris.App.Tests/Themes/IrisColorKeyTests.cs tests/Iris.App.Tests/TestSupport/WpfTestRunner.cs
# 设计 §10 与工单第 23 行：每个阶段只有一个提交，见 Task 10 Step 6。此处只暂存，不提交。
# git commit -m "Add Iris-owned colour keys, their dark set and the opaque fallback"
```

---

### Task 5: 测试资源环境核对与全量回归

**Files:**
- Create: `tests/Iris.App.Tests/Themes/IrisStyleKeyResolutionTests.cs`
- Modify: `tests/Iris.App.Tests/Updates/UpdateRecoveryRenderingTests.cs:36-48`

- [ ] **Step 1: 核对接线（这一步不写代码，只核对）**

测试宿主的接线**已经**在 Task 1 Step 1（`Themes/Iris.Styles.xaml`）与 Task 4 Step 6（`Themes/Iris.Light.xaml`）做完了。本步只核对 `tests/Iris.App.Tests/TestSupport/WpfTestRunner.cs` 里的 `AddIrisDictionaries` 是不是正好两项、顺序是 `Iris.Light.xaml` → `Iris.Styles.xaml`（与 `App.xaml` 一致），以及 `EnsureApplication` 里是在 `ControlsDictionary` 之后、`EditingMenus.xaml` 之前调用它。**若已经正确，不要改动这个文件。** 需要改的话，改完跑一次 `dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~Themes"` 确认没坏。

这一步存在的意义是：**测试宿主从不加载 `App.xaml`**，所以 `App.xaml` 里合并了什么，测试宿主里就得手动补什么。后面每一个新窗口、新样式测试都靠它才能拿到 Iris 资源；漏掉一项的表现是所有相关测试一起报 "resource not found"。

- [ ] **Step 2: 新增一条"键名解析"守卫测试**

**这一步是评审驱动的补充，不在原工单里，必须写进阶段报告的偏差清单。**

背景：`{DynamicResource X}` 的键名写错是**静默失败**——不报错、值就是 null，界面少一个颜色而所有测试全绿。`Iris.Styles.xaml` 现在用了 29 个键（27 个 WPF-UI 内置 + 2 个 Iris 自有），评审已逐个在运行时验证**当前全部可解析**，但**没有任何自动化守卫**：把 `IrisAcrylicCardFillBrush` 打成 `...FillBush`、或把 `ToggleSwitchKnobFillOn` 打错一个字母，测试仍然全绿，要到阶段 2 的窗口截图才可能发现。

这个守卫有一条**边界**，写在它的注释里：它只收 `Setter.Value` 上的键（含各类触发器里的 Setter），模板**体内**元素属性上的引用收不到——本文件里正是 `ToggleSwitchFillOff` / `ToggleSwitchStrokeOff` / `ToggleSwitchKnobFillOff` 这三个（`Iris.Styles.xaml:96/97/99`）。所以 29 个键里它收 26 个，剩下 3 个由 `IrisSwitchStyleTests.SwitchStyle_OffStateUsesTheOffStateKeys` 从消费端断言（Task 2）。两者合起来才是全部覆盖。

它同时补上另一个盲点：`Iris.Styles.xaml` 里 `{DynamicResource}` 的**键名**只被消费在样式里，而既有 9 个样式测试断言的是**渲染结果**，两者之间没有交叉校验。

`tests/Iris.App.Tests/Themes/IrisStyleKeyResolutionTests.cs`：

```csharp
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Iris.App.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Themes;

[Collection("IrisThemeResources")]
public sealed class IrisStyleKeyResolutionTests
{
    // 从已加载的 Style 对象图里挖出每一个写在 Setter 上的 {DynamicResource X} 键（覆盖边界见下），
    // 再逐个问 Application 解不解得开。
    // 不读源码文本：页面编译成 BAML，GetResourceStream 拿到的是 BAML 流而不是 XAML 文本，
    // 在测试里对源码做正则解析做不到。
    //
    // **覆盖边界（别把它当"整份文件全覆盖"）**：只到 Setter.Value 为止——Style.Setters、
    // Style.Triggers，以及 ControlTemplate.Triggers 里带/不带 TargetName 的 Setter 都收；
    // 写在模板**体内**元素属性上的引用收不到（模板实例化前它们在 BAML 的延迟内容里），
    // 本文件里就是这样三处：Iris.Styles.xaml:96/97/99 的 ToggleSwitchFillOff /
    // ToggleSwitchStrokeOff / ToggleSwitchKnobFillOff。那三个键由
    // IrisSwitchStyleTests.SwitchStyle_OffStateUsesTheOffStateKeys 从消费端显式断言。
    // 于是本守卫当前覆盖 29 个键里的 26 个；将来谁往模板体里加引用，要照上面那条补断言。
    [Fact]
    public Task EveryDynamicResourceKeyUsedByIrisStylesResolves() => WpfTestRunner.RunAsync(() =>
    {
        var styles = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Iris.App;component/Themes/Iris.Styles.xaml", UriKind.Absolute)
        };
        var used = new List<string>();
        foreach (var entry in styles.Keys)
        {
            if (styles[entry] is Style style)
            {
                CollectKeys(style, used);
            }
        }

        // 防空转：挖到 0 个键的话下面的循环一条都不跑，"全部通过"就是假的。若这条红了，说明
        // Setter.Value 上 {DynamicResource} 没有保持为 DynamicResourceExtension（本写法依赖它
        // 保持原样——Setter.Value 不是 DependencyProperty 目标，所以 ProvideValue 返回扩展本身）。
        used.ShouldNotBeEmpty();

        foreach (var key in used.Distinct())
        {
            Application.Current.TryFindResource(key).ShouldNotBeNull(
                $"{key} 被 Themes/Iris.Styles.xaml 使用，但没有任何已合并的字典定义它");
        }

        return Task.CompletedTask;
    });

    private static void CollectKeys(Style style, List<string> keys)
    {
        foreach (var setter in style.Setters.OfType<Setter>())
        {
            AddIfDynamicResource(setter.Value, keys);
            if (setter.Value is ControlTemplate template)
            {
                foreach (var trigger in template.Triggers)
                {
                    CollectTriggerKeys(trigger, keys);
                }
            }
        }

        foreach (var trigger in style.Triggers)
        {
            CollectTriggerKeys(trigger, keys);
        }
    }

    private static void CollectTriggerKeys(TriggerBase trigger, List<string> keys)
    {
        IEnumerable<Setter> setters = trigger switch
        {
            Trigger single => single.Setters.OfType<Setter>(),
            MultiTrigger multi => multi.Setters.OfType<Setter>(),
            DataTrigger data => data.Setters.OfType<Setter>(),
            MultiDataTrigger multiData => multiData.Setters.OfType<Setter>(),
            // EventTrigger 没有 Setter，本来就没得收。至于认不出来的触发器类型：静默返回空集合
            // 会让"新触发器类型带了个键"变成无声缺口，所以直接报红，逼人回来更新这个 switch。
            EventTrigger => Enumerable.Empty<Setter>(),
            _ => throw new NotSupportedException(
                $"{trigger.GetType().FullName} 是本守卫不认识的触发器类型，请更新 IrisStyleKeyResolutionTests"),
        };
        foreach (var setter in setters)
        {
            AddIfDynamicResource(setter.Value, keys);
        }
    }

    private static void AddIfDynamicResource(object? value, List<string> keys)
    {
        if (value is not DynamicResourceExtension extension)
        {
            return;
        }

        // ResourceKey 声明成 object。非字符串的键（ComponentResourceKey、{x:Type} 之类）没法拿去
        // TryFindResource，静默跳过又是一个无声缺口，所以让 Shouldly 当场报红。
        keys.Add(extension.ResourceKey.ShouldBeOfType<string>());
    }
}
```

范围说明（不要扩大）：只覆盖 `Themes/Iris.Styles.xaml`。其余窗口 XAML 里那三十来个 `{DynamicResource}`（`SettingsWindow.xaml`、`TranslateWindow.xaml`、`HistoryWindow.xaml`、`HotkeyRecorderBox.xaml`）是既有代码，本阶段不动、也不加断言。

边界再强调一次：不要为了"更全"去把遍历伸进模板体（模板体内的引用在 BAML 延迟内容里，实例化前取不到 `DynamicResourceExtension`；实例化之后再取要反射内部类型，得不偿失），也不要退化成读源码文本或手写键名清单——那正是这条守卫要避开的做法。模板体里的键按 Task 2 第三个用例那样补消费端断言。

Run: `dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~IrisStyleKeyResolutionTests"`

Expected: `总计: 1  失败: 0  成功: 1  已跳过: 0`。若 `used.ShouldNotBeEmpty()` 失败（挖不到键），**不要**改成读源码文本，也不要改成手写键名清单——把真实输出贴回来停下，这是需要重新设计守卫的信号。

- [ ] **Step 3: 改 `UpdateRecoveryRenderingTests.cs` 的资源环境**

设计 §3.1 与工单第 2 条都要求：`App.xaml` 里合并了什么，自建资源环境的测试就补什么——两处都点名了 `tests/Iris.App.Tests/TestSupport/WpfTestRunner.cs` 与 `UpdateRecoveryRenderingTests.cs`。前者在 Task 4 Step 6 已补上，这里补同一份。

这个测试在 `:42` 把 `app.Resources` 整个换掉（`app.Resources = new ResourceDictionary();`），所以它必须自己搭环境。在 `:44` 的 `ControlsDictionary` 之后追加两项，顺序与 `App.xaml` 一致：

```csharp
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Iris.App;component/Themes/Iris.Light.xaml", UriKind.Absolute) });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Iris.App;component/Themes/Iris.Styles.xaml", UriKind.Absolute) });
```

`:45-46`（`var theme` 与 `ApplicationThemeManager.Apply(theme)`）以及其余代码、该测试的全部断言**不动**。合并这两个字典不会改变恢复窗口的渲染：`Iris.Light.xaml` 只定义 `IrisAcrylic*` 四个新键、`Iris.Styles.xaml` 只定义带 `x:Key` 的新样式，都不覆盖 WPF-UI 已有的键；而 `UpdateRecoveryWindow` 用的是两个 WPF-UI 键（`SetResourceReference(..., "SolidBackgroundFillColorBaseBrush")` 与 `"TextFillColorPrimaryBrush"`，`UpdateRecoveryWindow.cs:26-27`），不是 Iris 键。

同时把 `:40-41` 那句已经失实的 `// Exactly App.xaml's global resources.` 换成准确的说法：

```csharp
            // The same global dictionaries App.xaml merges, minus Resources/EditingMenus.xaml:
            // the recovery window has no text input (UpdateRecoveryWindow.cs builds only
            // TextBlocks, Buttons and panels), so those styles are not loaded here.
```

是替换，不是追加——留着那句"Exactly App.xaml's"会让人以为这里与 `App.xaml` 逐项相同。

**给阶段 5 的一条设计规则（这条测试不再自动兜住它）**：`UpdateRecoveryWindow` 不应引用 `Iris.Acrylic*` 这类 Iris 自有键——它是纯色窗口，且要在"更新失败、什么都可能坏"的状态下显示。合并 Iris 字典之后，这条规则不再由对比度断言兜底（对比度断言本身仍在，`:100-102`，它继续挡住"颜色变差"），评审时按规则人工检查。

- [ ] **Step 4: 全量测试确认没有回归**

Run: `dotnet test Iris.slnx -c Release`
Expected: 全部通过，0 失败。这一步会跑十几分钟量级；若出现失败，先判断是不是新字典把某个既有隐式样式覆盖了（最可能是 `CheckBox`——本计划的新开关样式是**具名**的，不会成为隐式样式，但若后续把它改成无 `x:Key` 就会影响 25 处既有 `CheckBox`）。

- [ ] **Step 5: 暂存改动（本阶段不提交）**

```bash
git add tests/Iris.App.Tests/Themes/IrisStyleKeyResolutionTests.cs tests/Iris.App.Tests/Updates/UpdateRecoveryRenderingTests.cs
# 设计 §10 与工单第 23 行：每个阶段只有一个提交，见 Task 10 Step 6。此处只暂存，不提交。
# git commit -m "Pin the test resource environment to the same dictionaries App.xaml merges"
```

---

### Task 6: 样式示例渲染测试

把四类控件与两张卡放进一个离屏面板，在**浅色与深色两套字典**下各渲染一张图片，作为人工复看的证据。（「`src` 里不新增中文」那条不在这里，由 Task 10 Step 3 的本地化审计器守；本任务的测试只负责渲染与哈希。）

**Files:**
- Test: `tests/Iris.App.Tests/Themes/IrisStyleRenderingTests.cs`

- [ ] **Step 1: 写测试**

```csharp
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Iris.App.Tests.TestSupport;
using Shouldly;
using Wpf.Ui.Controls;
using Xunit;

namespace Iris.App.Tests.Themes;

[Collection("IrisThemeResources")]
public sealed class IrisStyleRenderingTests
{
    // 与 HistoryRenderingTests.cs:21、LocalizationRenderingTests.cs:34 同款写法：缓存成静态字段。
    // 写成内联的 new JsonSerializerOptions { WriteIndented = true } 会踩 CA1869，而本仓库
    // TreatWarningsAsErrors=true + AnalysisLevel=latest-recommended，CA1869 不在测试项目的 NoWarn 里。
    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task StylePanel_IsRenderedForReview(bool dark) => WpfTestRunner.RunAsync(() =>
    {
        // 空串不是 null：写成 ?? 时把 IRIS_T041_EVIDENCE 设成空串不会兜底，Path.Combine("", …) 会把
        // 证据写进进程当前目录（也就是 bin 输出目录），所以先用 IsNullOrEmpty 判空再退回临时目录。
        var evidenceRoot = Environment.GetEnvironmentVariable("IRIS_T041_EVIDENCE");
        var directory = Path.Combine(
            string.IsNullOrEmpty(evidenceRoot) ? Path.GetTempPath() : evidenceRoot,
            $"iris-styles-{(dark ? "dark" : "light")}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        var theme = dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light;

        var panel = new StackPanel { Margin = new Thickness(16), Width = 420 };
        // TextBlock 必须写全限定名：本文件同时 using 了 Wpf.Ui.Controls 与 System.Windows.Controls，
        // 两个命名空间都有 TextBlock，裸写会 CS0104 编译失败（与 IrisIconButtonStyleTests 同一处理）。
        // 而且这里要的正是基类那个：IrisSectionHeader 等样式的 TargetType 就是它。
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Section header", Style = (Style)Application.Current.FindResource("IrisSectionHeader") });
        panel.Children.Add(new Border
        {
            Style = (Style)Application.Current.FindResource("IrisRowCard"),
            Child = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } },
                Children =
                {
                    new StackPanel
                    {
                        Children =
                        {
                            new System.Windows.Controls.TextBlock { Text = "Row title", Style = (Style)Application.Current.FindResource("IrisRowTitle") },
                            new System.Windows.Controls.TextBlock { Text = "Row description", Style = (Style)Application.Current.FindResource("IrisRowDescription") },
                        },
                    },
                    new CheckBox { Style = (Style)Application.Current.FindResource("IrisSwitchCheckBox"), IsChecked = true, HorizontalAlignment = HorizontalAlignment.Right },
                },
            },
        });
        panel.Children.Add(new Border
        {
            Style = (Style)Application.Current.FindResource("IrisTag"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new System.Windows.Controls.TextBlock { Text = "Tag", Style = (Style)Application.Current.FindResource("IrisTagText") },
        });
        panel.Children.Add(new Wpf.Ui.Controls.Button
        {
            Style = (Style)Application.Current.FindResource("IrisIconButton"),
            Content = "CopySourceFull",
            Icon = new SymbolIcon { Symbol = SymbolRegular.Copy24 },
            HorizontalAlignment = HorizontalAlignment.Left,
        });

        // 渲染的是这个 Border（窗口的客户区），不是窗口本身：窗口的 ActualWidth/ActualHeight 是含非客户区
        // 的 460x320，直接 Render(window) 会在位图右侧和底部留下一条 alpha=0 的边——实测那张位图是 460x320，
        // 内容只占 444x280（右侧 16px、底部 40px 透明）。浅色档看不出来，深色档在查看器里是一条白带，
        // 会被误读成样式缺陷。
        var surface = new Border { Child = panel };

        var window = new Window
        {
            Content = surface,
            Width = 460,
            Height = 300,
            ShowActivated = false,
            ShowInTaskbar = false,
            Left = -20000,
            Top = -20000,
        };
        // 两套字典都合并在窗口这一层，只影响这个窗口的子树：WPF-UI 的 ThemesDictionary 换掉
        // TextFillColor* / SubtleFillColor*（标题与说明文字、标签底、图标按钮），Iris 那本换掉
        // IrisAcrylic*（卡面填充与描边）。不动 Application.Current.Resources，所以不用"改完再还原"。
        //
        // 初版这里只把窗口底色涂黑、不换字典，dark 档渲染出来其实是"浅色主题画在黑底上"：标题、标签、
        // 图标按钮那支近黑画笔合成到纯黑上全部消失，卡面却因为 #66FFFFFF(40% 白) 合成成 #666666 还看得见。
        // 实测那三点 (30,20) / (30,118) / (30,144) 全是 0,0,0 —— 一张"既不像浅色也不像深色"的图，
        // 容易被当成深色主题的样式缺陷。
        window.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = theme });
        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/Iris.App;component/Themes/Iris.{(dark ? "Dark" : "Light")}.xaml",
                UriKind.Absolute),
        });
        // 底色用主题自己的 ApplicationBackgroundBrush（实测浅色 250,250,250 / 深色 32,32,32），不写死白/黑：
        // 卡面填充是半透明的，压到应用真实的窗口底色上才看得出"比底色亮一点 + 一圈细边框"。
        // 这一句放在窗口与字典都就位之后：此时 surface 已挂进窗口的逻辑树，解析拿到的是窗口级那本。
        surface.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");

        // 钉住开关 ON 轨道的强调色，且只钉这一个键：UpdateRecoveryRenderingTests 的 finally
        // （tests/Iris.App.Tests/Updates/UpdateRecoveryRenderingTests.cs:115）调 1 参
        // ApplicationThemeManager.Apply(originalTheme)，该重载默认 updateAccent=true，会把 OS 强调色
        // （GetColorizationColor #FF0078D4）派生出的强调色笔刷写进进程级共享的 Application.Current.Resources
        // 并残留下来——app.Resources 的还原救不回来。于是同一份样式在不同进程组成下解析出的
        // ToggleSwitchFillOn 不同（仅本文件 #FF0067C0，宽过滤 Themes + UpdateRecoveryRenderingTests #FF006ABB），
        // 渲染出的哈希随之漂移（逐像素比对：只有那 40x20 开关区域会变，窗口底色不受影响）。
        // 这两个取值是本机生产路径的实测值：ThemeService 生产分支（SafeApplyWpfUiTheme）调的 Apply 同样让
        // updateAccent 取默认值 true，实测浅档 ON 轨道 #FF006ABB、深档 #FF4DB2FF——所以证据图与真机观感一致。
        // 直写 window.Resources（不是 MergedDictionaries 里那两本字典）：直写键的优先级高于合并字典，能压过
        // ThemesDictionary 带来的那个全局派生值。这是测试夹具"钉住不受测的输入"，不是样式改动——
        // ToggleSwitchFillOn 属 WPF-UI，不在 Iris 自有的四个键里。只钉这一个是因为实测只有它漂到画面上。
        var pinnedToggleFill = dark ? Color.FromArgb(0xFF, 0x4D, 0xB2, 0xFF) : Color.FromArgb(0xFF, 0x00, 0x6A, 0xBB);
        window.Resources["ToggleSwitchFillOn"] = new SolidColorBrush(pinnedToggleFill);

        try
        {
            window.Show();
            window.UpdateLayout();

            // 守卫：IrisCard/IrisRowCard 的 Background/BorderBrush 走 DynamicResource，键名拼错
            // 只会静默渲染成无底无框的透明块——图片哈希反映不出这个错误（哈希本来就会变），
            // 所以在这里显式断言。卡面必须真的拿到当前主题那本字典里的那两个键。
            // 底色也要一起断言：ApplicationBackgroundBrush 拼错时 SetResourceReference 只会留下 null，
            // 渲染出来整张图都是 alpha=0——哈希照旧、测试照绿，人复看的是一张空图。
            surface.Background.ShouldBeOfType<SolidColorBrush>();
            var rowCard = (Border)panel.Children[1];
            rowCard.Background.ShouldBeOfType<SolidColorBrush>();
            rowCard.BorderBrush.ShouldBeOfType<SolidColorBrush>();
            ((SolidColorBrush)rowCard.Background).Color.A.ShouldBeGreaterThan((byte)0);
            // 第三类控件：标签那个 Border 的底色也来自字典（IrisTag 的 Background 走 DynamicResource），
            // 一并守住，免得只有卡面被覆盖到。
            var tag = (Border)panel.Children[2];
            tag.Background.ShouldBeOfType<SolidColorBrush>();

            // dark 档必须真的换到深色。区分两档的只有 theme / ThemesDictionary{ Theme } /
            // Iris.{(dark ? "Dark" : "Light")}.xaml 这三处，任一处写死成 Light，dark 用例都照样全绿——
            // 这与初版"涂黑冒充深色"是同一族失败模式。窗口底色（ApplicationBackgroundBrush）实测深色
            // 32,32,32、浅色 250,250,250，断它那把笔刷的三通道之和，一次挡住"字典没换 / 主题写死 /
            // 窗口级 ThemesDictionary 被删"（删掉会退回应用级浅色）。读的是资源解析出的笔刷，不是位图像素。
            var background = (SolidColorBrush)surface.Background;
            var channels = background.Color.R + background.Color.G + background.Color.B;
            if (dark)
            {
                channels.ShouldBeLessThan(3 * 128);
            }
            else
            {
                channels.ShouldBeGreaterThan(3 * 128);
            }

            // 钉住的键必须真的压过合并字典：这条不是同义反复。它守的是"直写 window.Resources 的键
            // 优先级高于 MergedDictionaries"——哪天 WPF-UI 改了合并字典的优先级，这条会红，
            // 而不是让强调色漂移悄悄回来。
            window.FindResource("ToggleSwitchFillOn").ShouldBeOfType<SolidColorBrush>().Color.ShouldBe(pinnedToggleFill);

            var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            // 尺寸守卫：ActualWidth/ActualHeight 为 0 时位图是 0x0，照样能编码出一个非空的 PNG 文件。
            bitmap.PixelWidth.ShouldBeGreaterThan(0);
            bitmap.PixelHeight.ShouldBeGreaterThan(0);
            // 内容守卫：空图/纯色图也能生成非空 PNG 让测试全绿，人复看的却是一张空图。取一次像素、
            // 按 16 像素的步长采样（不逐像素比对），断言至少出现两种互不相同的颜色。
            var stride = bitmap.PixelWidth * 4;
            var pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);
            var distinct = new HashSet<uint>();
            for (var offset = 0; offset + 3 < pixels.Length; offset += 4 * 16)
            {
                distinct.Add(BitConverter.ToUInt32(pixels, offset));
            }

            distinct.Count.ShouldBeGreaterThan(1);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(directory, dark ? "styles-dark.png" : "styles-light.png");
            using (var output = File.Create(path))
            {
                encoder.Save(output);
            }

            // 同一个文件只读一次，哈希、字节数与下面的非空断言共用这一份字节。
            var bytes = File.ReadAllBytes(path);
            var manifest = new[] { new { File = Path.GetFileName(path), Bytes = bytes.Length, Width = bitmap.PixelWidth, Height = bitmap.PixelHeight, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) } };
            File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest, ManifestJson));
            bytes.Length.ShouldBeGreaterThan(0);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });
}
```

- [ ] **Step 2: 跑测试**

Run: `dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~IrisStyleRenderingTests"`
Expected: 2 个用例 PASS，各自输出 `styles-light.png` / `styles-dark.png` 与 `manifest.json`。

（渲染条件改过，两档哈希都会变。第一版实测的 `FD110B00…`（浅）与 `00348309…`（深）已作废，那一版是"不换字典、只涂黑窗口底色"的产物。）

（`IRIS_T041_EVIDENCE` 下每跑一次测试就新留一个 GUID 子目录，多次运行会累积；这些目录由协调方在阶段收尾时清理，只保留最终人工复看那两张图所在的目录。）

（强调色钉住之后的实测结果：`styles-light.png` = 5471 字节 / `sha256 4FB410D515931EFA0B1EA104EB654865B2ED4A5DC459D2348E6CDC4A3C694415`；`styles-dark.png` = 5489 字节 / `sha256 100CEF3C08AAF4DB4F0850D112DF15DBEE0A1A1BA847CF95AAF063B77E18FF6F`。这组哈希与**进程组成无关**：`--filter "FullyQualifiedName~IrisStyleRenderingTests"` 与 `--filter "FullyQualifiedName~Iris.App.Tests.Themes|FullyQualifiedName~UpdateRecoveryRenderingTests"` 两条命令产出的两张 PNG 字节数与 SHA256 完全相同（实测，两条退出码都是 0）。图里开关 ON 轨道的强调色 = **本机生产路径实测值**（浅 `#FF006ABB` / 深 `#FF4DB2FF`，即 `ThemeService` 那次 1 参 `Apply` 之后解析到的值），所以证据图与真机观感一致。）

- [ ] **Step 3: 人工复看两张图**

打开生成的 PNG（路径见测试输出；用 `IRIS_T041_EVIDENCE` 指到 `C:\Users\28910\AppData\Local\Temp\iris-t041-evidence`），**两档都要看**，逐条确认：行卡片有圆角、有细边框、卡面比窗口底色亮一点（半透明填充压上去的效果，不是不透明色块）；开关的轨道与滑块可辨；标签是小圆角块；图标按钮里只有图标、没有文字；深色那一档的文字是亮的、读得出来。

**别只靠肉眼**，这几条的客观量法（PowerShell + `System.Drawing` 采样就够，位图是 `surface` 的 444x280）：

- 圆角与边框：卡面左上角那几行里，横边比左竖边更靠右（半径约 5px），且最外一圈像素色 ≠ 卡面内部像素色。第一版实测：边框 `#EBEBEB`（= `#14000000` 合成到白底），圆角从 y=42 的 x=21 收到 y=46 的 x=17。
- 半透明：卡面内部像素 ≠ 窗口底色像素。第一版实测：`#66FFFFFF` 合成到白底 = 纯白（所以浅色那张的卡面只靠边框可见），合成到纯黑 = `#666666`。
- **消失类缺陷**：`(30,20)`（标题文字）、`(30,118)`（标签）、`(30,144)`（图标按钮）三处在两档里都必须有非底色像素。第一版的 dark 档这三点全是 `0,0,0`，就是这么抓出来的。
- 位图不该有透明边：第一版的位图是 460x320、内容只占 444x280。现在渲染的是窗口客户区那个 `Border`，整张图应该都是不透明的。

**这一步不能跳过**：渲染测试只记录哈希，哈希变了不等于看着对。发现问题就在这里改样式（或改这里的渲染条件），不要在后续阶段改。

两档的字典是**窗口级合并**的（`ThemesDictionary` + `Iris.{Light,Dark}.xaml`），机制与 Task 9 之后 `ThemeService` 在应用级换字典一致，只是不动 `Application.Current.Resources`。模糊/材质这一层本任务渲染不出来，阶段 5 用真实窗口截图验。

开关 ON 轨道的强调色在窗口级被**钉成生产实测值**（浅 `#006ABB` / 深 `#4DB2FF`，见 Step 1 里那段钉键注释）：本测试因此不受同进程其它测试残留的 OS 派生强调色影响，证据图与真机一致。

- [ ] **Step 4: 暂存改动（本阶段不提交）**

```bash
git add tests/Iris.App.Tests/Themes/IrisStyleRenderingTests.cs
# 设计 §10 与工单第 23 行：每个阶段只有一个提交，见 Task 10 Step 6。此处只暂存，不提交。
# git commit -m "Render the Iris style samples for review"
```

---

### Task 7: 窗口材质服务

**Files:**
- Create: `src/Iris.App/Services/WindowMaterialService.cs`
- Modify: `src/Iris.App/Hosting/AppHostBuilder.cs:81`（注册为单例）
- Test: `tests/Iris.App.Tests/Services/WindowMaterialServiceTests.cs`

接口按设计 §3.2：只有“给这个窗口应用亚克力”和“当前是否支持亚克力”。**版本判断必须可注入**，否则设计 §8.2 要求的“低版本系统走纯色”无法测试。

三处“服务不该动它”的断言都写成 `ShouldBe(BackdropDefault())`，而不是写死 `WindowBackdropType.None`：`FluentWindow.WindowBackdropType` 这个 DP 的默认值由 WPF-UI 决定、本机无法静态判定（若它其实是 `Auto`，写死 `None` 会把正确的行为判红），而我们要断言的是“服务没碰过它”，那正是“仍等于默认值”。**若 `FluentWindow.WindowBackdropTypeProperty` 不是 public**（编译报 CS0122），退路是改成 `ShouldNotBe(WindowBackdropType.Acrylic)`，并把这次替换记进报告。

- [ ] **Step 1: 写测试**

`tests/Iris.App.Tests/Services/WindowMaterialServiceTests.cs`：

```csharp
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Iris.App.Services;
using Iris.App.Tests.TestSupport;
using Shouldly;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Xunit;

namespace Iris.App.Tests.Services;

[Collection("IrisThemeResources")]
public sealed class WindowMaterialServiceTests
{
    [Theory]
    [InlineData(22621, true)]
    [InlineData(22631, true)]
    [InlineData(22620, false)]
    [InlineData(19045, false)]
    [InlineData(22000, false)]
    public Task IsAcrylicSupported_DependsOnOsBuild(int build, bool expected) => WpfTestRunner.RunAsync(() =>
    {
        var service = new WindowMaterialService(() => build, () => true);
        service.IsAcrylicSupported.ShouldBe(expected);
        return Task.CompletedTask;
    });

    [Fact]
    public Task IsAcrylicSupported_IsFalse_WhenBackdropReportsUnsupported() => WpfTestRunner.RunAsync(() =>
    {
        var service = new WindowMaterialService(() => 26100, () => false);
        service.IsAcrylicSupported.ShouldBeFalse();
        return Task.CompletedTask;
    });

    [Fact]
    public Task ApplyAcrylic_SetsBackdropType_WhenSupported() => WpfTestRunner.RunAsync(() =>
    {
        var service = new WindowMaterialService(() => 26100, () => true);
        var window = new FluentWindow { ExtendsContentIntoTitleBar = true, Content = "placeholder" };
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Left = -20000;
        window.Top = -20000;
        try
        {
            window.Show();
            service.ApplyAcrylic(window);
            window.WindowBackdropType.ShouldBe(WindowBackdropType.Acrylic);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task ApplyAcrylic_LeavesWindowSolid_WhenUnsupported() => WpfTestRunner.RunAsync(() =>
    {
        var service = new WindowMaterialService(() => 19045, () => true);
        var window = new FluentWindow { ExtendsContentIntoTitleBar = true, Content = "placeholder" };
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Left = -20000;
        window.Top = -20000;
        try
        {
            window.Show();
            service.ApplyAcrylic(window);
            window.WindowBackdropType.ShouldBe(BackdropDefault());
            var background = window.Background.ShouldBeOfType<SolidColorBrush>();
            background.Color.A.ShouldBe((byte)255);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Attach_AppliesBackdrop_AfterTheHandleIsCreated() => WpfTestRunner.RunAsync(() =>
    {
        var service = new WindowMaterialService(() => 26100, () => true);
        var window = new FluentWindow { ExtendsContentIntoTitleBar = true, Content = "placeholder" };
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Left = -20000;
        window.Top = -20000;
        try
        {
            service.Attach(window);                                   // 先挂，后 Show：此时还没有句柄
            window.WindowBackdropType.ShouldBe(BackdropDefault());
            window.Show();                                            // 句柄创建 → SourceInitialized → 应用
            window.WindowBackdropType.ShouldBe(WindowBackdropType.Acrylic);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Attach_DoesNothing_WhenUnsupported() => WpfTestRunner.RunAsync(() =>
    {
        var service = new WindowMaterialService(() => 19045, () => true);
        var window = new FluentWindow { ExtendsContentIntoTitleBar = true, Content = "placeholder" };
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Left = -20000;
        window.Top = -20000;
        try
        {
            service.Attach(window);
            window.Show();
            window.WindowBackdropType.ShouldBe(BackdropDefault());
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    // 回归：切主题会拆掉 DWM 背板、并把 Background 重置成不透明色（实测
    // ApplicationThemeManager.Apply(theme, WindowBackdropType.None) 之后 DWMWA_SYSTEMBACKDROP_TYPE
    // 从 3 变 1），所以之后必须把**背板**也装回来——只断言 Background 的 alpha 会在"窗口其实已经
    // 没有亚克力"时照绿。MainWindow 显式设成本窗口：Apply 只作用于 Application.MainWindow，
    // 显式指定让这条用例与执行顺序、与同进程里别的测试是否先建过窗口都无关。
    [Fact]
    public Task ApplyAcrylic_RestoresBackdrop_AfterTheThemeChangeRemovedIt() => WpfTestRunner.RunAsync(() =>
    {
        var service = new WindowMaterialService(() => 26100, () => true);
        // 必须在建窗口之前读：MainWindow 为空时，WPF 会把新建的第一个窗口自动设成 MainWindow。
        var previousMainWindow = Application.Current.MainWindow;
        var window = new FluentWindow { ExtendsContentIntoTitleBar = true, Content = "placeholder" };
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Left = -20000;
        window.Top = -20000;
        try
        {
            window.Show();
            Application.Current.MainWindow = window;
            service.ApplyAcrylic(window);
            ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.None);

            // 前置断言：先把「主题切换确实把背板拆掉了」钉死，堵住「中间这步被短路」的假绿。
            // 注意：在根本产不出背板的环境里（dwm 读回一直是 1），这条会真空通过，红的落点变成下面的
            // 终态断言——两种情形都不会假绿。
            SystemBackdropType(window).ShouldNotBe(AcrylicBackdropType);

            service.ApplyAcrylic(window);

            window.Background.ShouldBeOfType<SolidColorBrush>().Color.A.ShouldBe((byte)0);
            SystemBackdropType(window).ShouldBe(AcrylicBackdropType);
        }
        finally
        {
            Application.Current.MainWindow = previousMainWindow;
            window.Close();
        }

        return Task.CompletedTask;
    });

    private const int DwmwaSystemBackdropType = 38;
    private const int AcrylicBackdropType = 3;

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    // 读 DWM 的 SYSTEMBACKDROP_TYPE：3 = 亚克力。hr 不为 0 表示这次读回无效，直接判红。
    // 注意：这条断言依赖真实系统能上亚克力（Win11 + 透明效果打开），与那 10 条只读 DP 的用例不同。
    private static int SystemBackdropType(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        DwmGetWindowAttribute(handle, DwmwaSystemBackdropType, out var value, sizeof(int)).ShouldBe(0);
        return value;
    }

    // "服务没碰过它" == 该属性仍等于 DP 的默认值。不写死 None：默认值由 WPF-UI 决定、
    // 本机无法静态判定（若它其实是 Auto，写死 None 会把一份正确的行为判红）。
    // 写成方法而不是静态字段：字段初始化可能落在 xunit 的非 STA 线程上。
    private static WindowBackdropType BackdropDefault() => (WindowBackdropType)
        FluentWindow.WindowBackdropTypeProperty.GetMetadata(typeof(FluentWindow)).DefaultValue!;
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~WindowMaterialServiceTests"`
Expected: 编译失败（`WindowMaterialService` 不存在）。

- [ ] **Step 3: 写服务**

`src/Iris.App/Services/WindowMaterialService.cs`：

```csharp
using System;
using System.Windows;
using Wpf.Ui.Controls;

namespace Iris.App.Services;

/// <summary>
/// Owns the acrylic backdrop of the main windows. Nothing else may set
/// <see cref="FluentWindow.WindowBackdropType"/>: the theme layer keeps the windows solid and
/// this service switches the three acrylic windows over after their handle exists.
/// </summary>
internal sealed class WindowMaterialService
{
    private const int Windows11_22H2Build = 22621;

    private readonly Func<int> _osBuild;
    private readonly Func<bool> _backdropSupported;

    public WindowMaterialService()
        : this(() => Environment.OSVersion.Version.Build, () => WindowBackdrop.IsSupported(WindowBackdropType.Acrylic))
    {
    }

    internal WindowMaterialService(Func<int> osBuild, Func<bool> backdropSupported)
    {
        _osBuild = osBuild ?? throw new ArgumentNullException(nameof(osBuild));
        _backdropSupported = backdropSupported ?? throw new ArgumentNullException(nameof(backdropSupported));
    }

    internal bool IsAcrylicSupported => _osBuild() >= Windows11_22H2Build && _backdropSupported();

    /// <summary>
    /// Applies the acrylic backdrop if the system supports it. Safe to call again after a theme
    /// change: the theme change tears the backdrop down and restores an opaque background brush,
    /// so both the backdrop and the transparent background are (re-)applied here.
    /// </summary>
    internal void ApplyAcrylic(FluentWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!IsAcrylicSupported)
        {
            return;
        }

        if (window.WindowBackdropType != WindowBackdropType.Acrylic)
        {
            window.WindowBackdropType = WindowBackdropType.Acrylic;
            return;
        }

        // The backdrop does NOT survive a theme change: ApplicationThemeManager.Apply(theme, None)
        // runs WindowBackgroundManager.UpdateBackground on Application.MainWindow, which removes the
        // DWM backdrop and restores an opaque Background brush. Re-run WPF-UI's own apply path here
        // rather than clearing the brush by hand: setting the type away and back re-applies the
        // backdrop, removes the background brush, and still honours the veto WPF-UI puts on acrylic
        // in high-contrast themes - which a bare WindowBackdrop.ApplyBackdrop call would bypass.
        // Measured on the test host (it merges the same dictionaries App.xaml does):
        // DWMWA_SYSTEMBACKDROP_TYPE 3 -> 1 on the theme change, back to 3 after this.
        window.WindowBackdropType = WindowBackdropType.None;
        window.WindowBackdropType = WindowBackdropType.Acrylic;
    }

    /// <summary>
    /// Subscribes to the window so the backdrop is applied as soon as the handle exists.
    /// Called by the presenter right after it resolves the window. Idempotent.
    /// </summary>
    internal void Attach(FluentWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!IsAcrylicSupported)
        {
            return;
        }

        void OnSourceInitialized(object? sender, EventArgs e)
        {
            window.SourceInitialized -= OnSourceInitialized;
            ApplyAcrylic(window);
        }

        window.SourceInitialized += OnSourceInitialized;
    }

    /// <summary>
    /// Re-applies the backdrop to every open acrylic window. Called after the theme changed.
    /// </summary>
    internal void RefreshOpenWindows()
    {
        if (Application.Current is not { } application || !IsAcrylicSupported)
        {
            return;
        }

        foreach (Window window in application.Windows)
        {
            // ExtendsContentIntoTitleBar is the precondition for a backdrop at all (WPF-UI throws
            // when it is false), so this filter cannot miss an acrylic window - it is the same test
            // WPF-UI applies internally, not just "the three windows we know about".
            if (window is FluentWindow fluent && fluent.ExtendsContentIntoTitleBar)
            {
                ApplyAcrylic(fluent);
            }
        }
    }
}
```

- [ ] **Step 4: 注册进组合根**

`src/Iris.App/Hosting/AppHostBuilder.cs:81` 那组 `AddSingleton` 里，紧跟 `AddSingleton<ThemeService>();` 之后加一行：

```csharp
        builder.Services.AddSingleton<ThemeService>();
        builder.Services.AddSingleton<WindowMaterialService>();
```

- [ ] **Step 5: 跑测试确认通过**

Run: `dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~WindowMaterialServiceTests"`
Expected: 11 个用例全部 PASS。若 `ApplyAcrylic_LeavesWindowSolid_WhenUnsupported` 失败在 `window.Background` 上，说明 `FluentWindow` 的样式没给 `Background`（探测值 `#FFFAFAFA` 来自样式里的 `WindowBackground`）——那就不要断言具体画刷类型，改成断言 `window.WindowBackdropType == WindowBackdropType.None` 并删除那条背景断言，同时把这个偏离写进报告。

- [ ] **Step 6: 暂存改动（本阶段不提交）**

```bash
git add src/Iris.App/Services/WindowMaterialService.cs src/Iris.App/Hosting/AppHostBuilder.cs tests/Iris.App.Tests/Services/WindowMaterialServiceTests.cs
# 设计 §10 与工单第 23 行：每个阶段只有一个提交，见 Task 10 Step 6。此处只暂存，不提交。
# git commit -m "Add the window material service"
```

---

### Task 8: 三个窗口开启亚克力

**Files:**
- Modify: `src/Iris.App/Settings/SettingsWindow.xaml`、`src/Iris.App/Translation/TranslateWindow.xaml`、`src/Iris.App/History/HistoryWindow.xaml`（只在根元素上加一个属性；三个 `.xaml.cs` **不动**）
- Modify: `src/Iris.App/Settings/SettingsWindowPresenter.cs:291`、`src/Iris.App/Translation/TranslateWindowPresenter.cs:511`、`src/Iris.App/History/HistoryWindowSession.cs:25`（各加一行调用，另带英文注释；`HistoryWindowSession.cs` 顶部还要补一条 using）
- Modify: `tests/Iris.App.Tests/Settings/SettingsWindowPresenterTests.cs`（新增一条接线用例）
- Modify: `tests/Iris.App.Tests/Hosting/AppHostBuilderTests.cs`（**评审驱动的新增，不在原工单里**：在 `Host_ResolvesRequiredServices` 的清单里补一条 `WindowMaterialService` 非空断言，堵住「注册被挪走 → 三个窗口静默退回纯色而测试全绿」这条静默失效通道）
- Test: `tests/Iris.App.Tests/Themes/AcrylicWindowTests.cs`

三个窗口都由 DI 容器创建（`AppHostBuilder.cs:119`/`:126`/`:134` 的 `AddTransient`），Presenter 用自己那个 scope 解析它们（`SettingsWindowPresenter.cs:291`、`TranslateWindowPresenter.cs:511`、`HistoryWindowSession.cs:25`，三处的局部变量都叫 `scope`）。材质服务**从同一个 scope 取**，不走构造函数注入——理由见 Step 3。

- [ ] **Step 1: 在三个窗口的 XAML 根元素上加一个属性**

`ExtendsContentIntoTitleBar="True"`。**不要加 `WindowBackdropType`**（实测：`ExtendsContentIntoTitleBar=false` 时设它会抛，而且低版本系统上会留下透明的空窗口）。

`SettingsWindow.xaml` 根元素加在 `x:ClassModifier` 之后：

```xml
<ui:FluentWindow x:Class="Iris.App.Settings.SettingsWindow"
                 x:ClassModifier="internal"
                 ExtendsContentIntoTitleBar="True"
```

`TranslateWindow.xaml`：

```xml
<ui:FluentWindow x:Class="Iris.App.Translation.TranslateWindow"
                  x:ClassModifier="internal"
                  ExtendsContentIntoTitleBar="True"
```

`HistoryWindow.xaml`：

```xml
<ui:FluentWindow x:Class="Iris.App.History.HistoryWindow" x:ClassModifier="internal"
    ExtendsContentIntoTitleBar="True"
```

- [ ] **Step 2: 写测试**

`tests/Iris.App.Tests/Themes/AcrylicWindowTests.cs`。三个窗口的构造函数签名**各不相同**，所以按各自的真实构造方式建实例（已核实）。**三条用例都不 `Show()`**，这不是风格偏好：`Show()` 会触发 `SourceInitialized` → `HistoryWindow.PlaceInWorkArea`，在替身的 `IDesktopContext` 上必抛 `InvalidOperationException("History placement unavailable.")`（`HistoryWindow.xaml.cs:29`）；而这两个属性由 `InitializeComponent` 在构造函数里就设好了，不需要句柄。**若 `FluentWindow.WindowBackdropTypeProperty` 不是 public**（编译报 CS0122），退路是把那条默认值断言换成 `window.WindowBackdropType.ShouldNotBe(WindowBackdropType.Acrylic);`，并把这次替换记进报告。

```csharp
using System.Threading.Tasks;
using System.Windows;
using Iris.App.History;
using Iris.App.Settings;
using Iris.App.Translation;
using Iris.App.Tests.TestSupport;
using Iris.Core.Platform;
using NSubstitute;
using Shouldly;
using Wpf.Ui.Controls;
using Xunit;

namespace Iris.App.Tests.Themes;

[Collection("IrisThemeResources")]
public sealed class AcrylicWindowTests
{
    [Fact]
    public Task SettingsWindow_ExtendsContentIntoTitleBarAndKeepsTheBackdropForTheService() => WpfTestRunner.RunAsync(() =>
    {
        AssertExtendsTitleBarAndLeftBackdropUnset(new SettingsWindow());   // 无参构造（SettingsWindow.xaml.cs:24）
        return Task.CompletedTask;
    });

    [Fact]
    public Task TranslateWindow_ExtendsContentIntoTitleBarAndKeepsTheBackdropForTheService() => WpfTestRunner.RunAsync(() =>
    {
        AssertExtendsTitleBarAndLeftBackdropUnset(new TranslateWindow());   // 无参构造（TranslateWindow.xaml.cs:15）
        return Task.CompletedTask;
    });

    [Fact]
    public Task HistoryWindow_ExtendsContentIntoTitleBarAndKeepsTheBackdropForTheService() => WpfTestRunner.RunAsync(() =>
    {
        // HistoryWindow 是唯一带必需参数的：HistoryWindow(IDesktopContext, INativeWindowPlacementAdapter)。
        // 不 Show()，所以替身不需要配置 GetMonitors()/GetCursorPosition()（对照 HistoryTestScope.cs:55-56）。
        AssertExtendsTitleBarAndLeftBackdropUnset(
            new HistoryWindow(Substitute.For<IDesktopContext>(), Substitute.For<INativeWindowPlacementAdapter>()));
        return Task.CompletedTask;
    });

    private static void AssertExtendsTitleBarAndLeftBackdropUnset(FluentWindow window)
    {
        window.ExtendsContentIntoTitleBar.ShouldBeTrue();

        // "XAML 没设 WindowBackdropType" 的准确写法 == 它仍等于 DP 的默认值。不写死 None：
        // 默认值由 WPF-UI 决定、本机无法静态判定，若它其实是 Auto，写死 None 会把一份正确的
        // XAML 判红。设过它就会偏离默认值——这才是本阶段要守的那条线（材质归 WindowMaterialService）。
        var backdropDefault = (WindowBackdropType)FluentWindow.WindowBackdropTypeProperty
            .GetMetadata(typeof(FluentWindow)).DefaultValue!;
        window.WindowBackdropType.ShouldBe(backdropDefault);
    }
}
```

- [ ] **Step 3: 三个 Presenter 在解析出窗口后挂上材质服务**

**不要改窗口构造函数，也不要改 Presenter 的构造函数。** 两条理由都是查过的：

- 窗口：`SettingsWindow()` / `TranslateWindow()` 无参，`HistoryWindow` 两参；既有测试直接 `new` 它们（`SettingsWindowPresenterTests.cs:67`、`HistoryTestScope.cs:81`，另有多处对象初始化器写法）——加参数会弄坏它们。
- Presenter：`tests/` 里 `new SettingsWindowPresenter(` 有 **22 处**、`new TranslateWindowPresenter(` 有 **24 处**（实测统计），全部按现有参数表构造。加必需参数要改 46 处既有测试；加带默认值的参数则要靠 DI「挑一个能解析的构造函数」的规则兜底，同样不划算。

改用**从同一个 scope 取服务**：三个 Presenter 解析窗口那一行本来就是从 scope 里取（`scope.ServiceProvider.GetRequiredService<XxxWindow>()`），紧跟着再取材质服务即可——与 `TranslateWindowPresenter.cs:515` 的 `AttachPresenter` 属于同一个「创建后再交给别人」的既有模式。

服务侧提供 `Attach(FluentWindow)`（见 Task 7 Step 3）：它订阅 `SourceInitialized` 并在那时应用一次，所以在 `Show()` 之前调用即可。三个 Presenter 各自加一行：

`SettingsWindowPresenter.cs:291`，把

```csharp
                window = scope.ServiceProvider.GetRequiredService<SettingsWindow>();
```

改为

```csharp
                window = scope.ServiceProvider.GetRequiredService<SettingsWindow>();
                // Deliberately optional: the 22 existing construction sites in tests do not register
                // WindowMaterialService (and do not assert the material), so a required resolve would
                // turn them all red. The production path registers the singleton in AppHostBuilder.
                scope.ServiceProvider.GetService<WindowMaterialService>()?.Attach(window);
```

`TranslateWindowPresenter.cs:511`，把

```csharp
            window = scope.ServiceProvider.GetRequiredService<TranslateWindow>();
```

改为

```csharp
            window = scope.ServiceProvider.GetRequiredService<TranslateWindow>();
            // Deliberately optional, same reason as SettingsWindowPresenter: there are 24 construction
            // sites for this presenter in tests, and none of them register the service.
            scope.ServiceProvider.GetService<WindowMaterialService>()?.Attach(window);
```

`HistoryWindowSession.cs:25`，把

```csharp
            Window = scope.ServiceProvider.GetRequiredService<HistoryWindow>();
```

改为

```csharp
            var window = scope.ServiceProvider.GetRequiredService<HistoryWindow>();
            Window = window;
            // Deliberately optional, same reason as SettingsWindowPresenter: the tests that build this
            // session do not register the service.
            scope.ServiceProvider.GetService<WindowMaterialService>()?.Attach(window);
```

`HistoryWindowSession.cs` 顶部还要补 `using Iris.App.Services;`（实测：该文件只有 4 条 using，没有这一条；`SettingsWindowPresenter.cs:8` 与 `TranslateWindowPresenter.cs:8` 都已经有）。`WindowMaterialService` 已在 Task 7 Step 4 注册为单例，scope 的 provider 解析得到它。

本步**不改任何既有测试的构造点**，所以本阶段对既有测试的修改只有 `WpfTestRunner.cs`、`UpdateRecoveryRenderingTests.cs`（Task 1 / Task 4）与 `SettingsWindowPresenterTests.cs` 新增的那一条用例（下一步），逐条记进报告（Task 8 的评审又加了一条：`AppHostBuilderTests.cs` 补一条非空断言，见报告第 29 条）。

**评审驱动的补充：堵住可选解析的静默失效通道。** `GetService<WindowMaterialService>()?.Attach(...)` 漏注册不会编译失败，只会让三个窗口静默退回纯色、而上面那三条接线**没有任何回归护栏**。在 `tests/Iris.App.Tests/Hosting/AppHostBuilderTests.cs` 的 `Host_ResolvesRequiredServices` 里补一行断言（这一条**不在原工单里**，按「任何偏离工单之处必须写入偏差」记进报告第 29 条）：`var x = host.Services.GetService<T>();` 那一串的最后一行（`var hotkeyCoordinator = host.Services.GetService<HotkeyCoordinator>();`）之后加

```csharp
        var windowMaterialService = host.Services.GetService<WindowMaterialService>();
```

`x.ShouldNotBeNull();` 那一串的最后一行（`hotkeyCoordinator.ShouldNotBeNull();`）之后加

```csharp
        windowMaterialService.ShouldNotBeNull();
```

（该文件第 4 行已有 `using Iris.App.Services;`，实测无需补 using。）

- [ ] **Step 4: 加一条端到端接线测试**

上面三条只证明 XAML 属性到位、`Attach` 本身可用；还要证明 **Presenter 真的调了它**。照 `SettingsWindowPresenterTests.cs:91-109` 的 DI 集合写法加一条（`tests/Iris.App.Tests/Settings/SettingsWindowPresenterTests.cs`）：

```csharp
    [Fact]
    public async Task ShowAsync_AppliesAcrylic_WhenTheSystemSupportsIt()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var services = new ServiceCollection();
            var settingsService = Substitute.For<ISettingsService>();
            settingsService.Current.Returns(new AppSettings());

            var autoStartService = Substitute.For<IAutoStartService>();
            var uiDispatcher = new ImmediateUiDispatcher();
            // 假的版本号与支持探测：真机上是否支持不影响这条断言。
            var materialService = new WindowMaterialService(() => 26100, () => true);

            services.AddSingleton(settingsService);
            services.AddSingleton(autoStartService);
            services.AddSingleton<IUiDispatcher>(uiDispatcher);
            services.AddSingleton(materialService);
            services.AddSingleton<ThemeService>();
            services.AddScoped<GeneralSettingsSaveCoordinator>(sp =>
                new GeneralSettingsSaveCoordinator(settingsService, new TestSettingsEditCoordinator(settingsService), autoStartService, NullLogger<GeneralSettingsSaveCoordinator>.Instance));
            services.AddScoped<SettingsViewModel>();
            services.AddTransient<SettingsWindow>();

            using var rootProvider = services.BuildServiceProvider();
            var presenter = new SettingsWindowPresenter(rootProvider, uiDispatcher, NullLogger<SettingsWindowPresenter>.Instance);
            try
            {
                await presenter.ShowAsync("general");
                var window = presenter.ActiveSession!.Window;
                window.WindowBackdropType.ShouldBe(WindowBackdropType.Acrylic);
            }
            finally
            {
                await presenter.ShutdownAsync();
            }
        });
    }
```

成员名与收尾方式已实测对齐：`presenter.ActiveSession` / `presenter.ActiveSession.Window` 见 `SettingsWindowPresenterTests.cs:135-136`，收尾见 `:138-143` 的 `finally { await presenter.ShutdownAsync(); presenter.ActiveSession.ShouldBeNull(); }`。**`materialService` 必须注册**（`services.AddSingleton(materialService)`）：Presenter 用的是可选解析，漏注册不会编译失败，只会让这条断言以「`WindowBackdropType` 仍是默认值」的形式报红。

- [ ] **Step 5: 跑新测试与既有窗口测试**

Run: `dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~AcrylicWindowTests|FullyQualifiedName~SettingsWindowPresenterTests|FullyQualifiedName~SettingsWindowTranslationUiTests|FullyQualifiedName~TranslateWindowUiTests|FullyQualifiedName~HistoryRenderingTests"`
Expected: 全部 PASS。**这里是本阶段风险最高的一步**：`ExtendsContentIntoTitleBar="True"` 可能改变窗口内容区的起点（`ui:TitleBar` 控件高 48，实测）。既有测试里有按坐标取像素的断言——例如 `SelectionSettingsRenderingTests.cs:56` 的采样点写成 `((int)size.Width * 50 + 150) * 4`，即 **x=150、y=50**，正好紧贴标题栏下沿——若它们失败，先确认失败是布局位移还是真的样式问题，**不要**为了让测试过去而放宽阈值。

**本步实测结果**：过滤集 `总计 54 / 失败 0 / 成功 54 / 已跳过 0`、`EXIT=0`（协调方独立复跑得到同一读数）；影响面清单点名的「受影响」测试另外单独跑了两组（各 10 条与 26 条）也都是全绿——`SelectionSettingsRenderingTests.cs:56` 那条 `(150, 50)` 断言**没有**红。**未验证**：「标题栏的净像素位移是多少」没有单独测量，本步能确定的只是「没有引起任何既有几何/像素断言变红」。

派发前的只读影响面调查（协调方做的，扫了全仓 13 处各自私有的「渲染 + 记哈希」代码）结论，供本步对照：

- **没有任何测试把 sha256 写成期望常量，也不与仓库里已提交的 PNG 比对**：哈希是当场算出来写进 `manifest.json`/`manifest.txt` 的**证据**；全仓唯一的哈希断言是 `SettingsHotkeysIntegrationTests.cs:1707` 的 `lightHash.ShouldNotBe(darkHash)`。所以「哈希变化」本身不会让任何测试变红，会红的只可能是**几何/像素断言**。
- 渲染对象是**真实 `Window`**（`bitmap.Render(window)`），标题栏会进画面。**受影响**：`HistoryRenderingTests`、`SelectionSettingsRenderingTests`、`NetworkSettingsRenderingTests`、`OcrSettingsRenderingTests`、`NetworkSettingsFoundationTests`、`UpdateRenderingTests`、`MouseRenderingTests` 的 Settings 部分、`LocalizationRenderingTests` 的 Settings/Translation/History 三个场景、`OcrRenderingTests`、`SettingsHotkeysIntegrationTests`。**不受影响**：`ScreenshotRenderingTests`、`ScreenshotR1RenderingTests`、`UpdateRecoveryRenderingTests`、`UpdateHandoffLifecycleTests`（渲的不是这三个窗口）、`IrisStyleRenderingTests`（只渲测试自建元素）、`MouseRenderingTests` 的 24×24 图标。
- **最可能红的就是上面点名的 `SelectionSettingsRenderingTests.cs:56`**：`(150, 50)` 处的 R 通道硬断言（阈值 120/130）；`stride` 确实等于 `size.Width * 4`，无需任何行距修正；y=50 距标题栏下沿只差 2px。
- 本套测试宿主**没有注册 `WindowMaterialService`**（各 scope 直接 `new` 窗口，或只注册窗口本身），所以既有渲染测试**不会**走到「背景透明 + 背板」那条路——变化只应来自标题栏几何本身。
- `SettingsWindow.xaml` 与 `HistoryWindow.xaml` **本来就**带 `ui:TitleBar`（`Grid.Row="0"`），`TranslateWindow.xaml` 没有（靠透明 Grid + `DragMove`）。因此「本来带 TitleBar 的窗口净位移是否接近 0、裸窗口是否正好 +48px」**在跑之前无法确定**——本步就是去测它。

- [ ] **Step 6: 翻译窗口的拖动（必须在真机上确认，否则标明未验证）**

`TranslateWindow` 是三个窗口里唯一**没有** `ui:TitleBar` 的：它靠 `TranslateWindow.xaml:31` 的 `<Grid ... MouseLeftButtonDown="OnTitleBarMouseDown" Background="Transparent">` → `TranslateWindow.xaml.cs:43` 的 `DragMove()` 拖动。`ExtendsContentIntoTitleBar="True"` 会不会影响它，本机测不了（前台有全屏程序拦截合成输入），所以：

- 有条件时人工验证：打开翻译窗口，按住顶栏空白处拖动、双击顶栏。**记录下来。**
- 验证不了就在报告里写「未验证」，不要写成通过。
- **若确认拖不动**，退路是绕开 `ExtendsContentIntoTitleBar`，改用 WPF-UI 的静态后备路径（探测已验证到「只调 `ApplyBackdrop` 而不清 `Background` 时亚克力被不透明背景盖住」这一步，所以两者必须成对）：

```csharp
    // 退路：不使用 ExtendsContentIntoTitleBar，由服务自己清背景并挂后背。
    internal void ApplyAcrylicWithoutExtends(FluentWindow window)
    {
        if (!IsAcrylicSupported) return;
        WindowBackdrop.ApplyBackdrop(window, WindowBackdropType.Acrylic);
        WindowBackdrop.RemoveBackground(window);
    }
```

这条退路**本身也需要真机验证**（探测只验证了「不清背景就看不见」这一半），所以在报告里连同它的验证状态一起写清楚。三个窗口改用退路时要一并把 `ExtendsContentIntoTitleBar="True"` 撤掉，并重跑 Step 5。

- [ ] **Step 7: 暂存改动（本阶段不提交）**

```bash
git add src/Iris.App/Settings/SettingsWindow.xaml src/Iris.App/Translation/TranslateWindow.xaml src/Iris.App/History/HistoryWindow.xaml src/Iris.App/Settings/SettingsWindowPresenter.cs src/Iris.App/Translation/TranslateWindowPresenter.cs src/Iris.App/History/HistoryWindowSession.cs tests/Iris.App.Tests/Themes/AcrylicWindowTests.cs tests/Iris.App.Tests/Settings/SettingsWindowPresenterTests.cs
# 设计 §10 与工单第 23 行：每个阶段只有一个提交，见 Task 10 Step 6。此处只暂存，不提交。
# git commit -m "Apply the acrylic backdrop to the three main windows"
```

---

### Task 9: 修正 `ThemeService`

**Files:**
- Modify: `src/Iris.App/Services/ThemeService.cs:164-196`（`SafeApplyWpfUiTheme` 整段替换）、`:186` 附近、构造函数与字段声明处
- Create: `tests/Iris.App.Tests/Themes/IrisThemeDictionaryTests.cs`
- Modify: `tests/Iris.App.Tests/Services/WindowMaterialServiceTests.cs`（Step 6 加一条用例；**不补 using**，该文件已有 `using Wpf.Ui.Appearance;`）
- Modify（**质量评审驱动，不在原工单里**）：`tests/Iris.App.Tests/Hosting/AppHostBuilderTests.cs`（Step 4：真实组合根上的接线断言，一行）；`tests/Iris.App.Tests/Themes/IrisStyleRenderingTests.cs`（两行注释：Task 9 把 `ThemeService` 的 `Apply` 从一参改成两参后，那句「用的就是同一个 1 参 Apply」与行号都失实了，Task 6 的代码块已同步）

三处改动：明确传“无材质”；切换 Iris 自有主题字典；切完之后让材质服务重新透明化。

- [ ] **Step 1: 写失败测试**

新建 `tests/Iris.App.Tests/Themes/IrisThemeDictionaryTests.cs`。**不要**写进 `ThemeServicePreviewTests.cs`：那个类的用例跑在 xunit 自己的线程上，`ThemeService` 会把应用动作 `InvokeAsync` 到共享 STA 线程且**不等待**，断言时字典换没换是竞态；本测试要断言 `Application.Current.Resources.MergedDictionaries`，必须在 `WpfTestRunner.RunAsync` 里同步执行。挂 Task 1 建好的 `IrisThemeResources`。

**每条会换字典的用例都必须在 `finally` 里换回浅色**（派发前调查的结论）：整个测试进程只有一个 `Application`（`WpfTestRunner.cs:227`）、没有任何「跑前重置合并字典」的逻辑，不还原的话深色字典就留给后面的测试了。下面这块是**该文件的完整最终内容**（含 Step 4 的 DI 断言）：

```csharp
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Iris.App.Services;
using Iris.App.Tests.TestSupport;
using Iris.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Xunit;

namespace Iris.App.Tests.Themes;

[Collection("IrisThemeResources")]
public sealed class IrisThemeDictionaryTests
{
    // 直接调内部静态方法：不经过 dispatcher，结果确定，这是主断言。
    // 每一行都先换成相反的主题、再换成目标主题：宿主本来就合着浅色字典，只换一次的话 Light 那一行恒真，
    // 而「把深色换回浅色」这个方向根本没人断言。
    [Theory]
    [InlineData(ApplicationTheme.Light, ApplicationTheme.Dark, "Themes/Iris.Light.xaml", "Themes/Iris.Dark.xaml")]
    [InlineData(ApplicationTheme.Dark, ApplicationTheme.Light, "Themes/Iris.Dark.xaml", "Themes/Iris.Light.xaml")]
    public Task ApplyIrisThemeDictionary_ReplacesTheMergedIrisDictionary(
        ApplicationTheme theme, ApplicationTheme opposite, string expectedFile, string absentFile) =>
        WpfTestRunner.RunAsync(() =>
        {
            try
            {
                // 基线必须在第一次替换之前取：「先删再追加到末尾」的实现在第一次调用时就把字典挪走了，
                // 之后再怎么换下标都不变——在那之后才取基线，位置断言就守不住任何东西。
                var indexAtStart = IrisThemeDictionaryIndexes().ShouldHaveSingleItem();

                ThemeService.ApplyIrisThemeDictionary(opposite);
                IrisThemeDictionaryIndexes().ShouldHaveSingleItem().ShouldBe(indexAtStart);
                MergedSources()[indexAtStart].EndsWith(absentFile, StringComparison.OrdinalIgnoreCase).ShouldBeTrue();

                ThemeService.ApplyIrisThemeDictionary(theme);

                // 就地替换：仍然恰好一本 Iris 主题字典，而且还在原来的位置（位置决定覆盖优先级）。
                IrisThemeDictionaryIndexes().ShouldHaveSingleItem().ShouldBe(indexAtStart);
                MergedSources()[indexAtStart].EndsWith(expectedFile, StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
            }
            finally
            {
                RestoreLightDictionary();
            }

            return Task.CompletedTask;
        });

    [Fact]
    public Task Apply_ThroughThemeService_SwapsTheIrisDictionary() => WpfTestRunner.RunAsync(() =>
    {
        // ThemeService.Apply(AppTheme) 忽略传入值、改用设置里的主题（Apply → ApplyEffectiveTheme），
        // 所以把主题写进设置，走的才是与应用一致的那条路。传 Light 却能出现 Dark 字典，才算证明了这一点。
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings { General = new GeneralSettings { Theme = AppTheme.Dark } });
        using var service = new ThemeService(settings, visualThemeApplier: _ => { });
        try
        {
            // 起点必须是浅色：别的测试可能把深色字典留在共享 Application 里，那样下面的断言不换字典也会绿。
            RestoreLightDictionary();
            MergedSources().ShouldNotContain(source => source.EndsWith("Themes/Iris.Dark.xaml", StringComparison.OrdinalIgnoreCase));

            service.Apply(AppTheme.Light);

            MergedSources().ShouldContain(source => source.EndsWith("Themes/Iris.Dark.xaml", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            RestoreLightDictionary();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Apply_DoesNotLetAMaterialFailureEscape() => WpfTestRunner.RunAsync(() =>
    {
        // 必须是显式 Light：System 主题那条路外层本来就有 catch（SafeApplySystemTheme），测不出东西。
        // 显式主题这条路在启动时由 AppBootstrapper 同步调用、外层没有 catch——异常逃出去就是应用起不来。
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings { General = new GeneralSettings { Theme = AppTheme.Light } });
        var probed = false;
        var failing = new WindowMaterialService(
            () =>
            {
                probed = true;
                throw new InvalidOperationException("probe");
            },
            () => true);
        using var service = new ThemeService(settings, visualThemeApplier: _ => { }, windowMaterialService: failing);

        Should.NotThrow(() => service.Apply(AppTheme.Light));
        // 探针真的被调到过：否则「没抛出来」只说明根本没走到材质服务。
        probed.ShouldBeTrue();
        return Task.CompletedTask;
    });

    [Fact]
    public Task Apply_ReappliesTheWindowMaterialAfterTheThemeChange() => WpfTestRunner.RunAsync(() =>
    {
        // 接缝里做的就是生产分支做的那一件事（Apply(theme, None)），所以这条走的是与应用一致的顺序：
        // 先切主题（把窗口背景重置成不透明）→ 再由 ThemeService 调材质服务把它清回透明。
        // 背板本身（DWM 读回）由 WindowMaterialServiceTests 守着，这里只守「ThemeService 真的调了它」。
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings { General = new GeneralSettings { Theme = AppTheme.Light } });
        var material = new WindowMaterialService(() => 26100, () => true);
        // 必须在建窗口之前读：MainWindow 为空时，WPF 会把新建的第一个窗口自动设成 MainWindow。
        var previousMainWindow = Application.Current!.MainWindow;
        var window = new FluentWindow { ExtendsContentIntoTitleBar = true, Content = "placeholder" };
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Left = -20000;
        window.Top = -20000;
        byte? alphaRightAfterTheThemeChange = null;
        using var service = new ThemeService(
            settings,
            visualThemeApplier: theme =>
            {
                ApplicationThemeManager.Apply(theme, WindowBackdropType.None);
                alphaRightAfterTheThemeChange = (window.Background as SolidColorBrush)?.Color.A;
            },
            windowMaterialService: material);
        try
        {
            window.Show();
            // Apply(theme, None) 只作用于 Application.MainWindow，所以显式指定成本窗口。
            Application.Current.MainWindow = window;
            material.ApplyAcrylic(window);

            service.Apply(AppTheme.Light);

            // 前置：切主题确实把背景重置成了不透明——否则下面的终态断言什么也证明不了。
            alphaRightAfterTheThemeChange.ShouldBe((byte?)255, "null means the theme seam never ran");
            // 终态：ThemeService 在切完主题之后调了 RefreshOpenWindows。
            window.Background.ShouldBeOfType<SolidColorBrush>().Color.A.ShouldBe((byte)0);
        }
        finally
        {
            Application.Current.MainWindow = previousMainWindow;
            window.Close();
            RestoreLightDictionary();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Apply_KeepsASolidWindowOpaque_OnTheProductionPath() => WpfTestRunner.RunAsync(() =>
    {
        // 设计 §3.3 / §8.2 点名要的那一条：切主题后，纯色窗口的背景仍是不透明的 SolidColorBrush。
        // 不传接缝，走的是生产分支里那句 ApplicationThemeManager.Apply(theme, WindowBackdropType.None)；
        // 它只作用于 Application.MainWindow，所以把纯色窗口显式设成 MainWindow。
        // 材质服务声称支持亚克力：普通 Window 不是 FluentWindow，RefreshOpenWindows 不该碰它。
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings { General = new GeneralSettings { Theme = AppTheme.Light } });
        var material = new WindowMaterialService(() => 26100, () => true);
        // 必须在建窗口之前读：MainWindow 为空时，WPF 会把新建的第一个窗口自动设成 MainWindow。
        var previousMainWindow = Application.Current!.MainWindow;
        var window = new Window { Content = "placeholder", Background = Brushes.White };
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Left = -20000;
        window.Top = -20000;
        using var service = new ThemeService(settings, windowMaterialService: material);
        try
        {
            window.Show();
            Application.Current.MainWindow = window;

            service.Apply(AppTheme.Light);

            // 生产分支确实动过这个窗口：切主题会把背景换成主题自己的画刷，不再是初始那一支。
            // 没有这一句，初始的不透明白色就足以让下面的断言恒真。
            window.Background.ShouldNotBeSameAs(Brushes.White);
            window.Background.ShouldBeOfType<SolidColorBrush>().Color.A.ShouldBe((byte)255);
        }
        finally
        {
            Application.Current.MainWindow = previousMainWindow;
            window.Close();
            RestoreLightDictionary();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public void ThemeService_ResolvesTheMaterialServiceFromTheContainer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<ISettingsService>());
        services.AddSingleton<WindowMaterialService>();
        services.AddSingleton<ThemeService>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        provider.GetRequiredService<ThemeService>().HasWindowMaterialService.ShouldBeTrue();
    }

    private static string[] MergedSources() =>
        Application.Current!.Resources.MergedDictionaries
            .Select(dictionary => dictionary.Source?.OriginalString ?? string.Empty)
            .ToArray();

    private static int[] IrisThemeDictionaryIndexes() =>
        MergedSources()
            .Select((source, index) => (source, index))
            .Where(entry => entry.source.EndsWith("Themes/Iris.Light.xaml", StringComparison.OrdinalIgnoreCase)
                || entry.source.EndsWith("Themes/Iris.Dark.xaml", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.index)
            .ToArray();

    // 整个测试进程只有一个 Application、没有任何「跑前重置字典」的逻辑：不还原，深色字典就留给后面的测试了。
    private static void RestoreLightDictionary() => ThemeService.ApplyIrisThemeDictionary(ApplicationTheme.Light);
}
```

`ApplyIrisThemeDictionary` 必须是 `internal static`，测试才调得到（`InternalsVisibleTo Iris.App.Tests` 已存在）。经过 `ThemeService` 的两条断言依赖 dispatcher 线程环境（`ApplyEffectiveTheme` 只在**不在** dispatcher 线程时才 `InvokeAsync`，在 dispatcher 上是同步执行），所以都包在 `WpfTestRunner.RunAsync` 里，主断言放在第一条。`AddLogging()` 是 `Microsoft.Extensions.DependencyInjection` 命名空间里的扩展方法，**不需要** `using Microsoft.Extensions.Logging;`。

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~IrisThemeDictionaryTests"`
Expected: 编译失败（`ThemeService.ApplyIrisThemeDictionary`、`HasWindowMaterialService` 与第四个构造参数都还不存在）。Step 3 做完后全部转绿；「测试真的有牙」由 Step 5 之后的变异对照证明，不靠这一步。

- [ ] **Step 3: 改 `SafeApplyWpfUiTheme`**

**注意 `:166-180` 的 `_visualThemeApplier` 分支会提前 `return`**（它是测试接缝），`:186` 在它的 `else` 路径里。三行新逻辑如果接在 `:186` 之后，测试路径下字典切换与材质重挂**全部被跳过**。所以要把它改成 if/else，两条新调用放在分支**之外**。整段替换 `ThemeService.cs:164-196`：

```csharp
    private void SafeApplyWpfUiTheme(ApplicationTheme theme)
    {
        if (_visualThemeApplier != null)
        {
            try
            {
                _visualThemeApplier(theme);
            }
            catch (Exception ex)
            {
                if (_logger != null)
                {
                    LogThemeApplyFailed(_logger, theme.ToString(), ex.GetType().FullName ?? ex.GetType().Name);
                }
            }
        }
        else
        {
            try
            {
                if (Application.Current != null)
                {
                    // Ask for "no backdrop" explicitly: the window backdrop is owned by
                    // WindowMaterialService, and passing Mica here would replace its acrylic.
                    ApplicationThemeManager.Apply(theme, WindowBackdropType.None);
                }
            }
            catch (Exception ex)
            {
                if (_logger != null)
                {
                    LogThemeApplyFailed(_logger, theme.ToString(), ex.GetType().FullName ?? ex.GetType().Name);
                }
            }
        }

        // Outside the seam branch on purpose: the Iris dictionary and the window material have to
        // follow the theme on the test path too. Each step is guarded like the calls above, so a
        // failure stays a logged warning: AppBootstrapper applies an explicit Light/Dark theme
        // synchronously during startup, with no catch of its own.
        try
        {
            ApplyIrisThemeDictionary(theme);
        }
        catch (Exception ex)
        {
            if (_logger != null)
            {
                LogThemeApplyFailed(_logger, theme.ToString(), ex.GetType().FullName ?? ex.GetType().Name);
            }
        }

        try
        {
            _windowMaterialService?.RefreshOpenWindows();
        }
        catch (Exception ex)
        {
            if (_logger != null)
            {
                LogThemeApplyFailed(_logger, theme.ToString(), ex.GetType().FullName ?? ex.GetType().Name);
            }
        }
    }
```

**两句各自包 try/catch 是派发前调查后加的**（原计划裸调）：`AppBootstrapper.cs:214` 在启动时同步走到 `ThemeService.cs:148/:153`（显式 Light/Dark），外层没有任何 catch；裸调的话，Iris 字典加载失败或背板重挂失败会穿出 `StartCoreAsync`、被 `App.xaml.cs:36-41` 当成致命错误 `Shutdown(1)`——而同一位置现有的 WPF-UI 失败只记一条 Warning。分成两个 try 是因为字典换失败时背板仍应重挂（切主题已经把它拆了）。`Apply_DoesNotLetAMaterialFailureEscape` 钉住这一点。

文件顶部补 `using Wpf.Ui.Controls;`（`WindowBackdropType` 的命名空间；现有 `using Wpf.Ui.Appearance;` 不够）。**已核过**（包内 `C:\Users\28910\.nuget\packages\wpf-ui\4.3.0\lib\net10.0-windows7.0\Wpf.Ui.xml`）：`WindowBackdropType` 的四个枚举成员与 `WindowBackdrop.ApplyBackdrop(System.Windows.Window, Wpf.Ui.Controls.WindowBackdropType)` 全在 `Wpf.Ui.Controls`；而 `ApplicationThemeManager` / `ApplicationTheme` 在 `Wpf.Ui.Appearance`——两个命名空间都要。

并在同一个类里加（放在 `SafeApplyWpfUiTheme` 之后）：

```csharp
    private static readonly string[] IrisThemeDictionaryNames = ["Iris.Light.xaml", "Iris.Dark.xaml"];

    /// <summary>
    /// Swaps the Iris-owned colour dictionary for the one that matches the theme. WPF-UI swaps
    /// its own theme dictionary in ApplicationThemeManager; this one is ours, so it has to follow.
    /// </summary>
    internal static void ApplyIrisThemeDictionary(ApplicationTheme theme)
    {
        var dictionaries = Application.Current?.Resources.MergedDictionaries;
        if (dictionaries is null)
        {
            return;
        }

        var target = new ResourceDictionary
        {
            Source = new Uri(
                theme == ApplicationTheme.Dark
                    ? "pack://application:,,,/Iris.App;component/Themes/Iris.Dark.xaml"
                    : "pack://application:,,,/Iris.App;component/Themes/Iris.Light.xaml",
                UriKind.Absolute),
        };

        for (var index = 0; index < dictionaries.Count; index++)
        {
            var source = dictionaries[index].Source?.OriginalString;
            if (source is not null && IrisThemeDictionaryNames.Any(name => source.EndsWith(name, StringComparison.OrdinalIgnoreCase)))
            {
                dictionaries[index] = target;
                return;
            }
        }

        dictionaries.Add(target);
    }
```

（`System.Windows` 已在文件顶部第 3 行；`System.Linq` **不用补**——`Directory.Build.props` 开了 `ImplicitUsings`，实证是 `HotkeyRecordingCoordinator.cs:31` 用了 LINQ 而没有这条 using。新增注释一律英文：该文件既有注释是中文，但治理规则不许在 `src/` 里**新增**中文注释；既有中文注释一行不动。）

`Apply(AppTheme)` 的入参是被忽略的（`:71-79` 只调 `ApplyEffectiveTheme()`），不要试图在这里改这个既有行为。

`_windowMaterialService` 加进构造函数（可空，默认 `null`，保持既有构造点不破——**实测 `new ThemeService(` 在 `tests/` 里有 36 处、`src/` 里 0 处**，生产路径走 DI）：

```csharp
    public ThemeService(
        ISettingsService settingsService,
        Microsoft.Extensions.Logging.ILogger<ThemeService>? logger = null,
        Action<ApplicationTheme>? visualThemeApplier = null,
        WindowMaterialService? windowMaterialService = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _logger = logger;
        _visualThemeApplier = visualThemeApplier;
        _windowMaterialService = windowMaterialService;
        _settingsService.Changed += OnSettingsChanged;
    }
```

字段声明处（`ThemeService.cs:18` 之后）加 `private readonly WindowMaterialService? _windowMaterialService;`。

再加一个只读测试接缝，用来证明**生产路径真的拿到了服务**（否则 DI 会静默填 `null`，问题要到真机上才发现）：

```csharp
    internal bool HasWindowMaterialService => _windowMaterialService is not null;
```

- [ ] **Step 4: 让组合根把服务交给 `ThemeService`**

`WindowMaterialService` 已在 Task 7 Step 4 注册为单例；带默认值的 `WindowMaterialService?` 参数在服务缺失时会被 DI **静默填成 `null`**（带默认值的参数不会让 `ValidateOnBuild` 报错），所以构建通过不能证明接线成功。`IrisThemeDictionaryTests` 里的 `ThemeService_ResolvesTheMaterialServiceFromTheContainer`（已在 Step 1 的完整文件块里）把这条接缝钉住。

那一条用的是手搭的 `ServiceCollection`，守的其实是「MS.DI 会给带默认值的参数填已注册的实例」；**真实组合根**要另守一行（质量评审指出：有人删掉 `AppHostBuilder.cs:82` 的注册时，手搭容器那条不会红）。在 `tests/Iris.App.Tests/Hosting/AppHostBuilderTests.cs` 的 `Host_ResolvesRequiredServices` 里，紧跟 Task 8 加的 `windowMaterialService.ShouldNotBeNull();` 之后加一行（`:62` 解析 `AppBootstrapper` 时已经连带构造了 `ThemeService`，这里只是取同一个单例）：

```csharp
        host.Services.GetRequiredService<ThemeService>().HasWindowMaterialService.ShouldBeTrue();
```

Run: `dotnet build Iris.slnx -c Release`
Expected: `0 Warning(s)`、`0 Error(s)`。若 DI 校验报无法解析（或上面那条断言为 false），就在 `AppHostBuilder` 里改成显式工厂：

```csharp
        builder.Services.AddSingleton(sp => new ThemeService(
            sp.GetRequiredService<ISettingsService>(),
            sp.GetService<ILogger<ThemeService>>(),
            visualThemeApplier: null,
            sp.GetRequiredService<WindowMaterialService>()));
```

- [ ] **Step 5: 跑测试确认通过**

Run: `dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~IrisThemeDictionaryTests|FullyQualifiedName~ThemeServicePreviewTests"`
Expected: `IrisThemeDictionaryTests` 的 7 个用例（理论的 2 行 + 5 条事实）与 `ThemeServicePreviewTests` 的既有用例全部 PASS。

- [ ] **Step 6: 加一条「切主题后亚克力窗口的背板与透明背景都恢复」的测试**

这条守住第 6 条实测事实（切主题会把 `Background` 重置成不透明**并拆掉 DWM 背板**）。放在 `WindowMaterialServiceTests` 里——该文件顶部**已经有** `using Wpf.Ui.Appearance;`（Task 7 Step 1 的块里就带着），**不要再补一遍**：重复 using 会触发 CS0105，在 `TreatWarningsAsErrors=true` 下直接编译失败。这条用例要用的 `SystemBackdropType(Window)` 辅助、`DwmGetWindowAttribute` 声明与 `AcrylicBackdropType` 常量也已经在同一个类里（Task 7 Step 1），直接复用、**不要重复定义**（这条用例追加到文件末尾，会让计划里 Task 7 Step 1 的那块变成「截至 Task 7 的文件内容」——本阶段末尾的机械校验相应改为「该块必须作为**连续片段**出现在文件里」，见报告）：

```csharp
    [Fact]
    public Task RefreshOpenWindows_RestoresTheBackdropAfterThemeChange() => WpfTestRunner.RunAsync(() =>
    {
        var service = new WindowMaterialService(() => 26100, () => true);
        // 必须在建窗口之前读：MainWindow 为空时，WPF 会把新建的第一个窗口自动设成 MainWindow。
        var previousMainWindow = Application.Current.MainWindow;
        var window = new FluentWindow { ExtendsContentIntoTitleBar = true, Content = "placeholder" };
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Left = -20000;
        window.Top = -20000;
        try
        {
            window.Show();
            // Apply(theme, None) 只作用于 Application.MainWindow。不显式指定的话，这条用例拆的可能是
            // 别的窗口，前置断言就跟着失效——整条用例会退化成空转。指定之后，
            // 它与同类的其它用例、与执行顺序都无关。
            Application.Current.MainWindow = window;
            service.ApplyAcrylic(window);
            ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.None);

            // 前置断言：钉死「切主题确实把背板拆掉了」。在根本产不出背板的环境里它会真空通过，
            // 那时红的落点是下面的终态断言——两种情形都不会假绿。
            SystemBackdropType(window).ShouldNotBe(AcrylicBackdropType);
            window.Background.ShouldBeOfType<SolidColorBrush>()
                .Color.A.ShouldBe((byte)255, "the theme change is expected to restore an opaque background");

            service.RefreshOpenWindows();

            // 终态：背景透明**和**背板都得在。只断言 alpha 会在「窗口其实已经没有亚克力」时照绿。
            SystemBackdropType(window).ShouldBe(AcrylicBackdropType);
            window.Background.ShouldBeOfType<SolidColorBrush>().Color.A.ShouldBe((byte)0);
        }
        finally
        {
            Application.Current.MainWindow = previousMainWindow;
            window.Close();
        }

        return Task.CompletedTask;
    });
```

Run: `dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~WindowMaterialServiceTests"`
Expected: `总计: 12 / 失败: 0 / 成功: 12`（Task 7 的 11 条 + 这一条）。**若这条失败，先看红在哪一条断言上再动手**：红在**前置断言** → 这台机器上 `MainWindow` 或 `Apply(theme, None)` 没生效（环境问题，**不要**改断言）；红在**终态断言** → 服务没把背板装回来（代码问题）。实现侧不需要新写法——`WindowMaterialService.ApplyAcrylic` 已经就是「`WindowBackdropType` 先 `None` 再 `Acrylic`」的 DP 路径（Task 7 Step 3），`RefreshOpenWindows` 复用它。

- [ ] **Step 7: 暂存改动（本阶段不提交）**

```bash
git add src/Iris.App/Services/ThemeService.cs tests/Iris.App.Tests/Themes/IrisThemeDictionaryTests.cs tests/Iris.App.Tests/Services/WindowMaterialServiceTests.cs
# 设计 §10 与工单第 23 行：每个阶段只有一个提交，见 Task 10 Step 6。此处只暂存，不提交。
# git commit -m "Make the theme service stop owning the window backdrop"
```

---

### Task 10: 阶段收尾验证与提交

- [ ] **Step 1: 零警告构建**

Run: `dotnet build Iris.slnx -c Release`
Expected: `Build succeeded.`、`0 Warning(s)`、`0 Error(s)`。

- [ ] **Step 2: 全量测试**

Run: `dotnet test Iris.slnx -c Release`
Expected: 0 失败。逐条记录实际数字（通过/失败/跳过）用于完成报告，不要凭印象写。

- [ ] **Step 3: 本地化审计**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File build/audit-localization.ps1; "exit=$LASTEXITCODE"`
Expected: `exit=0`，无 `unapproved`。本阶段新增的 XAML 注释全部是英文，`src` 里没有新增中文字面量；若审计失败，先看它报的是哪个文件哪一行，不要改 `build/localization/exceptions.json` 去凑。

- [ ] **Step 4: 内存测量（如实报告）**

**绝不直接运行 `src/Iris.App/bin` 里的 exe**：那里没有 `portable.txt`，直接运行会读写 `%APPDATA%\Iris` 下的真实设置、密钥与历史库。用仓库自带的测量脚本——它把 Release 产物整份拷到 `%TEMP%\iris-measure-<guid>`、放一个 `portable.txt`（数据落在副本的 `data\` 里，`AppPaths.cs:19-43`），量完删掉副本；机器上已有 `Iris.App` 进程时脚本自己会拒绝运行（单实例互斥体按用户 SID 命名，与数据目录无关）。

Run（改动后）: `powershell -NoProfile -ExecutionPolicy Bypass -File build\measure-startup.ps1`
Run（改动前）: 把 `HEAD` 用 `git archive` 导出到仓库外的临时目录、在那里 `dotnet build src/Iris.App/Iris.App.csproj -c Release`，再运行**那份副本里的** `build\measure-startup.ps1`（脚本按自己所在位置找 `bin`）。
Expected: 两份完整输出原样贴进报告，不要转抄或取整。T-040 记录的 200 MiB 已经超过 160 MB 指标；本工单**不承诺**降到指标内，只如实报告改动前后的数字。拿不到就写“未验证”，不要编数字。

（本步原文是 `Get-Process Iris …`：进程名写错了，实际是 **`Iris.App`**；而且它只能读「已经在跑的安装版」，本机没有安装版，照原文执行只会得到空结果。）

- [ ] **Step 5: 真实窗口截图（人工）**

三个窗口各拍浅色、深色、中文、英文、默认尺寸、最小尺寸。**离屏渲染拍不到亚克力**（`RenderTargetBitmap` 只截客户区，不含 DWM 合成的背景），所以这一步只能真机截图。拿不到就写“未验证”。

做法与 Step 4 同一套隔离：Release 产物拷到 `%TEMP%\iris-t041-real-<guid>` + `portable.txt`，每个「主题 × 语言」组合预写一份 `data\settings.json`（七个热键全部 `enabled:false`、`launchAtStartup:false`、`checkForUpdates:false`、`translateWindow.hideOnDeactivate:false`）后单独启动一轮；用同一个副本 exe 带 `--action OpenSettings|OpenHistory|InputTranslate|Exit` 给主实例发命令；按 PID 找窗口、只截窗口矩形，证据放仓库外的 `%TEMP%\iris-t041-evidence\real-windows-<时间戳>`。**不点保存、不改开机自启**；只结束自己启动的那个 PID；收尾比对 `%APPDATA%\Iris` 三个文件的时间戳与 `HKCU\…\Run` 未被改动，并删除副本。「亚克力真的在」要有可测量的证据：同一个窗口压在两处不同的背景上各拍一张，比较同一块无控件区域的像素。

- [ ] **Step 6: 提交并报告**

```bash
git status --short
git add docs/superpowers/plans src/Iris.App tests/Iris.App.Tests
git commit -m "T-041 phase 1: add the shared theme layer and the window material service" -m "Co-Authored-By: Claude Fable 5.2 <noreply@anthropic.com>"
```

报告里必须包含：

- **与工单的偏差**：本阶段把材质服务的判定条件写成“系统版本 ≥ 22621 **且** `WindowBackdrop.IsSupported(Acrylic)`”（设计 §3.2 只写了版本号），多出的那半条是为了在 API 不支持时不留透明空窗口。
- **每个阶段一个提交**：工单第 23 行要求本阶段只产生一个本地提交，这与本项目“一张工单一个提交”的既有习惯不同，报告中要写明。
- **计划自身的缺陷（执行中被发现并已修正，逐条列出，附真实证据）**：
  1. Task 3 Step 1 的定稿测试编不过：`Descendants<TextBlock>(button)` 报 `CS0104`（该文件必须同时 `using Wpf.Ui.Controls;` 与 `using System.Windows.Controls;`，两者都有 `TextBlock`）。改为全限定名 `System.Windows.Controls.TextBlock`（写基类同时是更宽的覆盖）。
  2. Task 4 Step 5 自相矛盾：`Iris.SolidFallback.xaml` 的两个描边键写成半透明（`#1F000000` / `#1A000000`），而 Step 1 的 `SolidFallbackKeys_AreOpaque` 要求四个键全部 α=255 → 照计划实现必然 2/9 失败（实测失败值 31 / 26）。改为 `#FFE0E0E0` / `#FFE5E5E5`。
  3. Task 4 Step 5 的注释含中文（从设计文档引文 `("合并一份")` 带入），违反“`src` 不新增中文”且被本地化审计挡住（审计器只扫 `src/`，`build/LocalizationAudit/Program.cs:45`；评审实跑得到 exit 1，该条是当时**唯一**未批准项）。注释已改英文。
  4. 同一条注释里合成算式错一个色阶（`#1A000000` 压白底是 `#E5E5E5`，不是 `#E6E6E6`），且把来源错说成“亚克力 alpha”（`Iris.Light.xaml` 用的是 `#14000000` / `#0F000000`）。算式、来源、深色下的真实后果都已改准。
  5. Task 3 Step 3 的注释说 `Space or Enter` 都会置 `IsPressed`；实际只有 Space 会（`ButtonBase.OnKeyDown` 的 Enter 分支是 `SetIsPressed(false); OnClick();`）。已改。
  6. Task 8 的 Files 行写的测试文件名 `AcrylicWindowWiringTests.cs` 与同一任务 Step 2、类名、Step 6 的 `git add` 里的 `AcrylicWindowTests.cs` 不一致，已统一为后者。
  7. Task 1 的两处代码块在上一轮执行中与落盘内容分叉，收尾核对时才发现。Step 1：计划写的是弱版测试（单 theory、5 个 `InlineData`、只断 `ShouldNotBeNull`），落盘的是强版（两个 theory、6 个用例、断类型 `ShouldBeOfType<CornerRadius>()` / `ShouldBeOfType<Thickness>()`，并注明 3 个无消费者的 token 不在列）。Step 3：计划把 9 个数值 token 列成一整组，落盘把本阶段无消费者的 3 个（`IrisPillCornerRadius`、`IrisCardGap`、`IrisControlGap`）单独成组并注明“本阶段无消费者”；`IrisSectionHeader` 也从照抄 `IrisRowTitle` 的四个 setter 改成 `BasedOn="{StaticResource IrisRowTitle}"`。落盘版更严、更 DRY，且当时测试全绿，故以落盘为准，已把计划回填成与磁盘逐字一致（本项即回填记录）。是哪一步分叉的，现有记录无法判定。
  8. 与第 7 条同类、收尾核对时一并回填的另外两处。① `IrisSwitchStyleTests.cs` 落盘有两个用例，计划只写了一个：多出的 `SwitchStyle_KeepsWrappingContentTemplate` 用 `XamlReader.Parse` 就地造一个折行模板，证明换掉 `ControlTemplate` 之后调用方给的 `ContentTemplate` 仍被透传（`SettingsWindow.xaml:16` 的 `WrappingCheckBoxContent` 正是这么用的）。② `Iris.Styles.xaml` 的 `IrisNavItem` 落盘**没有**计划里那句 `<Setter Property="HorizontalAlignment" Value="Stretch" />`：该 setter 把 `FrameworkElement.HorizontalAlignment` 设成它本来就有的默认值（`Stretch`），且该样式无 `BasedOn`、`Border` 上有显式 `Style` 时隐式样式也不会参与，所以它是死代码——以磁盘为准，把计划里这行删掉，而不是往已评审的交付物里补死代码。
9. Task 6 Step 1 的定稿测试与第 1 条同一类：该文件必须同时 `using Wpf.Ui.Controls;` 与 `using System.Windows.Controls;`，裸写 `TextBlock` 报 `CS0104`。已把 4 处改成 `System.Windows.Controls.TextBlock`（要的正是基类那个：这些样式的 `TargetType` 就是它），并删掉一条用不到的 `using System.Windows.Shapes;`。
10. Task 6 的引言说这个渲染测试“顺带断言没有中文漏进 `src`”，但 Step 1 的测试块里没有这条断言，本阶段的实现里也没有（那条由 Task 10 Step 3 的本地化审计器守）。引言已改成准确说法。
11. Task 7 / Task 8 共六处断言把 `FluentWindow.WindowBackdropType` 写死成 `WindowBackdropType.None`。该 DP 的默认值由 WPF-UI 决定、本机无法静态判定（若它其实是 `Auto`，写死 `None` 会把一份**正确**的行为判红）。六处已改成“等于 DP 的默认值”（`FluentWindow.WindowBackdropTypeProperty.GetMetadata(typeof(FluentWindow)).DefaultValue`），断言意图不变：断言的是“服务没碰过它”。
12. Task 8 原计划给三个 Presenter 各加一个**必需**构造参数。实测 `tests/` 里 `new SettingsWindowPresenter(` **22 处**、`new TranslateWindowPresenter(` **24 处**，全部按现有参数表构造——照计划实现要改 46 处既有测试。已改成在**解析窗口的同一个 scope** 上 `GetService<WindowMaterialService>()?.Attach(window)`（可选用解析：既有测试构造点不动，生产路径由组合根注册的单例照常解析到）；`HistoryWindowSession.cs` 另补了一条 `using Iris.App.Services;`。**这是与计划原步骤的实质偏差，逐条记在此处**，也是本阶段对既有测试最省的做法。
13. Task 8 的三条窗口用例改成**不 `Show()`**：`Show()` 会触发 `SourceInitialized` → `HistoryWindow.PlaceInWorkArea`，在替身的 `IDesktopContext` 上必抛 `InvalidOperationException("History placement unavailable.")`（`HistoryWindow.xaml.cs:29`）。要断言的两个属性由 `InitializeComponent` 在构造函数里就设好，不需要句柄。
14. Task 9 的计划有多处自相矛盾/不实，均已改正：Files 头写 `ThemeServicePreviewTests.cs（新增用例）`，与 Step 1/Step 4/Step 7 实际要新建的 `Themes/IrisThemeDictionaryTests.cs` 冲突（以后者为准）；Step 6 要往 `WindowMaterialServiceTests.cs` 加用例却没提要补 `using Wpf.Ui.Appearance;`（该用例用到 `ApplicationThemeManager` / `ApplicationTheme`）；构造函数那句“保持既有 10 个调用点不破”实测是 **36 处**（`grep -rn "new ThemeService(" tests/ | wc -l` = 36，`src/` 里 0 处，生产路径走 DI）；Step 2 的“3 个用例”没说明 Step 4 还会再添第 4 个。
15. 有一条**审计意见经复核不成立**：提出者说 Task 9 Step 6 缺的是 `using Wpf.Ui.Appearance;`（理由是 `WindowBackdropType` 在这个命名空间）。实测包内 `C:\Users\28910\.nuget\packages\wpf-ui\4.3.0\lib\net10.0-windows7.0\Wpf.Ui.xml`：`WindowBackdropType` 的四个枚举成员（`None`/`Auto`/`Mica`/`Acrylic`/`Tabbed`）与 `WindowBackdrop.ApplyBackdrop(System.Windows.Window, Wpf.Ui.Controls.WindowBackdropType)` 都在 **`Wpf.Ui.Controls`**；只有 `ApplicationThemeManager` / `ApplicationTheme` 在 `Wpf.Ui.Appearance`。所以计划原来那句“补 `using Wpf.Ui.Controls;`（现有 `using Wpf.Ui.Appearance;` 不够）”是对的，Step 6 需要 `Wpf.Ui.Appearance` 是另一回事（它用 `ApplicationThemeManager`）。两条都对，但理由不同，证据已写进计划对应两步。
16. Task 6 Step 1 的代码**编译不过**（实测 `error CA1869`，退出码 1、0 个用例执行）：`JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true })` 每个序列化操作都新建 options，而本仓库 `TreatWarningsAsErrors=true` + `AnalysisLevel=latest-recommended` 把它升成 error（测试项目 `NoWarn` 只有 `CA1707;CA1515;CA2007;CA1861`）。已改成与 `HistoryRenderingTests.cs:21`、`LocalizationRenderingTests.cs:34` 同款的 `private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };`，**不放宽分析器**。
17. Task 6 的 dark 档**渲染条件不成立**（Task 6 Step 3 人工复看抓出，哈希与像素都留了证据）：测试宿主只 merge `Iris.Light.xaml`（`WpfTestRunner.cs:244` 硬编码），`dark` 参数只把窗口底色涂黑 → 那一档实际是"浅色主题画在黑底上"：标题、标签、图标按钮那支近黑画笔合成到纯黑上**整块消失**，而卡面 `#66FFFFFF`(40% 白) 合成成 `#666666` 反而看得见。实测像素：`(30,20)`（标题）`(30,118)`（标签）`(30,144)`（图标）全为 `0,0,0`，卡面 `(200,45)` = `102,102,102`。已改成在**窗口级**合并 `ThemesDictionary{Theme}` + `Iris.{Light,Dark}.xaml`（只影响该窗口子树，不动 `Application.Current.Resources`，机制与 Task 9 之后 `ThemeService` 在应用级换字典一致）；第一版哈希 `FD110B00BDFA2747B37864250C74A05F9CB87811B3D90A3647942B5A1FEBFB95`（浅）与 `003483096D0A97F85B328716588530DE25344EC04C9B2E857A729B2F3309FA68`（深）作废。
18. Task 6 的位图**带透明边**：原写法 `RenderTargetBitmap.Render(window)` 用的是窗口的 `ActualWidth`/`ActualHeight`（含非客户区，实测 460x320），而渲染出来的客户区只有 444x280 → 位图右侧 16px、底部 40px 的 `alpha=0`。浅色档在查看器里看不出来，深色档显示成一条白带，会被误读成样式缺陷（复核方第一眼就读错了）。已改成渲染窗口内容那个 `Border`（尺寸取它自己的 `ActualWidth`/`ActualHeight`），底色改用主题的 `ApplicationBackgroundBrush`（实测该键存在于包内 `Wpf.Ui.dll`），并新增一条 `surface.Background.ShouldBeOfType<SolidColorBrush>()`——键名拼错时 `SetResourceReference` 只留 null、整张图 alpha=0 而测试照绿，这条把它变成红灯。
19. `IrisAcrylicRowFillBrush` / `IrisAcrylicRowStrokeBrush` 在阶段 1 **没有任何消费端**：`Iris.Styles.xaml` 里只有 `IrisCard`（`:27-28`）用 Card 那一对，`IrisRowCard`（`:35-38`）只加 `Padding`/`Margin` → 行卡片实际用的是**卡面**填充（实测合成值 `#666666`，即 `#66FFFFFF`）。四个键的值都已被 `IrisColorKeyTests` 锁住（`IrisColorKeys_AreTranslucent` 对两本字典各断言 α<255，`SolidFallbackKeys_AreOpaque` 断言 α=255），所以这不是笔误，而是"预留给后续阶段却未标注"——计划自己的惯例是显式标注（`IrisPillCornerRadius` / `IrisCardGap` / `IrisControlGap`，Task 1 Step 3 `:177-180`），这一对没标。**本阶段不改**：要不要把 `IrisRowCard` 接到 Row 那一对（设计上行比卡更淡）属于阶段 2 列表样式的决定。
20. **与设计 §3.2 的偏差（需要架构师裁决）：阶段 1 不合并 `Iris.SolidFallback.xaml`。** 设计 §3.2 要求在系统不支持亚克力时用一份小字典把颜色键覆盖成不透明色；本阶段只**建**了这份字典（`src/Iris.App/Themes/Iris.SolidFallback.xaml`），没有任何地方合并它——全仓库 grep `SolidFallback` 只命中计划本身、`tests/Iris.App.Tests/Themes/IrisColorKeyTests.cs`、`src/Iris.App/Themes/Iris.Styles.xaml`、`src/Iris.App/Themes/Iris.Light.xaml` 四处，Task 7/8 的步骤里没有合并动作。理由是合并它会让**深色**主题更糟：这份字典的卡面填充是 `#FFFFFFFF`（不透明纯白），而深色主题下 `TextFillColorPrimaryBrush` 也是白色（Task 6 深色档实测标题文字 = `255,255,255`）→ 白卡面配白文字，卡面标题与说明文字**全都看不见**；不合并时卡面用半透明填充压在窗口底色上（深色实测 `#1AFFFFFF` 合成到 `#202020` 上得 `55,55,55`，配白字清楚可读）。浅色档两种做法只差 252 与 255（实测不合并时卡面填充 `252,252,252`；合并后是 `255,255,255`），肉眼不可辨。已同步改准计划第 7 行、Task 1 Step 6 那段，以及三处描述该行为的 XAML 注释（`Iris.Styles.xaml:24-26`、`Iris.Light.xaml:3-5`、`Iris.SolidFallback.xaml:3-14`）；改后 `Themes` + `UpdateRecoveryRenderingTests` 共 **31 个用例全通过**（退出码 0），两张 PNG 哈希不变（注释改动不影响渲染）。**请架构师裁决**：接受「阶段 1 不合并、阶段 2 补 Dark 变体后再合并」，还是本阶段就要一份按主题给两套值的字典。
21. Task 6 Step 1 的注释把 `ApplicationBackgroundBrush` 的浅色值写成 `#F3F3F3`，那是旧 T-040 证据里**应用自己**的窗口底色（`tasks/T-040.evidence/.../recovery-*-Light-540.json` 的 `WindowBackground: "#FFF3F3F3"`），不是这个键的值。实测 WPF-UI 4.3.0：浅色 `250,250,250`（`#FAFAFA`）、深色 `32,32,32`（`#202020`，这个原来是对的）。注释已改成实测值，计划与落盘文件同步改。
22. Task 6 的 PNG 哈希**随进程组成漂移**（本轮新发现，已修）：同一份测试在两种过滤器下渲染出的图不同——只跑本文件时开关 ON 轨道是 `#FF0067C0`，宽过滤（`Iris.App.Tests.Themes` + `UpdateRecoveryRenderingTests`）时是 `#FF006ABB`。逐像素比对：两版只差 **640 / 124764** 个像素，全部落在开关那 40x20 的区域（`x[379..418] y[63..82]`），窗口底色与其它控件完全一致；同变的有 `AccentFillColorDefault` / `AccentFillColorDefaultBrush` / `AccentTextFillColorPrimaryBrush`，而 `SystemAccentColor` / `ToggleSwitchKnobFillOn` / `ToggleSwitchFillOff` 不变。机制（调查代理实测）：触发者是既有测试 `tests/Iris.App.Tests/Updates/UpdateRecoveryRenderingTests.cs:115` 的 `finally { ApplicationThemeManager.Apply(originalTheme); }`——1 参重载默认 `updateAccent=true`，会把 OS 强调色（`GetColorizationColor()` = `#FF0078D4`）派生出的强调色笔刷写进**进程级共享**的 `Application.Current.Resources` 并残留下来，`app.Resources` 的还原救不回来（"含那一行 / 不含那一行"的对照探针：含=污染、不含=干净）。生产路径对应的是被污染那一档：`ThemeService`（`src/Iris.App/Services/ThemeService.cs:182-196`）用的就是同一个 1 参 `Apply`，实测生产浅档开关 ON 轨道 = `#FF006ABB`、深档 = `#FF4DB2FF`。修法：测试里在**窗口级直写** `window.Resources["ToggleSwitchFillOn"]`（直写键的优先级高于 `MergedDictionaries`），取值按主题用上面的生产实测值，并补一条断言——解析窗口作用域的 `ToggleSwitchFillOn` 必须等于钉住的那个值，守住"钉的优先级确实压过合并字典"；全程不碰任何全局状态（不调 `ApplicationThemeManager.Apply`、不写 `Application.Current.Resources`）。**未处理项**：既有测试的进程级副作用本阶段**不修**（属既有测试、改动面在本次范围之外，且钉键后本测试已免疫）；**其它渲染测试是否受同一副作用影响，未验证**。
23. **评审发现的严重缺陷（我实测确认，已修 + 已补回归测试）：切主题会拆掉 DWM 亚克力背板，原来的 `else` 分支救不回来。** 评审代理称 `ApplicationThemeManager.Apply(theme, WindowBackdropType.None)` 的第一步就是 `WindowBackdrop.RemoveBackdrop`（`WindowBackgroundManager.UpdateBackground`），所以 `ApplyAcrylic` 里那条「背板仍在 DWM 上、只需清掉不透明背景」的分支会让窗口停在「透明底 + 无背板」。这与计划开头「已实测的事实」第 6 条（声称读回仍是 3）直接冲突，我做了两组探针裁决：**(a) 裸 WPF-UI 探针进程**（`Application.Current.Resources` 里没有 WPF-UI 主题字典）——`Apply(Light, None)` 与真实的 Light→Dark→Light 切换之后，`DWMWA_SYSTEMBACKDROP_TYPE` 一直读回 `3`、`Background` 一直是 `#00FFFFFF`，**什么都没发生**（连计划说会发生的「背景被重置成不透明」也没发生）→ 说明这种环境下 `Apply` 根本走不到 `UpdateBackground`，**不能代表真实应用**（我第一轮就是被这个环境误导成「背板没事」）；**(b) 仓库自己的测试宿主**（`Application.Current.Resources` 合着与 `App.xaml` 相同的那批字典）——逐条实测：`1 dp=Acrylic dwm=3 bg=#00FFFFFF` → `2 Apply(Light,None) dwm=1 bg=#FFFAFAFA` → `3 + RemoveBackground dwm=1 bg=#00FFFFFF`（**缺陷坐实**：背板回不来）→ `4 + ApplyBackdrop+RemoveBackground dwm=3 bg=#00FFFFFF`；真实切到 Dark 同样 `5 dwm=1 bg=#FF202020` → `6 修法后 dwm=3 bg=#00FFFFFF`；另测 `7 WindowBackdrop.ApplyBackdrop` 对 `ExtendsContentIntoTitleBar=false` 的窗口**不抛异常**。第一版修法用 (b) 第 4/6 行验证过的写法（先 `WindowBackdrop.ApplyBackdrop` 再 `RemoveBackground`）；复审指出它绕开了 WPF-UI 在 `OnBackdropTypeChanged` 开头对**高对比度主题**的否决（`GetAppTheme() == HighContrast → newValue = None`，手写调用不经过那段），于是「首应用被否决掉的亚克力」会被这个 else 分支又装回去，自相矛盾。最终改成**走 WPF-UI 自己的 DP 路径**（`WindowBackdropType = None;` 再 `= Acrylic;`）：与第 4/6 行是等价转换、同样恢复背板，还自动继承高对比度否决、`SetWindowChrome` 与 `RemoveTitlebarBackground`，且不引入新的前置假设。同步更正了计划事实第 6 条、Task 7 Step 3 的服务块与 Step 1 的测试块。**新增第 11 条回归用例** `ApplyAcrylic_RestoresBackdrop_AfterTheThemeChangeRemovedIt`：读 DWM 的 `SYSTEMBACKDROP_TYPE` 断言等于 3，并断言 `Background` 的 alpha 为 0；`Application.Current.MainWindow` 显式设成本窗口，使结果与测试执行顺序、与同进程里别的测试是否先建过窗口都无关。**变异对照**：只删掉那两行修复、其余一律不动 → `总计 11 / 失败 1 / 成功 10 / 退出码 2`，红的正是这条新用例（`ApplyAcrylic_RestoresBackdrop_AfterTheThemeChangeRemovedIt`）；还原后 `11/11、退出码 0`、`iris_cmp_v4.py` 当时 16 项全 IDENTICAL（该脚本在 Task 8 后扩到 24 项，见第 28 条）。**这条用例是本阶段唯一一条会因环境不同而红的**：它依赖真实系统能上亚克力（Win11 + 透明效果打开；本机 build 26200 满足），与原先那 10 条只读 DP 的用例不同。原始读数留在 `%TEMP%\iris-t041-evidence\dwm-probe.txt`（仓库里的临时探针文件已删除、未入库）。
24. **Task 7 复审的其余结论与逐条处置**（评审代理反编译 WPF-UI 4.3.0 核对）：(a) 「`Attach` 幂等」的措辞不改——重复调用的**最终可观测状态**相同（多挂一个 handler 只是再应用一次），但严格说不是幂等，且该词没覆盖一个边角：`SourceInitialized` 已经触发之后再 `Attach`，那个 handler 永不执行；当前四个调用点都在 `Show()` 之前，不构成问题，故保留原词。(b) 「句柄未建时把 DP 置脏」已被修法消解，且比预想更彻底：`FluentWindow.OnSourceInitialized` 自己会拿**当前 DP 值**跑一次 `OnBackdropTypeChanged`，句柄一建好 WPF-UI 就会按 DP 装上背板。(c) `_backdropSupported` 在生产里近乎常量——不改代码，仅记于此：`WindowBackdrop.IsSupported(Acrylic)` 反编译等于 `Utilities.IsOSWindows7OrNewer`，真正的门槛只有 build ≥ 22621，不要以为 WPF-UI 会替我们把关。(d) `RefreshOpenWindows` 的窗口判据——评审**撤回**「改用显式标记」的建议并接受我的论证：`ExtendsContentIntoTitleBar` 是「能不能上背板」的**必要条件**（WPF-UI 在它为 false 时抛异常），与 WPF-UI 内部判据同源，不会漏窗口；只在 `if` 上方加三行注释留痕，逻辑未动。(e) `BackdropDefault()` 的注释保留（评审确认该 DP 默认值确为 `None`，注释无害）。(f) **Task 9 里那条走 `RefreshOpenWindows` 的用例会假绿**，已采纳评审的三点加固，Task 9 落地时必须照做：① 显式 `Application.Current.MainWindow = window;`（否则 `Apply(theme, None)` 拆不到它，整条用例退化成空转）；② 在 `Apply(theme, None)` 之后加前置断言 `SystemBackdropType(window).ShouldNotBe(AcrylicBackdropType);`；③ 在 `RefreshOpenWindows()` 之后加 `SystemBackdropType(window).ShouldBe(AcrylicBackdropType);`。

25. **派发 Task 8 前的只读影响面调查（未修改任何文件）**：为了让本阶段风险最高的 Task 8 Step 5 有据可依，协调方先派了一个只读子代理扫全仓的「渲染 + 记哈希」代码，结论已写进 Task 8 Step 5。要点：全仓**没有任何测试把 sha256 写成期望常量**（哈希是当场算出后写进 `manifest.json`/`manifest.txt` 的**证据**，唯一的哈希断言是 `SettingsHotkeysIntegrationTests.cs:1707` 的 `lightHash.ShouldNotBe(darkHash)`），所以「哈希变了」不会让测试变红；渲染用的是**真实窗口**，受影响的渲染测试 10 处、不受影响 6 处；最可能红的是 `SelectionSettingsRenderingTests.cs:56` 的 `(150, 50)` R 通道断言（阈值 120/130，`stride = size.Width * 4`，无需修正），因为 y=50 距标题栏下沿只有 2px；另外本套测试宿主**没有注册 `WindowMaterialService`**，既有渲染测试不会走到「背景透明 + 背板」那条路，变化只应来自标题栏几何。

26. **Task 8 暴露的一处「工单文字与治理规则冲突」（协调方裁决）**：Task 8 Step 3 的三个替换片段在计划里带的是**中文注释**（四行），而治理规则明令「`src/` 里不新增中文字面量与中文注释」。实现代理按治理规则执行、把这四行省掉了（只留那一行 C#）。协调方裁决：**改成英文注释保留**——`src/` 里本来就全是英文注释（见 `WindowMaterialService.cs`），而 `GetService` 而非 `GetRequiredService`、以及 `Window!` 这两处都是非显然选择，理由写在代码里比只写在计划里结实。计划里这三个片段已同步改为英文，本条即该偏差的记录。这是本阶段第 3 处「计划文字与治理规则打架」（前两处：`Iris.SolidFallback.xaml` 的空头承诺、Task 9 那条用例的加固）。

27. **Task 8 的实测读数与一处措辞更正**：Step 5 的过滤集（`AcrylicWindowTests|SettingsWindowPresenterTests|SettingsWindowTranslationUiTests|TranslateWindowUiTests|HistoryRenderingTests`）实测 `总计 54 / 失败 0 / 成功 54 / 已跳过 0`、`EXIT=0`（协调方独立复跑得到同一读数），影响面清单（第 25 条）点名的「受影响」测试另跑两组（10 条、26 条）也全绿——**担心的 `SelectionSettingsRenderingTests.cs:56` 那条 `(150, 50)` R 通道断言没有红**。据此把 Step 5 里「`ExtendsContentIntoTitleBar` 会改变内容区起点（实测标题栏占 48，内容从 Y=48 起）」更正为：48 是 **`ui:TitleBar` 控件**的实测高度，`ExtendsContentIntoTitleBar` 本身是否额外移动内容**没有单独测量**（未验证）；本步能确定的只是「没有引起任何既有几何/像素断言变红」。另：三处 `src/` 英文注释（第 26 条）、`SettingsWindowPresenterTests.cs` 新增的 `using Wpf.Ui.Controls;`、新用例的插入点，都是与工单的偏差，已逐条记录。

28. **机械校验脚本的现状（Task 8 之后）**：`%TEMP%\iris_cmp_v4.py` 现在共 **24 项**，Task 8 新增的 8 项是——`AcrylicWindowTests.cs` 整文件比对；三个 XAML 的根元素片段（Settings/Translate 各 3 行、History 2 行）必须**逐字连续出现**在文件里；三处 Presenter/Session 插入（Settings 5 行含 3 行英文注释、Translate 4 行、History 4 行）必须逐字连续出现；`SettingsWindowPresenterTests.cs` 里那条新用例（38 行）必须逐字连续出现。当前读数 **24/24 ALL IDENTICAL**（评审修正轮后扩到 26 项，见第 30 条）。这套校验**有牙**：它当场抓出一处不一致——计划把 `SettingsWindowPresenter.cs:291` 的片段写成 12 空格缩进，而真实代码嵌在 `if` 里是 16 空格；核对后判定**计划写错、代码是对的**，改的是计划。此前它也抓出过计划漏掉 `using Wpf.Ui.Appearance;`、以及三个 Iris 颜色字典从未被校验的盲区。

29. **Task 8 质量评审的结论与逐条处置**：结论 **批准，无阻塞项**。采纳三条——(a) `TranslateWindowPresenter.cs` 的英文注释原写「this file has 24 construction sites in tests」，实际那 24 处都在测试文件里、**不在**这个文件里，已改为「there are 24 construction sites for this presenter in tests」；(b) `HistoryWindowSession.cs` 原来写 `Attach(Window!)`，评审核对 `Window` 属性确为 `HistoryWindow?`、但同方法内 `Window.Show()`/`Window.WindowState` 等裸用也不报错（`NoWarn` 里没有 `CS86xx`），说明那个 `!` 冗余、注释的因果也不成立——改成先落局部变量 `var window = ...; Window = window; ... Attach(window)`，与另外两处完全对称，`!` 与那两行注释一并删掉；(c) 新增一条护栏：`Host_ResolvesRequiredServices` 的清单里补 `WindowMaterialService` 非空断言——评审核实过该清单原本**没有**它，而 `GetService<T>()?.Attach(...)` 的可选解析在注册被挪走时会静默把三个窗口退回纯色而测试全绿。**记录为已知局限、不改的**：(i) `AcrylicWindowTests` 用「等于 DP 默认值」判「XAML 没抢材质」，因此 XAML 里显式写 `WindowBackdropType="None"`（等于默认值）检测不到——行为上无害（服务随后会覆盖），但要知道这条断言不等价于「XAML 没写该属性」；(ii) 接线用例只有 Settings 一条，`TranslateWindowPresenter.cs:514` 与 `HistoryWindowSession.cs:29` 那两行**没有被任何用例守着**（删掉不会红）——本阶段的兜底是 Task 10 的真机截图（三个窗口都看）加协调方的机械校验（三行必须逐字在盘上），是否再补两条同型用例留给架构师裁决；(iii) `WindowMaterialServiceTests.BackdropDefault()` 与 `AcrylicWindowTests` 里各有一份取 DP 默认值的辅助，重复一行但收益太小，保留。（行号更正：History 改成局部变量形式后，那一行 `Attach` 现在落在 `HistoryWindowSession.cs:30`。）

30. **Task 8 评审修正轮的最终读数（协调方独立复跑，不取实现代理自报值）**：三处修正落盘后——`dotnet build tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release` `0 个警告 / 0 个错误`；Step 5 过滤集 `总计 54 / 失败 0 / 成功 54 / 已跳过 0`、`EXIT=0`；`FullyQualifiedName~AppHostBuilderTests` `总计 13 / 失败 0 / 成功 13`、`EXIT=0`；两者合并再跑一次 `总计 67 / 失败 0 / 成功 67`、`EXIT=0`。`iris_cmp_v4.py` 扩到 **26 项**（新增 `AppHostBuilderTests.cs:70` 的解析行与 `:91` 的断言行，各须逐字出现一次），读数 `ALL IDENTICAL`。暂存区相对 HEAD：`AppHostBuilderTests.cs` `+2 / -0`，`HistoryWindowSession.cs` `+6 / -1`，`TranslateWindowPresenter.cs` `+3 / -0`，`SettingsWindowPresenter.cs` `+4 / -0`；该文件第 4 行本就有 `using Iris.App.Services;`，**没有补 using**。**护栏的变异对照（协调方亲自重做）**：把 `AppHostBuilder.cs:82` 的 `AddSingleton<WindowMaterialService>()` 临时注释掉重建 → `总计 13 / 失败 1 / 成功 12`、`退出代码: 2`，红的是 `Iris.App.Tests.Hosting.AppHostBuilderTests.Host_ResolvesRequiredServices`；还原后工作区 blob 与暂存区 blob 同为 `d5bdc0c22087a306ef7be9be777cbc8129f4c941`、`git diff` 为空、重跑全绿。**一处必须如实说明的怪象**：Shouldly 在这条用例里打印的失败文本是 `settingsService should not be null but was`、栈帧行号 `AppHostBuilderTests.cs:68`——而 68 行不是断言，`settingsService` 也并不为空。实现代理另做了一次对照（改为注释掉 `HotkeyCoordinator` 的注册，已还原）得到**一模一样**的文本，说明 Release 构建下 Shouldly 取「表达式名/行号」在这条用例里不可信；所以「是哪一行断言红的」只能由**变异前后唯一的差别就是那一条注册**来推定，**不能**从失败文本里读出来。结论分两层写：「注册被删 → 该用例变红」**已验证**；「红的恰是新增的 `windowMaterialService.ShouldNotBeNull()` 这一行」是**推定，未由失败文本直接证实**。

31. **派发 Task 9 前的只读影响面调查与据此改的三处计划（协调方裁决）**：(a) **异常逃逸**——原计划把 `ApplyIrisThemeDictionary(theme)` 与 `_windowMaterialService?.RefreshOpenWindows()` 裸放在 `SafeApplyWpfUiTheme` 末尾。调查查到 `AppBootstrapper.cs:214` 在启动时于 UI 线程**同步**走到 `ThemeService.cs:148/:153`（设置为显式 Light/Dark 时），外层没有任何 catch；裸调时这两句一旦抛异常会穿出 `StartCoreAsync`、被 `App.xaml.cs:36-41` 当致命错误 `Shutdown(1)`，而同一位置现有的 WPF-UI 失败只记一条 Warning。为「不改变既有的失败语义」，改成**两句各自包一个与文件现有写法一致的 try/catch + `LogThemeApplyFailed`**，并新增用例 `Apply_DoesNotLetAMaterialFailureEscape` 钉住（这条用例与这两个 try/catch 都**不在原工单里**）。(b) **跨测试污染**——整个测试进程只有一个 `Application`（`WpfTestRunner.cs:227`），没有任何「跑前重置合并字典」的逻辑；`tests/` 里 `new ThemeService(` 36 处、`AddSingleton<ThemeService>()` 35 处，其中只有 4 处传了 `visualThemeApplier`，其余都是真实例，改动后它们应用深色主题时会把共享 Application 的 Iris 字典换成 `Iris.Dark.xaml` 并留着（`MouseRenderingTests.cs:29/42/81`、`TranslateWindowUiTests.cs:79-103`、`SettingsWindowTranslationUiTests.cs:254-293`、`OcrSettingsLifecycleTests.cs:167`、`OcrRenderingTests.cs:26` 等，它们只还原 WPF-UI 那本字典或根本不还原）。调查逐个核过：**目前没有任何断言会因此变红**——`src/` 下除 `Themes/` 四个文件外没有任何 XAML/代码引用 `Iris*` 键，`Themes/` 下的测试要么按 URI 自己加载、要么在窗口级合并、要么只断言与明暗无关的量；唯一从 `Application.Current.FindResource` 读 Iris 颜色的 `IrisColorKeyTests.cs:20` 只断言 alpha < 255，深色值照样满足（但它的局部变量叫 `light`，被污染后名不副实）。处置：新文件 `IrisThemeDictionaryTests` 里每条会换字典的用例都在 `finally` 里换回浅色；**既有测试一条不改**。**留给架构师的已知局限**：阶段 2 起生产 XAML 开始消费 `Iris*` 颜色键之后，这种既有的「主题状态跨测试泄漏」会开始影响渲染类测试的像素（哈希不被断言，所以大概率仍不会红，但证据图会随执行顺序变化）；届时需要在 `WpfTestRunner` 加统一的重置，本阶段不做。(c) **两处计划文字更正**——`IrisThemeDictionaryTests.cs` 不需要 `using Microsoft.Extensions.Logging;`（`AddLogging()` 在 `Microsoft.Extensions.DependencyInjection` 命名空间）；`ThemeService.cs` 不需要补 `using System.Linq;`（`ImplicitUsings`）。另：`ThemeService.cs` 的既有注释是中文、日志模板也是中文（`:10-13`、`:104`、`:314`），本次新增注释按治理规则写英文，同一文件里会出现两种语言——**既有中文一行未动**。调查里凡属框架内部行为的判断（xunit「并行 collection 先跑、禁并行的随后串行」、MS.DI 给带默认值的参数填已注册实例、被丢弃的 `InvokeAsync` 任务的异常去向）都是凭记忆、**未在仓库内确认**；其中第二条由 `ThemeService_ResolvesTheMaterialServiceFromTheContainer` 实测兜住。

32. **Task 9 的实测读数、变异对照与一条变异逼出来的新增用例（协调方独立复跑）**：`dotnet build Iris.slnx -c Release` `0 个警告 / 0 个错误`；`IrisThemeDictionaryTests|ThemeServicePreviewTests|WindowMaterialServiceTests` 合并 `总计 26 / 失败 0 / 成功 26`、`EXIT=0`（其中 `IrisThemeDictionaryTests` 6 条、`WindowMaterialServiceTests` 12 条）；补用例**之前**协调方跑过一次 `Iris.App.Tests` 全量 `总计 936 / 失败 0 / 成功 936`、`EXIT=0`（补用例之后的全量 `937 / 0 / 937` 是实现代理的读数，协调方的全量复跑放在 Task 10 Step 2）。`iris_cmp_v4.py` 扩到 **32 项**、`ALL IDENTICAL`：新增 `IrisThemeDictionaryTests.cs` 整文件比对（148 行）、`ThemeService.cs` 四段（`SafeApplyWpfUiTheme` 64 行、`IrisThemeDictionaryNames` + `ApplyIrisThemeDictionary` 35 行、构造函数 12 行、`HasWindowMaterialService` 1 行）逐字连续出现、Step 6 的用例 40 行逐字连续出现在 `WindowMaterialServiceTests.cs:196`；原来的「Task 7 Step 1 整文件等于」改为「磁盘去掉 Task 9 那 40 行与一行相邻空行后与 Task 7 的块逐字一致（195 行）」——比「作为连续片段出现」更严，证明既有 11 条用例与辅助成员一行未动。暂存区相对 HEAD：`ThemeService.cs` `+77 / -4`（新增行里无中文，脚本实测 0 行）、`IrisThemeDictionaryTests.cs` `+148 / -0`、`WindowMaterialServiceTests.cs` `+236 / -0`（HEAD 里没有它；相对 Task 7 的暂存版本是 +41）。Step 4 的显式工厂**没有用上**：`AddSingleton<ThemeService>()` 原样即可解析到材质服务，`AppHostBuilder.cs` 在 Task 9 未改。**变异对照（实现代理执行，每组还原后以 sha256 与空 `git diff` 证明干净；协调方未重做）**：M1 把 `ApplyIrisThemeDictionary` 方法体改成 `return;` → `总计 5 / 失败 2`，红的是理论的 Dark 那一行与 `Apply_ThroughThemeService_SwapsTheIrisDictionary`（Light 那一行照绿——测试宿主本来就合着浅色字典，这一行**单独不具备牙**）；M2 去掉包着 `RefreshOpenWindows()` 的 try/catch → `Apply_DoesNotLetAMaterialFailureEscape` 红，原文 `should not throw but threw System.InvalidOperationException "probe"`；**M3 把 `_windowMaterialService?.RefreshOpenWindows();` 整句注释掉 → 当时全绿（`17 / 0 / 17`）**，即「`ThemeService` 切完主题后重挂亚克力」这条 Task 9 的核心接线**没有任何用例守着**。据此新增用例 `Apply_ReappliesTheWindowMaterialAfterTheThemeChange`（**不在原工单里**）：在测试接缝里执行真实的 `ApplicationThemeManager.Apply(theme, None)` 并记下那一刻窗口背景的 alpha，断言「刚切完是 255」（前置）与「`ThemeService.Apply` 返回后是 0」（终态）。补完后重做 M3 → `总计 6 / 失败 1`，恰好这条红，原文 `window.Background should be 0 but was 255`（`IrisThemeDictionaryTests.cs:116`，终态断言；前置断言未红）；M4 把重挂那个 try 块挪到切主题**之前** → 同一条、同一断言红，说明它同时守住了「调了」与「在切主题之后调」；连跑 3 遍均 `6 / 0 / 6`。**这条用例的两处局限**：它只断言背景 alpha、不读 DWM 背板（背板由 `WindowMaterialServiceTests` 守）；它与 `RefreshOpenWindows_RestoresTheBackdropAfterThemeChange`、`ApplyAcrylic_RestoresBackdrop_AfterTheThemeChangeRemovedIt` 一样依赖真实系统能上亚克力，换一台不能产出背板的机器是否成立**未验证**。**没有任何断言盯着参数的一句（措辞已按规范评审更正，见第 33 条）**：生产分支里的 `ApplicationThemeManager.Apply(theme, WindowBackdropType.None)`——既有的无接缝测试会执行到它，但截至本条写下时没有断言守着它传的是什么；第 33 条补上了。

33. **Task 9 规范合规评审的结论与处置**：结论「实现与计划逐字一致、无阻塞项；1 条应修」。**应修（已修）**：设计 §3.3 原文要求「加一条测试：切换主题后，纯色窗口的背景仍是不透明的 `SolidColorBrush`」（§8.2 同义），计划自检表把它映射到 `ApplyAcrylic_LeavesWindowSolid_WhenUnsupported` 与 Task 9 Step 6——**两条都不成立**（前者根本不切主题；后者测的是亚克力窗口且不经过 `ThemeService`）。这是**计划对设计的缺口**，不是实现偏离计划。处置：新增 `Apply_KeepsASolidWindowOpaque_OnTheProductionPath`——普通 `Window` 设为 `Application.MainWindow`，用**不带接缝**的真 `ThemeService`（注入声称支持亚克力的材质服务）切主题，断言背景仍是 alpha 255 的 `SolidColorBrush`；自检表已改成指向它。它同时补上了「生产分支传的是无材质」这一句的断言。**变异对照（M5 协调方已亲自重做，读数一致；M6 仅实现代理执行）**：M5 把 `WindowBackdropType.None` 改成 `Mica`、M6 改回原始的一参 `Apply(theme)` → 两组都是 `总计 7 / 失败 1 / 成功 6`、退出码 2，恰好只有这条红，原文同为 `window.Background should be 255 but was 0`（`IrisThemeDictionaryTests.cs:152`；类型断言未红，即仍是 `SolidColorBrush`、alpha 为 0）——这也顺带**实测坐实了工单要修的那个缺陷**：不显式传「无材质」，WPF-UI 会把主窗口的背景清成透明。每组还原后 `ThemeService.cs` 的 sha256 回到 `2F4ECBD1…BECA40`、`git diff` 为空。**协调方独立复跑（补完后）**：`dotnet build Iris.slnx -c Release` `0 个警告 / 0 个错误`；`IrisThemeDictionaryTests|ThemeServicePreviewTests|WindowMaterialServiceTests` `总计 27 / 失败 0 / 成功 27`、`EXIT=0`（`IrisThemeDictionaryTests` 现为 7 条，文件 184 行 / 8417 字节 / LF / 无 BOM）；`iris_cmp_v4.py` 32 项 `ALL IDENTICAL`。实现代理另报一次 `Iris.App.Tests` 全量 `938 / 0 / 938`（协调方的全量复跑在 Task 10 Step 2）。**这条用例的一处未验证**：未变异时读到的 255 是来自初始的 `Brushes.White` 还是来自切主题后的重置，没有区分——变异只证明「改参数会让它变成 0」。**规范复审结论：✅ 规范合规**，无新的阻塞或应修项；复审另记三点（均不处理，如实列出）：设置给的是 Light 而宿主基线也是 Light，严格说不是一次「切换」；断言的是「仍不透明」（对得上设计第 68 行），第 226 行字面的「背景不变」没有被断言；`previousMainWindow` 是在 `new Window` 之后才读的，若当时 `MainWindow` 为空、WPF 已把它自动设成本窗口，`finally` 里的「还原」会指回一个已关闭的窗口（复审员凭 WPF 行为记忆，**未确认**；`WindowMaterialServiceTests` 里是同一写法）。**评审另记、本阶段不处理的覆盖缺口**（如实列出）：理论用例的 Light 那一行单独看近乎恒真（宿主本来就合着浅色字典，有牙的是 Dark 行）；`AppTheme.System` / 跟随系统那条路会换字典并重挂——没有专门用例；包着 `ApplyIrisThemeDictionary` 的那个 try/catch 没有用例（只有包着 `RefreshOpenWindows` 的那个有）；「就地替换保持合并顺序」没有用例（先删再追加的实现也会绿）；找不到 Iris 字典时的追加分支没有用例；真实组合根（`AppHostBuilder`）上 `HasWindowMaterialService` 为 true 没有用例（`ThemeService_ResolvesTheMaterialServiceFromTheContainer` 用的是手搭容器，`AppHostBuilderTests` 只断言服务已注册）。评审还指出两处已过期的行号注释（`ThemeService.cs:71-79 → :123`、`:198-221`），已改成不含行号的写法（`Apply → ApplyEffectiveTheme`、`SafeApplySystemTheme`）。其余核对项均无问题：`specs/CONTRACTS.cs` 无相关签名、无冲突；Task 9 只动三个文件；`ThemeService.cs` 无计划外改动、既有中文一行未动、新增行无中文；既有 `ThemeService` 构造点未改；无新增用户可见文字。

34. **Task 9 代码质量评审的结论与逐条处置**：结论 **批准（有建议），无必须修项**（评审员对照 dotnet/wpf 的 `Window.cs` 与 lepoco/wpfui 4.3.0 源码核实了框架行为，未跑 build/test）。**采纳（只动测试，均不在原工单里）**：(a) `Apply_ThroughThemeService_SwapsTheIrisDictionary` 可能空转——它只断言「含 Dark」，而别的测试（`ThemeServicePreviewTests.cs:170` 那条接缝抛异常的用例）可能已把深色字典留在共享 Application 里 → 开头先换回浅色并加前置断言「不含 Dark」；(b) Dark→Light 方向从未被断言、理论的 Light 行近乎恒真 → 理论改成「先换成相反主题、再换成目标主题」，并断言「恰好一本 Iris 主题字典」与「合并位置不变」（顺带补上第 33 条列的「就地替换保持合并顺序」缺口）；(c) `Apply_DoesNotLetAMaterialFailureEscape` 没证明异常真的被抛过 → 加 `probed` 标志；(d) Task 9 让一条既有注释变假——`IrisStyleRenderingTests.cs:119-120`（Task 6 的文件）写着「ThemeService 用的就是同一个 1 参 Apply」，现在是两参，且引用的行号也过期 → 改成不含行号的准确说法（结论不变：`updateAccent` 仍取默认值 true）；(e) 在 `AppHostBuilderTests.Host_ResolvesRequiredServices` 再加一行 `host.Services.GetRequiredService<ThemeService>().HasWindowMaterialService.ShouldBeTrue();`，补上第 33 条列的「真实组合根」缺口（手搭 `ServiceCollection` 的那条守的只是 MS.DI 的行为，删掉 `AppHostBuilder.cs:82` 时它不会红）；(f) `previousMainWindow` 改到**建窗口之前**读（四处：`IrisThemeDictionaryTests` 两处、`WindowMaterialServiceTests` 两处）——评审员读 WPF 源码确认：`Window.Initialize()` 在 `MainWindow` 为空时会把新建窗口自动设成 `MainWindow`；原写法靠「先还原再 `Close()`」的顺序才碰巧正确；同时更正 `WindowMaterialServiceTests` 里一句不准的注释（「要么什么都拆不到」这种情形不存在，只存在「拆的是别的窗口」）；(g) `Apply_KeepsASolidWindowOpaque_OnTheProductionPath` 在「`Apply` 根本没碰到本窗口」或 Win10 上恒绿 → 加一句 `window.Background.ShouldNotBeSameAs(Brushes.White)`，这也消掉了第 33 条那处「255 来自哪里」的未验证。**不采纳、留给架构师（如实记录）**：(i) **四个 catch 共用同一条日志**（`LogThemeApplyFailed`，EventId 1，只记主题名与异常类型名，且既有测试明确要求不传异常对象）——所以「WPF-UI 切主题 / 换 Iris 字典 / 重挂材质」哪一步失败在日志里**无法区分**，而材质重挂恰是现场最可能失败的一步。评审建议给日志加 `{Step}` 参数或分 EventId。不在本阶段做的理由：那条日志模板是既有的中文字面量（`ThemeService.cs` 末尾），改它要么动既有中文、要么新增一条英文模板与之并存，属于日志约定层面的决定，且超出工单范围。(ii) 离屏窗口搭建已重复 8 份、`MainWindow` 保存/还原 4 份，评审认为已到该抽辅助方法的程度（尤其后者的正确性依赖顺序）——本阶段只把顺序改对，不抽。(iii) `IrisThemeDictionaryNames` 静态字段声明在方法之间而不是文件顶部；`RefreshOpenWindows_…` 用例追加在私有辅助成员之后——风格问题，不改。(iv) **主题没变也会拆装一次背板**：`SettingsWindowSession.cs:269` 关窗时无条件 `ClearPreview` → 走一遍 `Apply` + 所有亚克力窗口 `None→Acrylic`；是否肉眼可见闪烁**未验证**，列入 Task 10 Step 5 真机观察项。(v) 接缝不再隔离全局状态：传 `visualThemeApplier: _ => { }` 的既有夹具现在会改全局 Iris 字典而 WPF-UI 主题不动，留下「WPF-UI 浅色 + Iris 深色」的错配（与第 31 条 (b) 同源，目前无断言受影响）。**评审确认无问题的**：`Iris.Styles.xaml` 里 `IrisAcrylic*` 的消费者都是 `DynamicResource`（`:28-29`），不存在切主题后不更新的缺陷（后续阶段若有人写成 `StaticResource` 就会出现）；就地替换与 WPF-UI 自己换主题字典的做法相同，不建议加「已是目标字典就短路」；三条入口都做了 `CheckAccess`/`InvokeAsync`，新逻辑在 UI 线程；`RefreshOpenWindows()` 在 `_gate` 锁之外调用；`Dispose` 之后不会进入新逻辑；`HasWindowMaterialService` 与文件里既有的 `HasActivePreview` 同类，可接受。

35. **Task 9 质量修正轮的实测读数与两处「断言没牙」的更正**：采纳项落盘后（四个测试文件，`src/` 未动），实现代理做了四组变异，其中两组**不符合预期**并如实报回：**N2**（把就地替换 `dictionaries[index] = target` 改成 `RemoveAt` + `Add`）→ `7 / 0 / 7` **全绿**。原因是协调方在计划里把基线下标写在了第一次替换**之后**取——「先删再追加」的实现在第一次调用时就把字典挪到末尾，此后下标不再变，位置断言守不住任何东西。**这是计划（协调方）写错，不是实现偏离。** 已改成在任何替换**之前**取基线，协调方亲手重做 N2 → `总计 7 / 失败 1 / 成功 6`，红的是理论的 Dark 行，原文 `should be 2 but was 4`（`IrisThemeDictionaryTests.cs:38`）；只红一行的原因也已核对：先跑的那一行失败后 `finally` 的还原把字典留在了末尾，后跑的那一行基线取到的就是末尾。**N3**（注释掉 `AddSingleton<WindowMaterialService>()`）红在 Task 8 加的 `ShouldNotBeNull` 上，新加的真实组合根断言没被执行到、**没被证明有牙**。协调方改用一组对口的变异亲手重做：把 `AppHostBuilder.cs:81` 的 `AddSingleton<ThemeService>()` 临时换成不传材质服务的显式工厂（服务仍可解析、但 `ThemeService` 拿不到）→ `AppHostBuilderTests` `总计 13 / 失败 1 / 成功 12`，红的是 `Host_ResolvesRequiredServices`，失败形态是 `should be … but was …`（不是 N3 那种 `should not be null`）；与第 30 条同理，Shouldly 在这条用例里报的行号（`:68`）不可信，「红的恰是新加那一行」由**失败形态不同 + 变异唯一改动点**推定。另两组符合预期：**N1**（`IrisThemeDictionaryNames` 漏掉 Dark）→ 2 条红（理论 Light 行 `should have single item but had 2 items`；`Apply_ThroughThemeService_SwapsTheIrisDictionary` 的新前置断言）；**N4**（生产分支的 `Apply(theme, None)` 整句注释掉）→ 只有 `Apply_KeepsASolidWindowOpaque_OnTheProductionPath` 红，红在新加的 `ShouldNotBeSameAs`，原文 `window.Background should not be same as #FFFFFFFF but was #FFFFFFFF`（N1、N4 由实现代理执行，协调方未重做）。每组变异还原后以空 `git diff` 证明干净。**协调方最终读数**：`dotnet build` `0 个警告 / 0 个错误`；`IrisThemeDictionaryTests|ThemeServicePreviewTests|WindowMaterialServiceTests|AppHostBuilderTests|IrisStyleRenderingTests` `总计 42 / 失败 0 / 成功 42`；`iris_cmp_v4.py` **33 项** `ALL IDENTICAL`（新增 `AppHostBuilderTests` 里真实组合根那一行）。暂存区相对 HEAD：`IrisThemeDictionaryTests.cs` `+221 / -0`（221 行 / 11011 字节 / LF / 无 BOM）、`WindowMaterialServiceTests.cs` `+238 / -0`、`AppHostBuilderTests.cs` `+3 / -0`、`IrisStyleRenderingTests.cs` `+207 / -0`、`ThemeService.cs` `+77 / -4`（sha256 `2F4ECBD1…BECA40`，自 Task 9 首次交付起未变）、`AppHostBuilder.cs` `+1 / -0`。**流程偏差一条**：这次 4 行断言修正与两组变异是协调方直接做的，没有再经实现代理（按「子代理逐个任务」的约定本应派回）；理由是错在计划本身、改动只有基线取值的位置、且需要立刻用变异验证。

36. **Task 10 Step 1–3 的实测读数（协调方执行，原始日志在 `%TEMP%\iris-task10-full.log`）**：`dotnet build Iris.slnx -c Release` → `0 个警告 / 0 个错误`、`BUILD_EXIT=0`。`dotnet test Iris.slnx -c Release --no-build` → `总计 3843 / 失败 0 / 成功 3841 / 已跳过 2`、`TEST_EXIT=0`，七个测试程序集全部「已通过」（`Iris.App.Tests` 用时 3m 49s，另六个是 `ArchitectureTests`、`Platform.Windows.Tests`、`Providers.Tests`、`Maintenance.Tests`、`Infrastructure.Tests`、`Core.Tests`）。**跳过的 2 条都不是本工单的测试**，且都自报为环境限制：`Iris.Platform.Windows.Tests.Clipboard.ClipboardSnapshotIsolatedTests.SnapshotHelper_UsesExclusiveNonInteractiveStation_AndJoinsOwnedProcess` 与 `…Win32ClipboardIsolatedIntegrationTests.IsolatedWindowStation_NativeClipboardIntegration_ExecutesInDedicatedProcess`，原文同为 `[ENVIRONMENT LIMITATION] … Window station creation failed or already exists (station=0, error=183)`；它们在本工单改动之前是否同样被跳过**未验证**（没有在 HEAD 上跑过对照）。本地化审计 `powershell -NoProfile -ExecutionPolicy Bypass -File build/audit-localization.ps1` → `exit=0`，`resourceErrors: []`，`sourceEntries 1196 / commentEntries 978 / reviewedNonUiEntries 218`（这一次是在 Task 9 首次交付后跑的；其后的改动只在 `tests/`，而审计器只扫 `src/`——提交前会再跑一遍，以那一遍为准）。

37. **Task 10 Step 5 真机截图的做法、读数与人工复看结论（执行代理在隔离副本里做，协调方未亲手复跑；证据在仓库外 `%TEMP%\iris-t041-evidence\real-windows-20261113-020145`，289 个文件、3,879,689 字节，其中 PNG 130 个）**。**隔离**：先派只读调查确认办法（便携模式：exe 旁有 `portable.txt` 时数据落在 `<exe 目录>\data`；先例 `build/measure-startup.ps1:81-88`），再把 Release 产物拷到 `%TEMP%\iris-t041-real-<guid>` 运行，全程没有直接运行 `bin` 里的 exe。应用共启动 8 次（1 次试跑 + 4 轮正式 + 3 轮切主题），每次都在发 `--action Exit` 后 15 秒内自行退出、退出码 0，没有用过 `Stop-Process`。收尾比对：`%APPDATA%\Iris` 的 `settings.json`（4501 字节）、`secrets.dat`（645 字节）、`history.db`（147456 字节）的 `LastWriteTime` 与长度均未变；`HKCU\…\Run` 下前后都没有 `Iris` 值；副本已删除。副本日志共 40 行、全部 `[INF]`，启动耗时 839.2–881.5 ms。**四轮（Light/zh-CN、Dark/zh-CN、Light/en-US、Dark/en-US）× 三个窗口 × 默认/最小尺寸**：24 张图全部拍到；`DwmGetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE)` 全部返回 `0x00000000`、读回 `3`；最小尺寸请求值与读回一致（设置 600×440、历史 280×240、翻译 280×300）。**「亚克力真的在」的可测量证据**：同一窗口分别压在纯黑底板与白底（带红绿蓝竖条）底板上，比较同一块 40×40 无控件区域的平均值——浅色 `135 → 227`（差 92），深色 `53.82 → 146.4`（差约 92.6），12 组（4 轮 × 3 窗口）全部如此，白底图里能看到竖条被模糊后透出。设置窗口的左侧导航栏与底部按钮栏两处逐像素相同（浅色 `237`、深色 `28`）：这两块本阶段没有改样式，仍是不透明底，属预期。**运行中切主题（zh-CN，用 UI Automation 只操作「主题」下拉框、不保存）**：Light→Dark 与 Dark→Light 两个方向，切换后三个已打开的窗口读回值都是 `3`，像素对比分别落在深色那组（`53.82 → 146.4`）与浅色那组（`135 → 227`）；关闭设置窗口（预览还原）后另两个窗口回到起始主题且仍是亚克力；副本 `settings.json` 的 `theme` 始终是起始值（预览没有落盘）。这是 Task 9 生产路径（`Apply(theme, None)` → 换字典 → `RefreshOpenWindows`）在真机上的直接证据。**与设计一致的系统行为**：窗口不是前台窗口时读回值仍是 `3`，但像素是纯色（两位置 99%以上像素相同）——即设计 §3.2 写的「失焦时系统显示纯色属系统行为，不处理」；由此可知**读回值 `3` 不能单独证明亚克力可见**（`WindowMaterialServiceTests` 里读 DWM 的三条用例守的是「背板装上了」，不是「看得见」）。是不是系统对所有应用都如此，没有拿别的应用对照，**未验证**。**观察到的问题（都未在本阶段处理）**：(a) 切主题时连拍（约 20–25 ms 一帧）各抓到**一帧中间态**——Light→Dark 一次是标题栏与内容区背板纯黑、卡片仍浅色，一次是背板近白不透明；Dark→Light 是导航栏深色、内容区浅色的混合帧；下一到两帧即完成。人眼是否察觉**未验证**。关设置窗口时没抓到中间帧（报告第 34 条里担心的「关窗时拆装一次背板是否闪烁」：按 20–25 ms 的采样没有看到，更短的闪烁未验证）。(b) **着色层很薄**：黑底与白底之间摆幅约 92 级，浅色主题压在本机深色桌面上时背板呈中灰（`135`），导致浅色下三处次要文字对比度偏低——设置窗口底栏「鼠标监听已关闭」、历史窗口「选择一条记录查看全文和全部服务结果」、翻译窗口自绘标题「Iris 翻译」。这是 DWM 亚克力本身的着色强度，应用侧只能靠在背板上叠自己的底色来压，属于阶段 2 起要定的设计问题，**请架构师留意**。(c) 布局类：英文下设置窗口导航项折行、最后可见项「Network and proxy」被切掉半行；历史窗口缩到 280×240 时「清空...」按钮折到第二行并被底部按钮区切掉一半（中英文都有）；英文下翻译窗口目标语言下拉框「Chinese (Simplified)」折成两行，与左边的下拉框高度不齐；设置窗口标题文字左侧约 20 px 空白、没有图标。本阶段没有改这三个窗口的布局（只加了 `ExtendsContentIntoTitleBar` 与材质），**这些是否改动前就有，未验证**；留给阶段 2–4 的布局重构。**没有看到的缺陷**：稳态下没有白底白字、黑底黑字、透明空洞或黑块；标题栏没有重复、内容没有被压住；最小化/关闭按钮可见。**本条的未验证项**：设置窗口除「常规」页外的 7 个页面；历史有记录、翻译有结果时的样子（副本是空库，没有发起翻译）；最小尺寸下的两位置像素对比（只在默认尺寸做了）；英文下的切主题；「跟随系统」主题；透明效果关闭/高对比度/Win10 下的纯色回退；非 100% DPI 与多显示器；小于 `MinWidth`/`MinHeight` 时是否被拒绝（只请求了刚好等于最小值的尺寸）；拖动窗口与双击标题栏（Task 8 Step 6，仍未验证）；设置窗口作为第三个被打开时没有成为前台窗口（是应用行为还是命令转发进程拿不到前台权，未验证）。**顺带读数**：三个窗口同时开着、启动后 22–23 秒时，`WorkingSet64` 四轮为 `221483008 / 220614656 / 218816512 / 219623424`，`PrivateMemorySize64` 为 `140083200 / 140316672 / 127623168 / 142118912`（这是「三窗口都开」的状态，与第 38 条的空闲测量不可直接比较，改动前的同状态读数没有量）；四轮各做一次 `Get-NetTCPConnection -OwningProcess`，结果全部为空（只是 TCP 的一次快照，UDP/DNS 与其它时刻未验证）。**执行中偏离派发步骤之处**：两处对比底板是执行代理自己开的无边框置顶窗口，而不是压在用户的真实窗口上；为了三窗口同开，`OpenSettings`/`OpenHistory` 每轮各发了两次；拍摄时把窗口临时置顶，切主题轮里对历史/翻译窗口调用过 `SetForegroundWindow`；应用在副本里自己把 `settings.json` 补全成完整文件并写了 `.bak` 与 `translateWindow.lastBounds`（只发生在副本里）。

38. **Task 10 Step 4 内存与启动测量：改动前后各一份完整输出（协调方执行；原始输出在 `%TEMP%\iris-t041-measure-after.log` 与 `%TEMP%\iris-t041-measure-before.log`）**。**做法**：两次都用仓库自带的 `build\measure-startup.ps1`（默认 `Runs=3`、`IdleSeconds=30`；脚本自己拷便携副本到 `%TEMP%`、量完删除）。「改动后」量的是工作区的 Release 产物（`built 2026-11-13 01:13:52`）；「改动前」是把 `HEAD`（`513ab5d`）用 `git archive` 导出到仓库外的 `%TEMP%\iris-t041-baseline-513ab5d`、在那里 `dotnet build src/Iris.App/Iris.App.csproj -c Release`（`0 个警告 / 0 个错误`，`built 2026-11-13 02:06:45`）后运行那份副本里的同一个脚本（`git diff --quiet HEAD -- build/measure-startup.ps1` 无内容差异）。两次相隔约 2 分钟、同一台机器，先量改动后、再量改动前。量完 `Iris.App` 进程数 0、`iris-measure-*` 残留目录 0。**改动后**（开始时系统 CPU 负载 27%）：

    ```
    Run StartupMs VisibleWindows WorkingSetMB PrivateBytesMB ExitCode ShutdownLogLines
      1     944.3              1        163.5           79.8 timeout                 3
      2     947.2              0        124.9           54.8 timeout                 3
      3     920.5              0        124.7           55.4 timeout                 3
    --- averages --- StartupMs=937.3  WorkingSetMB=137.7  PrivateBytesMB=63.3
    ```

    **改动前**（开始时系统 CPU 负载 16%）：

    ```
    Run StartupMs VisibleWindows WorkingSetMB PrivateBytesMB ExitCode ShutdownLogLines
      1    1054.4              1        167.1           78.4 timeout                 3
      2     887.9              0        124.8           54.5 timeout                 3
      3     997.8              0        124.7           54.6 timeout                 3
    --- averages --- StartupMs=980.0  WorkingSetMB=138.9  PrivateBytesMB=62.5
    ```

    两份里「第二实例（第 1 轮）」都是 `ExitedWithin5s : True`、`ExitCode : 0`、`LogLinesAdded : 0`（`ElapsedMs` 140 / 139）。**怎么读**：空闲状态（没有打开任何窗口）下，改动前后的 Working Set 与 Private Bytes 逐轮相差都在 4 MB 以内（第 2、3 轮相差不超过 0.8 MB），每份只有 3 轮、轮间自身波动（第 1 轮比后两轮高约 40 MB）远大于前后差，**这组数据看不出本阶段改动对空闲内存有可分辨的影响**；它不能说明「打开亚克力窗口后的内存」——那个状态只有改动后的读数（第 37 条：三窗口同开约 `219–221 MB` Working Set），**改动前的同状态读数没有量，未验证**。T-040 记录的 200 MiB 是怎么量出来的（哪个状态），本条没有核对，**不与之比较**。**两份输出里一模一样、因此不是本阶段引入的三处异常（记录在案，不在本工单范围，请架构师留意）**：(a) 三轮的 `ExitCode` 全是 `timeout`——模拟注销（`WM_QUERYENDSESSION` + `WM_ENDSESSION`）后进程 10 秒内没有退出，被脚本自己 `Stop-Process`（脚本只结束它自己启动的 PID）；(b) 第 1 轮注销期间的日志在「Iris 已退出，退出码 0」之后还有一行 `[FTL] (Iris.App.Program) Primary startup or cleanup failed: IrisException`；(c) 第 1 轮 `VisibleWindows` 是 `1`（NFR 写的是 0），只出现在启动过第二实例的那一轮。按脚本末行的 NFR 逐项对照：`StartupMs <= 1500` 两份都满足；`PrivateBytesMB <= 100` 两份都满足；`WorkingSetMB <= 160` 两份的第 1 轮都不满足（163.5 / 167.1）、平均值满足；`ExitCode = 0` 两份都不满足；`ShutdownLogLines >= 1` 两份都满足。

- **评审驱动的额外断言与测试（都不在原工单里）**：`IrisIconButtonStyleTests` 增 `Descendants<...TextBlock>(button).ShouldNotBeEmpty()`（防断言空转）与“模板里必须存在以 `IsPressed` 为条件的触发器”（否则键盘按下无反馈这个已修 bug 会静默复活）；`IrisSwitchStyleTests` 增 `SwitchStyle_KeepsWrappingContentTemplate`（自定义模板不得弄丢调用方给的 `ContentTemplate`）；`IrisColorKeyTests` 增“深色键集合等于浅色键集合”的显式断言与深色取值也过半透明断言（原本只是“数量相等 + 计数”涌现出来的性质）；**新增一条守卫测试** `tests/Iris.App.Tests/Themes/IrisStyleKeyResolutionTests.cs`，断言 `Iris.Styles.xaml` 里 `Setter.Value` 与各类触发器 setter 上的每个 `{DynamicResource X}` 键都能解析——键名拼错是静默失败（值为 null、无异常），本阶段之前没有任何东西挡得住。第一轮实现被评审查出缺口：遍历到不了模板**体内**元素属性上的引用，29 个键只收到 26 个，`ToggleSwitchFillOff` / `ToggleSwitchStrokeOff` / `ToggleSwitchKnobFillOff` 三键零自动化覆盖（恰恰就是这条守卫针对的那类缺陷）→ 已补 `IrisSwitchStyleTests.SwitchStyle_OffStateUsesTheOffStateKeys` 从消费端断言这三个键，并把守卫注释里"所有"的说法收紧成准确的作用域；另外把两处**静默跳过**改成当场报红（`ResourceKey` 不是字符串时、以及遇到认不出的触发器类型时）。
- **新增文件**：`Themes/Iris.SolidFallback.xaml` 是设计 §3.2 说的“覆盖颜色键的小字典”，设计文档没有给它命名。
- **已知局限（需要架构师裁决）**：`Iris.SolidFallback.xaml` 只有一套（浅色）值，深色主题 + 无亚克力时卡面是纯白 `#FFFFFFFF`，而 `TextFillColorPrimaryBrush` 在深色下也是白色 → **卡面标题与说明文字都看不见**（不是“浅色卡片”这么轻）。本阶段因此**不合并**它（见第 20 条），这条局限同时写在 XAML 注释里。建议作为**阶段 2 的准入条件**：加 Dark 变体，或在同一份字典里按主题给两套值。
- **选择的取舍**：开关样式用**静态 setter** 模板而不是带 `Storyboard` 的版本，理由是离屏渲染在翻转后立即取图会拍到旧状态。这是“只换模板”范围内允许的实现选择，不改规范。
- **证据位置**：Task 6 的两张 PNG 与两份 `manifest.json` 落在**仓库外**（跑测试时把 `IRIS_T041_EVIDENCE` 指到 `C:\Users\28910\AppData\Local\Temp\iris-t041-evidence`，测试每次再建一个 GUID 子目录），不入库；报告里记下绝对路径与两张图的 SHA256。人工复看由协调方做，不是实现代理做。**最终一对**（协调方复看用的就是这一对）：`...\iris-t041-evidence\iris-styles-light-f539ad3a002c46c1bc304ed10d464759\styles-light.png`（5471 B / `4FB410D515931EFA0B1EA104EB654865B2ED4A5DC459D2348E6CDC4A3C694415`）与 `...\iris-styles-dark-ff9ec25a025e4ea8800fd561b6e3bb4b\styles-dark.png`（5489 B / `100CEF3C08AAF4DB4F0850D112DF15DBEE0A1A1BA847CF95AAF063B77E18FF6F`）；`iris-styles-light-bfa9e3183baa4c7f9e51b1acb9c5e9f8\` 与 `iris-styles-dark-d0b9272b7e6444af95f54e4944225270\` 里那两个文件与它们逐字节相同（窄过滤那一次跑的）。阶段收尾清理 `iris-t041-evidence\` 时保留这四个目录之外的都不留（第一版缺陷图那一对 `iris-styles-light-22d49edb…`/`iris-styles-dark-7c262066…` 作为缺陷证据保留，见 Task 6 证据段的记录）。
- **未验证项**：拖拽窗口、双击最大化（本机前台有全屏程序拦截合成输入）、切 DPI 后亚克力是否保持（本机单显示器 100%）、非活动窗口的亚克力表现、真实窗口截图；**高对比度主题下的真实表现**（「走 DP 路径会继承 WPF-UI 的否决」这条是反编译 WPF-UI 4.3.0 得出的，本机未实测）；`WindowMaterialService` 的默认构造（`Environment.OSVersion` 与 `WindowBackdrop.IsSupported`）在真实进程里的取值（测试只走注入版本）；探测工程 `ProbeIconButtonStyle` 的点击触发方式（子代理推断，未独立验证）；Task 5 返工后那三个测试数字（4/0、1/0、41/0）是复核代理在“只读、不跑 `dotnet`”的约束下**未独立复现**的项（由 Task 10 Step 2 的全量跑覆盖）。
- **观感待复核（不是缺陷）**：深色档开关 ON 旋钮渲染为**纯黑**（实测 `(0,0,0)`），浅色档为纯白 `255,255,255`。该填充来自 WPF-UI 深色字典的 `ToggleSwitchKnobFillOn`（键被 `IrisStyleKeyResolutionTests` 证明可解析；我方模板与 WPF-UI 自带模板用的是同一个键、同一个元素），不是我方写死或未解析。轨道实测：浅档 `(0,106,187)`（`#006ABB`）、深档 `(77,178,255)`（`#4DB2FF`）——即强调色钉住之后的生产路径值（见第 22 条），不再是钉住前的 `(0,103,192)`。深色档黑钮压在 `(77,178,255)` 亮蓝轨上对比更强；浅色档白钮压在 `(0,106,187)` 上同样可辨（人工复看已确认）。**复看结论可平移**：钉住后的图与复看过的那一版逐像素只差开关轨道那一块（浅 640 px、深 649 px，边界框都是 `x[379..418] y[63..82]`），圆角、细边框、半透明、文字与图标按钮全部逐字节相同。观感仍留待第 5 阶段真窗口复核。
- **实现代理报告里未能复现的数字**：Task 6 的实现报告称 `IrisStyleRenderingTests.cs` 为 `129 行 / 9061 字节`；协调方在落盘文件上复测为 `148 行`（含 12 行空行、136 行非空）、`8166 字节`、`LF`、无 BOM、`sha256 B80714DD638C26C22C4F776B143C1DE963B8BB5694398CAC60477E35A4F9A320`（Python 计数与 `Get-FileHash` 两个独立工具）。**那组数字是第一版实现的值，已被评审修复轮取代**：修完 7 项评审意见后（并加钉强调色那一段）当前落盘文件为 `207 行`、`13042 字节`、`LF`（CR=0）、无 BOM、`sha256 2E769B038BC3FFEF2FC7D0B272BA031DC96E29D58BF458C381ACC8724A596CCD`，与计划代码块**逐字一致**（`iris_cmp_v4.py` 当时 13 项全 IDENTICAL；Task 8 后为 24 项，见第 28 条）。本报告凡引用该文件尺寸/行数一律以协调方复测值为准，不以实现代理自报数字为准。
- **改过的现有测试（逐条）**：`tests/Iris.App.Tests/TestSupport/WpfTestRunner.cs`（`AddIrisDictionaries`：补 `Iris.Light.xaml`、`Iris.Styles.xaml`，Task 1 建、Task 4 补第二个）；`tests/Iris.App.Tests/Updates/UpdateRecoveryRenderingTests.cs`（补同样两项字典，并把 `:40-41` 那句已失实的 `// Exactly App.xaml's global resources.` 换成准确说法，Task 5）；`tests/Iris.App.Tests/Settings/SettingsWindowPresenterTests.cs`（`+40 / -0`：补 `using Wpf.Ui.Controls;` 与新用例 `ShowAsync_AppliesAcrylic_WhenTheSystemSupportsIt`，既有用例一行未动，Task 8）；`tests/Iris.App.Tests/Hosting/AppHostBuilderTests.cs`（`+2 / -0`：`Host_ResolvesRequiredServices` 里补 `WindowMaterialService` 的解析与非空断言，**评审驱动、不在原工单里**，Task 8，见第 29、30 条）。Task 8 实测**没有任何**现有测试因布局位移而需要调整（见第 27 条）。
- **已知不一致（未处理）**：已提交的 `tasks/T-041.work-order-20261111.md` 写着 “`.codex`（它已被 `.gitignore` 忽略）”，但实测 `.gitignore` 里没有 `.codex` 条目，`git status` 把它显示为 `?? .codex/`。设计 §10 只要求忽略 `.superpowers/`，所以本阶段不改 `.gitignore`、不碰 `.codex`。

---

## 自检

**设计 §3 覆盖情况：**

| 设计条款 | 落在哪 |
|---|---|
| §3.1 新建 `Themes/`，样式与数值 | Task 1、2、3 |
| §3.1 `Iris.Light.xaml` / `Iris.Dark.xaml` 只放 WPF-UI 没有的键 | Task 4 |
| §3.1 不覆盖 WPF-UI 已有键 | Task 4（只定义 `IrisAcrylic*` 四个新键；测试断言这些键是半透明的） |
| §3.1 在 `App.xaml` 与两处测试资源环境合并 | Task 1 Step 4、Task 4 Step 6、Task 5 |
| §3.1 图标一律用 `SymbolIcon`，不用 `Image`，不依赖 Segoe Fluent | Task 3、Task 6 |
| §3.1 `src` 里不新增中文字面量与中文注释 | Task 10 Step 3（审计）；全部新 XAML 注释用英文 |
| §3.2 三个窗口设 `ExtendsContentIntoTitleBar="True"` | Task 8 Step 1 |
| §3.2 纯色窗口服务不碰 | Task 7（服务只被三个窗口注入调用） |
| §3.2 版本判定与纯色回退 | Task 4 Step 5、Task 7 |
| §3.2 XAML 默认纯色、句柄创建后才应用 | Task 8 Step 1、Step 3 |
| §3.2 失焦时系统显示纯色属系统行为，不处理 | 不做（写进报告） |
| §3.3 明确传入“无材质” | Task 9 Step 3 |
| §3.3 切主题后对已打开的亚克力窗口重新应用 | Task 9 Step 3、Step 6 |
| §3.3 加一条“切换主题后纯色窗口背景仍不透明”的测试 | Task 9 Step 1（`Apply_KeepsASolidWindowOpaque_OnTheProductionPath`）。原先映射到的 `ApplyAcrylic_LeavesWindowSolid_WhenUnsupported` 与 Task 9 Step 6 都不成立：前者根本不切主题，后者测的是亚克力窗口且不经过 `ThemeService`（规范评审指出，见报告第 33 条） |

**设计 §8.1 要求“测试资源环境加主题字典”** → Task 5。

**设计 §9 要求的验证** → Task 10。

**不做的事**：`specs/`（阶段 0 已改完）、`tasks/`、`.codex`、`specs/CONTRACTS.cs`、`Iris.App.csproj` 的 `ProjectReference`、任何新增依赖。

---

**Phase 1 计划到此结束。** 阶段 2（设置窗口）的计划在阶段 1 落地后再写：导航宽度与选中态、翻译服务页与 OCR 页的列表 → 详情，都依赖本阶段确定的 `IrisNavItem` 与 `IrisRowCard` 的实际观感，以及 `ExtendsContentIntoTitleBar="True"` 之后内容区从 Y=48 起的布局基线。
