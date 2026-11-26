# Iris

一个轻量、安静的 Windows 桌面翻译工具，常驻系统托盘，支持输入翻译、划词翻译和截图 OCR。你可以直接接入自己的大模型或翻译接口，把常用功能收进快捷键里，随手调出、用完即走。

## 功能特点

- **输入翻译 & 并发对比**：按快捷键（默认 `Ctrl+Alt+F`）呼出翻译窗口，打字停顿 1 秒自动开翻，也可以按 `Enter` 立即触发；支持同时配置多个服务并发输出，方便对比译文。
- **划词翻译**：选中文本后按 `Ctrl+Alt+D` 翻译，或开启鼠标划词（在光标旁显示小图标 / 划选后直接翻译）。优先通过 Windows UI Automation 读取文本，避免无故污染剪贴板；使用剪贴板回退时也会尽量恢复原有内容，并自动避开终端等按 `Ctrl+C` 会触发中断的程序。
- **截图识别与翻译**：`Ctrl+Alt+S` 框选区域直接翻译，`Ctrl+Alt+O` 仅识别文字（支持系统内置 Windows OCR 与百度智能云 OCR）。
- **本地历史与隐私**：翻译记录保存在本地 SQLite 数据库，支持搜索、收藏与原样重译；API 密钥通过 Windows DPAPI 加密保存在本机，请求只发往你配置的服务端点。
- **原生 Windows 11 观感**：设置、翻译和历史窗口采用亚克力材质并跟随系统浅色/深色主题（在 Windows 10 等不支持亚克力的环境上自动回退为纯色背景）。

## 安装与更新

从 [GitHub Releases](https://github.com/Tmcity/Iris/releases) 下载最新版本的 `Iris-<版本>-Setup.exe`（仅支持 Windows x64）。

- 安装包会在本地缺少 `.NET 10 Desktop Runtime` 时自动拉取微软官方运行时；首次冷安装受系统策略检查影响可能需要几分钟，之后应用内检查更新通常几秒即可完成。
- 目前安装包尚未做代码签名，首次运行如果遇到 Windows SmartScreen 提示，可结合发布页附带的 `SHA256.json` 核对文件哈希后放行。
- 更多安装参数、目录位置与卸载说明见 [安装指南](docs/INSTALLATION.md)。

## 从源码构建

需要安装 `.NET 10 SDK`（`10.0.100` 及以上版本）：

```powershell
# Release 构建
dotnet build Iris.slnx -c Release

# 运行全量测试
dotnet test Iris.slnx -c Release
```

### 目录结构

- `src/Iris.Core`：核心模型、设置结构与翻译/OCR/划词主流程（无平台耦合）
- `src/Iris.Providers`：服务接入层（OpenAI 兼容接口、Claude / Anthropic、百度 OCR）
- `src/Iris.Infrastructure`：本地设置读写、SQLite 历史库与缓存
- `src/Iris.Platform.Windows`：Windows 平台能力（全局热键、鼠标钩子、UI Automation 选区、截图、Windows 本地 OCR、DPAPI 加密）
- `src/Iris.App`：WPF 桌面端入口与界面

打包脚本与发布检查流程见 [发布流程](docs/RELEASING.md)。
