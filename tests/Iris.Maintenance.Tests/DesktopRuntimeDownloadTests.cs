using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Shouldly;

namespace Iris.Maintenance.Tests;

public sealed class DesktopRuntimeDownloadTests
{
    private static readonly byte[] s_installer = "synthetic Microsoft runtime payload; never executed"u8.ToArray();

    [Fact]
    public async Task InstalledRuntimeAvoidsAllTransportAndSignatureWork()
    {
        using var files = new OwnedFiles();
        using var http = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("No request expected")));
        await new DesktopRuntimeDownload(http, files.Cache, files.Work, () => true, _ => throw new InvalidOperationException("No verification expected"))
            .PrepareCacheAsync(TestContext.Current.CancellationToken);
        File.Exists(files.Cache).ShouldBeFalse();
    }

    [Fact]
    public async Task ValidatedPayloadIsPublishedOnlyAfterHashAndSignatureWithNoIdentityHeaders()
    {
        using var files = new OwnedFiles();
        int calls = 0, signatures = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            request.Headers.Authorization.ShouldBeNull(); request.Headers.Contains("Cookie").ShouldBeFalse();
            request.RequestUri!.Host.ShouldBe("dotnetcli.blob.core.windows.net");
            return Task.FromResult(Ok(++calls == 1 ? Metadata() : s_installer));
        }));
        await new DesktopRuntimeDownload(http, files.Cache, files.Work, () => false, path =>
        {
            File.Exists(files.Cache).ShouldBeFalse(); File.ReadAllBytes(path).ShouldBe(s_installer); signatures++;
        }).PrepareCacheAsync(TestContext.Current.CancellationToken);
        File.ReadAllBytes(files.Cache).ShouldBe(s_installer); calls.ShouldBe(2); signatures.ShouldBe(1);
        Directory.GetFiles(files.Work).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("redirect")]
    [InlineData("metadata-budget")]
    [InlineData("foreign-source")]
    [InlineData("hash")]
    [InlineData("signature")]
    [InlineData("truncated")]
    public async Task DownloadFailuresNeverPublishOrLeaveOwnedPartialFiles(string scenario)
    {
        using var files = new OwnedFiles();
        int calls = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            if (calls == 1)
            {
                if (scenario == "redirect") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Content = new ByteArrayContent([]) });
                var metadata = Ok(Metadata(scenario == "foreign-source" ? "https://example.test/runtime.exe" : null));
                if (scenario == "metadata-budget") metadata.Content.Headers.ContentLength = 3 * 1024 * 1024;
                return Task.FromResult(metadata);
            }
            var payload = Ok(scenario == "hash" ? "changed"u8.ToArray() : s_installer);
            if (scenario == "truncated") payload.Content.Headers.ContentLength = s_installer.Length + 1;
            return Task.FromResult(payload);
        }));
        var download = new DesktopRuntimeDownload(http, files.Cache, files.Work, () => false,
            _ => { if (scenario == "signature") throw new InvalidDataException("synthetic signer rejection"); });
        await Should.ThrowAsync<Exception>(() => download.PrepareCacheAsync(TestContext.Current.CancellationToken));
        File.Exists(files.Cache).ShouldBeFalse();
        if (Directory.Exists(files.Work)) Directory.GetFiles(files.Work).ShouldBeEmpty();
        if (scenario is "redirect" or "metadata-budget" or "foreign-source") calls.ShouldBe(1);
    }

    [Fact]
    public async Task ValidExistingCacheIsVerifiedAndCorruptCacheIsPreservedWithoutExecution()
    {
        using var files = new OwnedFiles();
        Directory.CreateDirectory(Path.GetDirectoryName(files.Cache)!);
        File.WriteAllBytes(files.Cache, s_installer);
        int signatures = 0, requests = 0;
        using var http = new HttpClient(new Handler((_, _) => { requests++; return Task.FromResult(Ok(Metadata())); }));
        var download = new DesktopRuntimeDownload(http, files.Cache, files.Work, () => false, _ => signatures++);
        await download.PrepareCacheAsync(TestContext.Current.CancellationToken);
        requests.ShouldBe(1); signatures.ShouldBe(1);
        byte[] corrupted = "owned corrupt cache"u8.ToArray(); File.WriteAllBytes(files.Cache, corrupted);
        await Should.ThrowAsync<InvalidDataException>(() => download.PrepareCacheAsync(TestContext.Current.CancellationToken));
        File.ReadAllBytes(files.Cache).ShouldBe(corrupted); signatures.ShouldBe(1);
    }

    [Fact]
    public async Task CacheAppearingDuringDownloadIsNotOverwritten()
    {
        using var files = new OwnedFiles(); int calls = 0;
        byte[] other = "another owned test cache"u8.ToArray();
        using var http = new HttpClient(new Handler((_, _) =>
        {
            if (++calls == 1) return Task.FromResult(Ok(Metadata()));
            Directory.CreateDirectory(Path.GetDirectoryName(files.Cache)!); File.WriteAllBytes(files.Cache, other);
            return Task.FromResult(Ok(s_installer));
        }));
        var download = new DesktopRuntimeDownload(http, files.Cache, files.Work, () => false, _ => { });
        await Should.ThrowAsync<IOException>(() => download.PrepareCacheAsync(TestContext.Current.CancellationToken));
        File.ReadAllBytes(files.Cache).ShouldBe(other); Directory.GetFiles(files.Work).ShouldBeEmpty();
    }

    [Fact]
    public async Task CancellationBeforePayloadTransportDoesNotPublish()
    {
        using var files = new OwnedFiles(); using var cancellation = new CancellationTokenSource(); int calls = 0;
        using var http = new HttpClient(new Handler((_, token) =>
        {
            if (++calls == 1) return Task.FromResult(Ok(Metadata()));
            cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(Ok(s_installer));
        }));
        var download = new DesktopRuntimeDownload(http, files.Cache, files.Work, () => false, _ => throw new InvalidOperationException("No signer call expected"));
        await Should.ThrowAsync<OperationCanceledException>(() => download.PrepareCacheAsync(cancellation.Token));
        File.Exists(files.Cache).ShouldBeFalse(); if (Directory.Exists(files.Work)) Directory.GetFiles(files.Work).ShouldBeEmpty();
    }

    [Fact]
    public async Task TransientMetadataFailureRetriesWithinTheSameRequestBudget()
    {
        using var files = new OwnedFiles(); int calls = 0;
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(++calls switch
        {
            1 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new ByteArrayContent([]) },
            2 => Ok(Metadata()),
            _ => Ok(s_installer)
        })));
        await new DesktopRuntimeDownload(http, files.Cache, files.Work, () => false, _ => { }).PrepareCacheAsync(TestContext.Current.CancellationToken);
        calls.ShouldBe(3); File.ReadAllBytes(files.Cache).ShouldBe(s_installer);
    }

    [Fact]
    public async Task RangeDownloadsRetryInterruptedBlockWithoutDuplicatingEarlierBytes()
    {
        using var files = new OwnedFiles();
        byte[] payload = new byte[9 * 1024 * 1024 + 7];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);
        int requests = 0; var starts = new List<long>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (++requests == 1) return Task.FromResult(Ok(Metadata(installer: payload)));
            var range = request.Headers.Range!.Ranges.Single();
            long start = range.From!.Value, end = Math.Min(range.To!.Value, payload.Length - 1);
            starts.Add(start);
            HttpContent content = requests == 3 ? new StreamContent(new InterruptedReadStream(payload.AsSpan((int)start, 128 * 1024).ToArray()))
                : new ByteArrayContent(payload.AsSpan((int)start, (int)(end - start + 1)).ToArray());
            content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, payload.Length);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content });
        }));
        await new DesktopRuntimeDownload(http, files.Cache, files.Work, () => false, _ => { }).PrepareCacheAsync(TestContext.Current.CancellationToken);
        File.ReadAllBytes(files.Cache).ShouldBe(payload); starts.ShouldBe([0L, 8L * 1024 * 1024, 8L * 1024 * 1024]);
        Directory.GetFiles(files.Work).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("changed-total")]
    [InlineData("ignored-range")]
    public async Task InvalidContinuationIsRejectedWithoutPublishingEarlierBytes(string scenario)
    {
        using var files = new OwnedFiles(); int requests = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (++requests == 1) return Task.FromResult(Ok(Metadata(installer: [1, 2])));
            var range = request.Headers.Range!.Ranges.Single();
            range.From.ShouldBe(requests == 2 ? 0L : 1L);
            if (requests == 3 && scenario == "ignored-range") return Task.FromResult(Ok([2]));
            var content = new ByteArrayContent(requests == 2 ? [1] : [2]);
            content.Headers.ContentRange = requests == 2 ? new ContentRangeHeaderValue(0, 0, 2) : new ContentRangeHeaderValue(1, 1, 3);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content });
        }));
        await Should.ThrowAsync<InvalidDataException>(() => new DesktopRuntimeDownload(http, files.Cache, files.Work, () => false,
            _ => throw new InvalidOperationException("No signer call expected"))
            .PrepareCacheAsync(TestContext.Current.CancellationToken));
        File.Exists(files.Cache).ShouldBeFalse(); requests.ShouldBe(3); Directory.GetFiles(files.Work).ShouldBeEmpty();
    }

    [Fact]
    public async Task UnexpectedRangeOffsetIsRejectedBeforePublishing()
    {
        using var files = new OwnedFiles(); int requests = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            if (++requests == 1) return Task.FromResult(Ok(Metadata()));
            var content = new ByteArrayContent(s_installer);
            content.Headers.ContentRange = new ContentRangeHeaderValue(1, s_installer.Length, s_installer.Length + 1);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content });
        }));
        await Should.ThrowAsync<InvalidDataException>(() => new DesktopRuntimeDownload(http, files.Cache, files.Work, () => false, _ => { })
            .PrepareCacheAsync(TestContext.Current.CancellationToken));
        File.Exists(files.Cache).ShouldBeFalse(); requests.ShouldBe(2); Directory.GetFiles(files.Work).ShouldBeEmpty();
    }

    private static byte[] Metadata(string? url = null, byte[]? installer = null) => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
    {
        ["latest-runtime"] = "10.0.13",
        ["releases"] = new[] { new { windowsdesktop = new { version = "10.0.13", files = new[] { new
        {
            rid = "win-x64", name = "windowsdesktop-runtime-win-x64.exe", hash = Convert.ToHexString(SHA512.HashData(installer ?? s_installer)),
            url = url ?? "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.13/windowsdesktop-runtime-10.0.13-win-x64.exe"
        } } } } }
    });
    private static HttpResponseMessage Ok(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request, cancellationToken);
    }
    private sealed class InterruptedReadStream(byte[] prefix) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position == prefix.Length) throw new IOException("synthetic interrupted block");
            int copied = Math.Min(count, prefix.Length - _position);
            prefix.AsSpan(_position, copied).CopyTo(buffer.AsSpan(offset, copied));
            _position += copied;
            return copied;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_position == prefix.Length) return ValueTask.FromException<int>(new IOException("synthetic interrupted block"));
            int copied = Math.Min(buffer.Length, prefix.Length - _position);
            prefix.AsMemory(_position, copied).CopyTo(buffer);
            _position += copied;
            return ValueTask.FromResult(copied);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class OwnedFiles : IDisposable
    {
        private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "iris-runtime-test-" + Guid.NewGuid().ToString("N"))).FullName;
        internal string Cache => Path.Combine(_root, "downloads", "runtime.exe");
        internal string Work => Path.Combine(_root, "owned-work");
        public void Dispose() => Directory.Delete(_root, true);
    }
}
