using System.Collections.Concurrent;
using Iris.Core.Imaging;
using Iris.Core.Ocr;
using Iris.Core.Presentation;

// External boundaries only. Actual handlers, dispatcher and presentation remain product code.
sealed class CaptureBoundary : IScreenshotService
{
    internal readonly ConcurrentQueue<(ScreenshotPurpose Purpose,CancellationToken Token)> Calls=new();
    internal Func<ScreenshotPurpose,CancellationToken,Task<ScreenshotResult?>> Body = (_,_)=>Task.FromResult<ScreenshotResult?>(null);
    public Task<ScreenshotResult?> CaptureRegionAsync(ScreenshotPurpose purpose,CancellationToken cancellationToken)
    { Calls.Enqueue((purpose,cancellationToken));return Body(purpose,cancellationToken); }
}

sealed class RecognitionBoundary : IOcrService
{
    internal readonly ConcurrentQueue<(ImageData Image,string? Provider,CancellationToken Token)> Calls=new();
    internal Func<ImageData,string?,CancellationToken,Task<OcrOutcome>> Body = (_,_,_)=>
        Task.FromResult(new OcrOutcome(new("review",[],null,TimeSpan.Zero),"Review OCR text",false));
    public Task<OcrOutcome> RecognizeAsync(ImageData image,string? providerId=null,CancellationToken cancellationToken=default)
    { Calls.Enqueue((image,providerId,cancellationToken));return Body(image,providerId,cancellationToken); }
    public void StopAccepting() { }
    public Task StopAsync(CancellationToken cancellationToken=default)=>Task.CompletedTask;
    public Task RetryCleanupAsync(CancellationToken cancellationToken=default)=>Task.CompletedTask;
    public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
}
