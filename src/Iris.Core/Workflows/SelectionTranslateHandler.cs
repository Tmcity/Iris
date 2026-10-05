using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Microsoft.Extensions.Logging;

namespace Iris.Core.Workflows;

/// <summary>以同一操作租约读取当前选区并发布窗口或安全通知。</summary>
public sealed partial class SelectionTranslateHandler(
    ITranslateWindowPresenter presenter, ISelectionTextReader reader, ISettingsService settings,
    IDesktopContext desktop, ILogger<SelectionTranslateHandler> logger) : IActionHandler
{
    public IrisAction Action => IrisAction.SelectionTranslate;

    public async Task ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        bool icon = context.Trigger == ActionTrigger.SelectionIcon;
        bool auto = context.Trigger == ActionTrigger.MouseSelection;
        var expected = context.ExpectedSelectionSource;
        if ((icon || auto) && expected == null || expected != null && !ValidSource(expected)
            || icon && (string.IsNullOrWhiteSpace(context.Payload) || context.Payload.Length > 65_536)
            || auto && !string.IsNullOrEmpty(context.Payload))
            throw new IrisException(ErrorCode.InvalidArgument, ErrorMessages.GetDefault(ErrorCode.InvalidArgument));
        expected = expected == null ? null : expected with { WindowTitle = null };
        SelectionSettings? mouseSettings = null;
        if (icon || auto)
        {
            mouseSettings = settings.Current.Selection;
            if (mouseSettings.MouseMode != (icon ? MouseSelectionMode.ShowIcon : MouseSelectionMode.AutoTranslate)) return;
        }
        using var operation = presenter.TryBeginOperation(cancellationToken);
        if (operation == null) return;

        string? text = null;
        ErrorInfo? error = null;
        bool ownForeground = false;
        bool openInput = false;
        string stage = "SETTINGS";
        try
        {
            // Desktop queries are synchronous native work; never run them on App's UI thread.
            var captured = await Task.Run(() =>
            {
                var selection = mouseSettings ?? settings.Current.Selection;
                var options = new SelectionReadOptions(selection.ReadOrder.ToArray(), selection.RestoreClipboard,
                    selection.ClipboardTimeoutMs, selection.ClipboardUnsafeProcesses.ToArray()) { ExpectedSource = expected };
                bool emptyInput = selection.OpenInputWhenEmpty;
                stage = "DESKTOP";
                ForegroundAppInfo? source;
                try { source = desktop.GetForegroundApp(); }
                catch (Exception ex) when (expected != null)
                {
                    LogReadFailed(logger, "EXPECTED_SOURCE", ErrorCode.CaptureFailed, ex.GetType().Name);
                    source = null;
                }
                bool own = expected == null && source?.ProcessId == Environment.ProcessId;
                bool matches = expected == null || source != null && source.ProcessId != Environment.ProcessId && source.ProcessId == expected.ProcessId && source.WindowHandle == expected.WindowHandle
                    && string.Equals(source.ProcessName, expected.ProcessName, StringComparison.OrdinalIgnoreCase);
                return (Options: options, OpenInput: emptyInput, Own: own, Matches: matches);
            }, operation.CancellationToken).ConfigureAwait(false);
            if (!captured.Matches) return;
            ownForeground = captured.Own;
            openInput = captured.OpenInput;
            if (icon) text = context.Payload;
            else if (!ownForeground)
            {
                stage = "READ";
                var result = await reader.ReadAsync(captured.Options, operation.CancellationToken).ConfigureAwait(false);
                text = result?.Text;
            }
        }
        catch (OperationCanceledException) { return; }
        catch (IrisException ex)
        {
            LogReadFailed(logger, stage, ex.Code, ex.GetType().Name);
            error = new(ex.Code, ErrorMessages.GetDefault(ex.Code));
        }
        catch (Exception ex)
        {
            LogReadFailed(logger, stage, ErrorCode.Unknown, ex.GetType().Name);
            error = new(ErrorCode.Unknown, ErrorMessages.GetDefault(ErrorCode.Unknown));
        }

        // Publication failures belong to Dispatcher; do not recursively publish another error.
        if ((icon || auto) && error == null && string.IsNullOrWhiteSpace(text)) return;
        if (error == null && !ownForeground && string.IsNullOrWhiteSpace(text) && !openInput)
        {
            await presenter.TryNotifyAsync(operation, NotificationLevel.Warning, ErrorCode.SelectionEmpty).ConfigureAwait(false);
            return;
        }
        var request = error != null
            ? new TranslateWindowRequest { Text = null, Origin = TextOrigin.Selection, Anchor = context.Anchor, AutoTranslate = false, FocusInput = false, Error = error }
            : ownForeground
                ? new TranslateWindowRequest { Text = null, Origin = TextOrigin.Input, Anchor = context.Anchor, AutoTranslate = false, FocusInput = true }
                : string.IsNullOrWhiteSpace(text)
                    ? new TranslateWindowRequest { Text = "", Origin = TextOrigin.Selection, Anchor = context.Anchor, AutoTranslate = false, FocusInput = true, Error = new(ErrorCode.SelectionEmpty, ErrorMessages.GetDefault(ErrorCode.SelectionEmpty)) }
                    : new TranslateWindowRequest { Text = text, Origin = TextOrigin.Selection, Anchor = context.Anchor, AutoTranslate = true, FocusInput = false };
        await presenter.TryShowAsync(operation, request).ConfigureAwait(false);
    }

    private static bool ValidSource(ForegroundAppInfo source) => source.WindowHandle != 0 && source.ProcessId > 0 &&
        !string.IsNullOrWhiteSpace(source.ProcessName) && source.ProcessName.IndexOfAny(['\\', '/', ':', '\0']) < 0;

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "选区前置工作失败: Stage={Stage}, ErrorCode={ErrorCode}, ExceptionType={ExceptionType}")]
    private static partial void LogReadFailed(ILogger logger, string stage, ErrorCode errorCode, string exceptionType);
}
