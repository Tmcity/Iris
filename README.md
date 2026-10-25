# Iris

Iris 是一个 Windows 桌面翻译工具（C# / .NET 10 / WPF），常驻系统托盘，支持全局热键、划词翻译与截图 OCR 翻译。悬浮球仍在规划中，M6 延期。

Windows x64 安装入口为公开 [GitHub Releases](https://github.com/Tmcity/Iris/releases) 中的 `Iris-<版本>-Setup.exe`，安装、运行时要求、更新及卸载说明见 [安装指南](docs/INSTALLATION.md)。首期构建未签名；下载后按该 Release 的 `SHA256.json` 核对。源代码构建成功不代表在线升级已经验证，实际验证状态见任务报告。

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

正式发布的版本计划、正常历史整合、tag/CI 准入及实际用户路径验证顺序见 [发布流程](docs/RELEASING.md)。初始公开 README 原字节保留在 [历史记录](docs/history/README.initial-github.md)。
