using System;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Errors;

namespace Iris.App.Translation;

internal sealed partial class TranslateWindowViewModel
{
    private long _inputRevision;

    internal void AcceptSourceCopy(string text, TranslateWindowOperation operation)
    {
        TrackCopy(text, operation.CancellationToken, operation, _inputRevision);
    }

    private bool CanCopySource(string text, long revision, TranslateWindowOperation operation) =>
        !_disposed && _inputRevision == revision && string.Equals(_inputText, text, StringComparison.Ordinal)
        && IsCurrentOperation(operation) && _presenterRef?.IsCurrentVisibleOperation(operation) == true;

    private async Task CopySourceAsync(QueuedCopyItem item)
    {
        await _uiDispatcher.InvokeAsync(async () =>
        {
            if (item.CancelRequested || item.Cts.IsCancellationRequested
                || !CanCopySource(item.Text, item.SourceRevision, item.SourceOperation!)) return;
            // The shared copy queue keeps exactly one real write in flight, including
            // a clipboard that ignores cancellation, after the caller returns its lease.
            await _clipboardService.SetTextAsync(item.Text, item.Cts.Token).ConfigureAwait(false);
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task ShowSourceCopyErrorAsync(QueuedCopyItem item, Exception exception)
    {
        var code = exception is IrisException known && Enum.IsDefined(known.Code) ? known.Code : ErrorCode.Unknown;
        try
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (CanCopySource(item.Text, item.SourceRevision, item.SourceOperation!))
                    Error = new ErrorInfo(code, ErrorMessages.GetDefault(code));
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception displayError) { LogClipboardError(_logger, displayError.GetType().Name); }
    }
}
