using System.IO;
using System.Net;
using System.Net.Http;
using Iris.App.Updates;
using Iris.Core.Errors;
using Shouldly;
using Velopack.Logging;
using Velopack.Sources;

namespace Iris.App.Tests.Updates;

public sealed class UpdateHttpTests
{
    [Fact]
    public async Task GitHubSelectsCompatibleFeedBeforeNewestReleaseAnd404RemainsFailure()
    {
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.NotFound)));
        await using (var downloader = new UpdateHttpDownloader(client, TestContext.Current.CancellationToken))
        {
            var failure = await Should.ThrowAsync<IrisException>(() => downloader.DownloadString("https://api.github.com/repos/Tmcity/Iris/releases"));
            failure.Code.ShouldBe(ErrorCode.EndpointNotFound);
        }
        var source = new UpdateRuntime.LatestGithubSource(new CompatibleFeedDownloader());
        var feed = await source.GetReleaseFeed(new NullVelopackLogger(), "Iris", "win");
        feed.Assets.ShouldBeEmpty(); // A selected, valid empty feed is subsequently classified by ValidatedUpdateSource.
    }
    [Fact]
    public async Task ManualRedirectChecksHostAndReleasesOldResponseWithoutForwardingAuthentication()
    {
        int sends = 0; var released = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            request.Headers.Contains("Authorization").ShouldBeFalse(); request.Headers.Contains("Proxy-Authorization").ShouldBeFalse(); sends++;
            if (sends == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found) { Content = new Content("redirect", released, "") };
                response.Headers.Location = new Uri("https://release-assets.githubusercontent.com/owned/feed.json?synthetic=1"); return response;
            }
            released.ShouldContain("redirect"); return new(HttpStatusCode.OK) { Content = new Content("feed", released, "{\"Assets\":[]}") };
        }));
        await using var downloader = new UpdateHttpDownloader(client, TestContext.Current.CancellationToken);
        var text = await downloader.DownloadString("https://github.com/Tmcity/Iris/releases/download/1.0.0/releases.win.json", new Dictionary<string, string> { ["Authorization"] = "ignored-synthetic" });
        text.ShouldBe("{\"Assets\":[]}"); sends.ShouldBe(2); released.ShouldContain("feed");
        Should.Throw<IrisException>(() => UpdateHttpDownloader.ValidateUri("https://evil.example.test/feed", true));
        Should.Throw<IrisException>(() => UpdateHttpDownloader.ValidateUri("http://github.com/Tmcity/Iris/releases/download/a/b", false));
    }
    [Fact]
    public async Task CancellationAfterOldReadReturnsStartsNoFurtherReadAndJoinsBeforeDisposal()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new HeldStream(entered, release);
        using var cancel = new CancellationTokenSource();
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
        var downloader = new UpdateHttpDownloader(client, cancel.Token);
        var work = downloader.DownloadString("https://api.github.com/repos/Tmcity/Iris/releases");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); cancel.Cancel(); work.IsCompleted.ShouldBeFalse();
            release.TrySetResult(); await Should.ThrowAsync<OperationCanceledException>(() => work);
            stream.Reads.ShouldBe(1); await downloader.DisposeAsync(); stream.Closed.ShouldBeTrue();
        }
        finally { release.TrySetResult(); try { await work; } catch (OperationCanceledException) { } await downloader.DisposeAsync(); }
    }
    [Fact]
    public async Task FailedDownloadClosesItsFileAndRemovesPartialBeforeReturningToSdk()
    {
        string root = Path.Combine(Path.GetTempPath(), "iris-update-partial-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string partial = Path.Combine(root, "owned.partial");
        var stream = new FailingReadStream();
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
        var downloader = new UpdateHttpDownloader(client, TestContext.Current.CancellationToken);
        try
        {
            await Should.ThrowAsync<IOException>(() => downloader.DownloadFile("https://github.com/Tmcity/Iris/releases/download/1.0.0/package.nupkg", partial, _ => { }, cancelToken: TestContext.Current.CancellationToken));
            File.Exists(partial).ShouldBeFalse(); stream.Closed.ShouldBeTrue();
        }
        finally { await downloader.DisposeAsync(); Directory.Delete(root, true); }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request)); }
    private sealed class Content(string id, List<string> released, string text) : StringContent(text)
    { protected override void Dispose(bool disposing) { if (disposing) released.Add(id); base.Dispose(disposing); } }
    private sealed class HeldStream(TaskCompletionSource entered, TaskCompletionSource release) : MemoryStream
    {
        internal int Reads; internal bool Closed;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { Reads++; entered.TrySetResult(); await release.Task; buffer.Span[0] = (byte)'{'; return 1; }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }
    private sealed class CompatibleFeedDownloader : IFileDownloader
    {
        public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30) => Task.FromResult("""
            [{"name":"unrelated","published_at":"2026-10-13T00:00:00Z","assets":[]},
             {"name":"compatible","published_at":"2026-10-12T00:00:00Z","assets":[{"name":"releases.win.json","browser_download_url":"https://github.com/Tmcity/Iris/releases/download/compatible/releases.win.json"}]}]
            """);
        public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        {
            url.ShouldContain("/compatible/"); return Task.FromResult("{\"Assets\":[]}"u8.ToArray());
        }
        public Task DownloadFile(string url, string targetFile, Action<int> progress, IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default) => throw new InvalidOperationException();
    }
    private sealed class FailingReadStream : MemoryStream
    {
        private int _reads; internal bool Closed;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_reads++ != 0) throw new IOException("synthetic incomplete response");
            buffer.Span[0] = 1; return ValueTask.FromResult(1);
        }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }
}
