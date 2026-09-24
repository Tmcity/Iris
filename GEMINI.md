# Iris — 开发助手须知

你正在协助开发 Iris（C# / .NET 10 / WPF 翻译工具）。开始任何工作之前：

1. 阅读 `tasks/current_task.md`：只实现其中列出的范围，不做范围外的改动。
2. 契约以 `specs/CONTRACTS.cs` 为唯一依据：不得修改已落地契约的签名；需要变更时写入 `tasks/CONTRACT_CHANGE_REQUESTS.md`。
3. 架构与规范见 `specs/ARCHITECTURE.md`，重点：§0 铁律、§4.2 依赖规则、§10 平台陷阱、§16 编码规范。
4. 历史数据库结构见 `specs/DB_SCHEMA.sql`；设置文件格式见 `specs/settings.example.json`。
5. 完成前必须执行：`dotnet build Iris.slnx -c Release`（零警告）和 `dotnet test Iris.slnx -c Release`（全绿）。
6. 测试中禁止真实联网；禁止在代码、日志、异常消息中出现 API Key。
