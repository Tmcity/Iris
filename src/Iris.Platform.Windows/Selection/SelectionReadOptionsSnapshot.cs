using Iris.Core.Errors;
using Iris.Core.Platform;
using Iris.Core.Settings;

namespace Iris.Platform.Windows.Selection;

/// <summary>平台和 App 协调器共享的纯捕获；不启动任何服务或原生工作。</summary>
internal static class SelectionReadOptionsSnapshot
{
    internal static SelectionReadOptions Freeze(SelectionReadOptions options)
    {
        if (options?.Order == null || options.ClipboardUnsafeProcesses == null || options.ClipboardTimeoutMs is < 100 or > 2000)
            throw Invalid();
        var order = options.Order.ToArray();
        var excluded = options.ClipboardUnsafeProcesses.ToArray();
        if (order.Any(method => method is not SelectionReadMethod.UIAutomation and not SelectionReadMethod.Clipboard) || excluded.Any(name => name == null))
            throw Invalid();
        var source = options.ExpectedSource;
        if (source != null && (source.WindowHandle == 0 || source.ProcessId <= 0 || string.IsNullOrWhiteSpace(source.ProcessName) ||
            source.ProcessName.IndexOfAny(['\\', '/', ':', '\0']) >= 0)) throw Invalid();
        return new(order.Distinct().ToArray(), options.RestoreClipboard, options.ClipboardTimeoutMs, excluded)
        {
            ExpectedSource = source == null ? null : source with { WindowTitle = null },
        };
    }

    internal static bool Matches(ForegroundAppInfo? actual, ForegroundAppInfo expected) => actual != null &&
        actual.WindowHandle == expected.WindowHandle && actual.ProcessId == expected.ProcessId &&
        string.Equals(actual.ProcessName, expected.ProcessName, StringComparison.OrdinalIgnoreCase);

    private static IrisException Invalid() => new(ErrorCode.InvalidArgument, "SELECTION_OPTIONS_INVALID");
}
