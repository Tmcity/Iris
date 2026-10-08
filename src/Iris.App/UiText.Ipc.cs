namespace Iris.App;

internal static partial class UiText
{
    public const string CommandLineUsage = "Iris 命令行用法：\n无参数：启动托盘；已运行时打开输入翻译。\n--action <动作名称>：执行已开放动作（不接受数字）。\n--translate <文本>：翻译完整文本。TranslateText 必须使用此形式。\n--help 或 -h：显示帮助。\n每次只能使用一种形式，文本作为一个参数传入。";
    public const string CommandLineInvalid = "命令行参数无效。\n\n" + CommandLineUsage;
    public const string IpcUnavailable = "无法连接到当前用户会话中的 Iris，请稍后重试。";
    public const string IpcUnconfirmed = "未能确认命令是否已接收。命令可能已经执行，请先检查 Iris 的状态；本次不会自动重发。";
    public const string IpcRejected = "Iris 未接纳此命令，可能正在退出或忙于处理已有请求。";
    public const string StartupFailed = "Iris 启动或清理失败，请稍后重试。";
}
