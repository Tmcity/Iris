using System.Runtime.ExceptionServices;
using Iris.Core.Common;
using Iris.Core.Imaging;
using Iris.Core.Ocr;
using Iris.Core.Settings;
using NSubstitute;

namespace Iris.Core.Tests.Ocr;

internal sealed class OcrPipelineTestScope : IAsyncDisposable
{
    internal readonly IOcrProviderRegistry Registry = Substitute.For<IOcrProviderRegistry>();
    internal readonly IImagePreprocessor Images;
    internal readonly Provider Main = new("main"), Fallback = new("fallback");
    internal readonly Snapshot Lease = new();
    internal readonly List<(ImageData Image, ImageConstraints Constraints)> Prepared = [];
    internal readonly List<(Task Task, Type? Error)> Tasks = [];
    internal readonly List<Action> Releases = [];
    internal readonly ImageData Image = new([1, 2, 3], ImageFormat.Png, 40, 40);
    internal Func<ImageData, ImageConstraints, CancellationToken, Task<ImageData>>? Prepare;
    internal OcrService Service { get; }
    internal OcrPipelineTestScope()
    {
        Lease.Providers = [Registration(Main), Registration(Fallback)];
        Registry.AcquireSnapshotAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<IOcrProviderSnapshot>(Lease));
        Images = new Preparation(this);
        Service = new(Registry, Images, new OcrTextNormalizer());
    }
    internal static OcrProviderRegistration Registration(Provider provider) => new(new() { Id = provider.Id, DisplayName = provider.Id, Kind = OcrProviderKind.WindowsLocal }, provider, Guid.NewGuid());
    internal Task<OcrOutcome> Start(Type? error = null, string? id = null, CancellationToken token = default)
    { var task = Service.RecognizeAsync(Image, id, token); Tasks.Add((task, error)); return task; }
    internal (Task Entered, Func<Task> Wait, Action Open) Gate()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action open = () => release.TrySetResult(); Releases.Add(open);
        return (entered.Task, async () => { entered.TrySetResult(); await release.Task; }, open);
    }
    public ValueTask DisposeAsync() => Service.DisposeAsync();
    private sealed class Preparation(OcrPipelineTestScope owner) : IImagePreprocessor
    {
        public Task<ImageData> PrepareAsync(ImageData image, ImageConstraints constraints, CancellationToken cancellationToken)
        {
            owner.Prepared.Add((image, constraints));
            return owner.Prepare?.Invoke(image, constraints, cancellationToken) ?? Task.FromResult(image);
        }
    }
    internal static async Task Run(Func<OcrPipelineTestScope, Task> body)
    {
        var f = new OcrPipelineTestScope(); Exception? primary = null; var errors = new List<Exception>();
        try { await body(f); } catch (Exception ex) { primary = ex; }
        finally
        {
            foreach (var open in f.Releases) open(); f.Lease.Fail = false;
            foreach (var (task, error) in f.Tasks) try { await task; } catch (Exception ex) { if (error == null || !error.IsInstanceOfType(ex)) errors.Add(ex); }
            try { await f.Service.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
        }
        if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) errors.Insert(0, primary);
        if (errors.Count != 0) throw new AggregateException(errors);
    }
    internal sealed class Snapshot : IOcrProviderSnapshot
    {
        public AppSettings Settings { get; set; } = new() { Ocr = new() { ActiveProviderId = "main", FallbackProviderId = "fallback", LanguageHint = "ja", TextLayout = OcrTextLayout.MergeParagraphs } };
        public IReadOnlyList<OcrProviderRegistration> Providers { get; set; } = [];
        internal bool Fail;
        internal int Calls;
        internal Func<Task>? Before;
        public async ValueTask DisposeAsync() { Interlocked.Increment(ref Calls); if (Before != null) await Before(); if (Fail) throw new IOException("private image cleanup"); }
    }
    internal sealed class Provider(string id) : IOcrProvider
    {
        internal string Id => id;
        internal bool Configured = true;
        internal Exception? ConstraintError;
        internal Func<OcrRequest, CancellationToken, Task<OcrResult>>? Recognize;
        internal readonly List<OcrRequest> Requests = [];
        internal OcrResult Result = new(id, [new("你"), new("好")], null, TimeSpan.FromMilliseconds(7));
        internal ImageConstraints Limits = new(10000, false, 100, 40, [ImageFormat.Png]);
        public ProviderDescriptor Descriptor => new(id, id, "WindowsLocal", null, Configured);
        public ImageConstraints Constraints => ConstraintError == null ? Limits : throw ConstraintError;
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken)
        { Requests.Add(request); return Recognize?.Invoke(request, cancellationToken) ?? Task.FromResult(Result); }
        public Task<ProviderTestResult> TestAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
