namespace Iris.Platform.Windows.Selection;

/// <summary>
/// 目标窗口与前台验证适配器。
/// 用于在子进程启动前及结果发布前，复核源窗口有效、前台归属一致且非 Iris 自身。
/// 此验证绝不抢焦点、不激活窗口。
/// </summary>
internal interface ITargetWindowValidator
{
    bool IsTargetValidAndForeground(nint expectedWindow, uint expectedProcessId);
}
