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
            return ErrorMessages.GetDefault(irisEx.Code);
        }

        return $"{UiText.FatalUnknown}\n{ErrorMessages.GetDefault(ErrorCode.Unknown)}";
    }
}
