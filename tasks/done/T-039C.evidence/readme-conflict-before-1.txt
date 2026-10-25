<<<<<<< HEAD
# Iris

Iris 是一个 Windows 桌面翻译工具（C# / .NET 10 / WPF），常驻系统托盘，支持全局热键、悬浮球、划词翻译与截图 OCR 翻译。

## 架构

项目采用 Clean Architecture 架构风格：
- `src/Iris.Core`: 核心领域模型、契约与流程编排（net10.0，零平台依赖）
- `src/Iris.Providers`: 翻译与 OCR 服务提供方（OpenAI 兼容协议、Anthropic 原生 SDK、百度 OCR）
- `src/Iris.Infrastructure`: 持久化、设置与本地存储
- `src/Iris.Platform.Windows`: Windows 平台集成（热键、钩子、选区、截图、WinRT OCR、DPAPI）
- `src/Iris.App`: WPF 桌面应用组合根与 UI 界面

## 构建与测试

需要 .NET 10 SDK (`10.0.100` 或更高版本)。

```bash
# 编译
dotnet build Iris.slnx -c Release

# 运行测试
dotnet test Iris.slnx -c Release
```
=======
# Iris
>>>>>>> refs/remotes/t039c-public/direct-main
