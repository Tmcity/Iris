using Iris.Core.Errors;

namespace Iris.App.Capture;

// Only controlled internal diagnostics receive this marker; external exception text is never imported.
internal sealed class ScreenshotException(ErrorCode code, string diagnostic)
    : IrisException(code, ErrorMessages.GetDefault(code), detail: diagnostic);

internal static class ScreenshotErrors
{
    internal static string Describe(string stage, Exception error) => error switch
    {
        ScreenshotException known => $"{stage}[{known.Detail}]",
        AggregateException aggregate => $"{stage}[{string.Join(';', aggregate.InnerExceptions.Select(e => Describe("ERROR", e)))}]",
        _ => $"{stage}=0x{error.HResult:X8}"
    };

    internal static ScreenshotException Wrap(string stage, Exception error) =>
        new(error is IrisException iris ? iris.Code : ErrorCode.CaptureFailed, Describe(stage, error));
}
