using System.Collections.Concurrent;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using Iris.Core.Common;
using Iris.Core.Imaging;
using Iris.Core.Ocr;
using Iris.Core.Settings;
using Iris.Providers.Ocr.Baidu;
using Microsoft.Extensions.Time.Testing;

namespace Iris.Providers.Tests.Ocr.Baidu;

internal sealed class BaiduTestScope : IHttpClientFactory, IDisposable
{
    internal const string ApiKey = "synthetic-api+ &=?#%", Secret = "synthetic-secret+ &=?#%", TokenValue = "synthetic-token+ &=?#%";
    internal readonly Clock Time = new();
    internal readonly ConcurrentQueue<(Uri Uri, string? Body, string? Type)> Requests = new();
    internal readonly ConcurrentQueue<BodyStream> Streams = new();
    internal readonly ConcurrentQueue<Client> Clients = new();
    internal readonly List<(Task Task, Type? Expected)> Tasks = [];
    internal readonly List<Action> Releases = [];
    internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Respond { get; set; }
    internal Func<Task>? BeforeRead { get; set; }
    internal Action? BeforeStreamDispose { get; set; }
    internal bool UnknownContentLength { get; set; }
    internal bool FailStreamDispose { get; set; }
    internal bool FailClientDispose { get; set; }
    internal Exception? ReadFailure { get; set; }
    internal BaiduOcrProvider Provider { get; }
    private readonly Handler _handler;
    internal BaiduTestScope(OcrProviderProfile? profile = null, ProviderCredentials? credentials = null)
    {
        _handler = new(this);
        Provider = (BaiduOcrProvider)new BaiduOcrProviderFactory(this, Time).Create(profile ?? Profile(), credentials ?? new(ApiKey, Secret));
    }
    public HttpClient CreateClient(string name)
    {
        if (name != Iris.Providers.Http.HttpClientNames.Ocr) throw new InvalidOperationException("Wrong named client.");
        var client = new Client(this, _handler) { Timeout = Timeout.InfiniteTimeSpan }; Clients.Enqueue(client); return client;
    }
    internal static OcrProviderProfile Profile(BaiduOcrMode mode = BaiduOcrMode.AccurateBasic, bool paragraph = true) =>
        new() { Id = "synthetic-provider", DisplayName = "Synthetic", Kind = OcrProviderKind.BaiduCloud,
            BaiduOptions = new() { Mode = mode, BaseUrl = "https://example.test/gateway/v1/", Paragraph = paragraph } };
    internal static OcrRequest Input(string language = "auto", byte[]? bytes = null) => new(new(bytes ?? [1, 2, 3], ImageFormat.Png, 20, 20), language);
    internal static bool IsToken(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal);
    internal HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        var stream = new BodyStream(this, Encoding.UTF8.GetBytes(json)); Streams.Enqueue(stream);
        return new(status) { Content = new StreamContent(stream) };
    }
    internal HttpResponseMessage Token(string? token = null, long expiry = 2592000) =>
        Json(System.Text.Json.JsonSerializer.Serialize(new { access_token = token ?? TokenValue, expires_in = expiry }));
    internal T Track<T>(T task, Type? expected = null) where T : Task { Tasks.Add((task, expected)); return task; }
    internal Task<OcrResult> Recognize(OcrRequest? input = null, Type? expected = null, CancellationToken token = default) =>
        Track(Provider.RecognizeAsync(input ?? Input(), token), expected);
    internal (Task Entered, Action Release) Gate(out Func<Task> wait)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        wait = async () => { entered.TrySetResult(); await release.Task; };
        Action open = () => release.TrySetResult(); Releases.Add(open); return (entered.Task, open);
    }
    internal static async Task Run(Func<BaiduTestScope, Task> body, OcrProviderProfile? profile = null, ProviderCredentials? credentials = null)
    {
        var f = new BaiduTestScope(profile, credentials); Exception? primary = null; var errors = new List<Exception>();
        try { await body(f); } catch (Exception ex) { primary = ex; }
        finally
        {
            foreach (var release in f.Releases) release();
            f.FailStreamDispose = f.FailClientDispose = false; f.BeforeRead = null; f.ReadFailure = null; f.BeforeStreamDispose = null; f.Time.FailDispose = false;
            var stop = f.Provider.DisposeAsync().AsTask();
            foreach (var (task, expected) in f.Tasks)
                try { await task; } catch (Exception ex) { if (expected == null || !expected.IsInstanceOfType(ex)) errors.Add(ex); }
            try { await stop; } catch (Exception ex) { errors.Add(ex); }
            if (f.Streams.Any(s => !s.Released) || f.Clients.Any(c => !c.Released)) errors.Add(new InvalidOperationException("Owned mock resource was not released."));
            f._handler.Dispose();
        }
        if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) errors.Insert(0, primary);
        if (errors.Count != 0) throw new AggregateException(errors);
    }
    public void Dispose() { Provider.Dispose(); _handler.Dispose(); }
    private sealed class Handler(BaiduTestScope owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            owner.Requests.Enqueue((request.RequestUri!, body, request.Content?.Headers.ContentType?.MediaType));
            return owner.Respond != null ? await owner.Respond(request, cancellationToken) : IsToken(request) ? owner.Token() : owner.Json("{\"words_result\":[]}");
        }
    }
    internal sealed class Client(BaiduTestScope owner, HttpMessageHandler handler) : HttpClient(handler, disposeHandler: false)
    {
        internal bool Released;
        protected override void Dispose(bool disposing)
        {
            if (disposing && owner.FailClientDispose) throw new IOException("private client cleanup", unchecked((int)0x81230003));
            base.Dispose(disposing); Released = true;
        }
    }
    internal sealed class BodyStream(BaiduTestScope owner, byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        internal int InFlight;
        internal long TotalRead;
        public override bool CanSeek => !owner.UnknownContentLength && base.CanSeek;
        internal bool Released;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref InFlight);
            try
            {
                if (owner.BeforeRead != null) await owner.BeforeRead();
                if (owner.ReadFailure != null) throw owner.ReadFailure;
                int read = await base.ReadAsync(buffer, CancellationToken.None); TotalRead += read; return read;
            }
            finally { Interlocked.Decrement(ref InFlight); }
        }
        protected override void Dispose(bool disposing)
        {
            if (InFlight != 0) throw new InvalidOperationException("Early stream disposal.");
            owner.BeforeStreamDispose?.Invoke();
            if (disposing && owner.FailStreamDispose) throw new IOException("private stream cleanup", unchecked((int)0x81230002));
            base.Dispose(disposing); Released = true;
        }
    }
    internal sealed class Clock : TimeProvider
    {
        internal readonly FakeTimeProvider Inner = new();
        internal bool FailDispose { get; set; }
        internal Action? AfterTimestampRead { get; set; }
        private readonly ConcurrentDictionary<long, TaskCompletionSource> _created = new();
        public override long TimestampFrequency => Inner.TimestampFrequency;
        public override long GetTimestamp() { long value = Inner.GetTimestamp(); AfterTimestampRead?.Invoke(); return value; }
        public override DateTimeOffset GetUtcNow() => Inner.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = Inner.CreateTimer(callback, state, dueTime, period);
            _created.GetOrAdd(dueTime.Ticks, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            return new Timer(this, timer);
        }
        private sealed class Timer(Clock owner, ITimer inner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);
            public void Dispose() { if (owner.FailDispose) throw new IOException("private timer"); inner.Dispose(); }
            public async ValueTask DisposeAsync() { if (owner.FailDispose) throw new IOException("private timer"); await inner.DisposeAsync(); }
        }
        internal Task TimerCreated(TimeSpan due) => _created.GetOrAdd(due.Ticks, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        internal void Advance(TimeSpan duration) => Inner.Advance(duration);
    }
}
