using Iris.Core.Errors;

namespace Iris.Platform.Windows.Capture;

internal sealed class ScreenCaptureException(ErrorCode code, string diagnostic) : Exception("Screen capture operation failed.")
{
    internal ErrorCode Code { get; } = code;
    internal string Diagnostic { get; } = diagnostic;
}

internal static class ScreenCaptureErrors
{
    internal static ScreenCaptureException Fail(string stage, ErrorCode code = ErrorCode.CaptureFailed, int? nativeError = null) =>
        new(code, $"stage={stage};native={nativeError?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NA"}");
    internal static string Describe(string stage, Exception error) => error is ScreenCaptureException known
        ? known.Diagnostic : $"stage={stage};hr=0x{error.HResult:X8}";
    internal static ScreenCaptureException Combine(string stage, Exception? body, IReadOnlyList<string> cleanup, bool cancelled) =>
        new(cleanup.Count != 0 ? ErrorCode.CaptureFailed : body is ScreenCaptureException known ? known.Code : ErrorCode.CaptureFailed,
            $"{(body == null ? $"stage={stage}" : Describe(stage, body))};cancelled={cancelled};cleanup=[{string.Join('|', cleanup)}]");
    internal static IrisException Public(Exception error) => error is ScreenCaptureException known
        ? new(known.Code, ErrorMessages.GetDefault(known.Code), detail: known.Diagnostic)
        : new(ErrorCode.CaptureFailed, ErrorMessages.GetDefault(ErrorCode.CaptureFailed), detail: Describe("UNEXPECTED", error));
}
