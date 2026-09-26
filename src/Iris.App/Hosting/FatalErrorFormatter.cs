using System;
using Iris.Core.Errors;

namespace Iris.App.Hosting;

/// <summary>
/// 致命错误弹窗消息格式化辅助类。
/// </summary>
internal static class FatalErrorFormatter
{
    public static string Format(Exception ex)
    {
        if (ex is IrisException irisEx)
        {
            return string.IsNullOrEmpty(irisEx.Detail)
                ? irisEx.Message
                : $"{irisEx.Message}\n{irisEx.Detail}";
        }

        return $"{UiText.FatalUnknown}\n{ex.Message}";
    }
}
