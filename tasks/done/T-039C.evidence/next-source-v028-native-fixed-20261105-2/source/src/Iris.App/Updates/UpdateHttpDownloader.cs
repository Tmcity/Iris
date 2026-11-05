using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;
using Iris.Core.Errors;
using Iris.Platform.Windows.Maintenance;
using Velopack.Sources;

namespace Iris.App.Updates;

/// <summary>Per-operation transport: every request/response remains owned until its actual cleanup succeeds.</summary>
internal sealed class UpdateHttpDownloader(HttpClient client, CancellationToken operationToken) : IFileDownloader, IAsyncDisposable
{
    private readonly List<ResponseOwner> _responses = [];
    private readonly List<Stream> _files = [];
    private readonly HashSet<string> _failedFiles = new(StringComparer.OrdinalIgnoreCase);
    internal const int MetadataBytes = 2 * 1024 * 1024;
    internal static Uri ValidateUri(string value, bool redirect)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) throw ReleaseIdentity.Invalid();
        bool allowed = uri.Host switch
        {
            "api.github.com" => uri.AbsolutePath.Equals("/repos/Tmcity/Iris/releases", StringComparison.OrdinalIgnoreCase),
            "github.com" => uri.AbsolutePath.Equals("/Tmcity/Iris/releases/latest/download/releases.win.json", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.StartsWith("/Tmcity/Iris/releases/download/", StringComparison.OrdinalIgnoreCase),
            "release-assets.githubusercontent.com" or "objects.githubusercontent.com" => redirect,
            _ => false
        };
        if (!allowed) throw ReleaseIdentity.Invalid();
        return uri;
    }
    private async Task<ResponseOwner> OpenAsync(string url)
    {
        var uri = ValidateUri(url, false);
        for (int hop = 0; ; hop++)
        {
            operationToken.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd(uri.Host == "api.github.com" ? "application/vnd.github+json" : "application/octet-stream");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, operationToken).ConfigureAwait(false);
            var owner = new ResponseOwner(response); _responses.Add(owner);
            operationToken.ThrowIfCancellationRequested();
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (hop >= 5 || response.Headers.Location == null) throw ReleaseIdentity.Invalid();
                uri = ValidateUri(new Uri(uri, response.Headers.Location).AbsoluteUri, true);
                owner.Dispose(); _responses.Remove(owner); continue;
            }
            if (response.StatusCode == HttpStatusCode.NotFound) throw new IrisException(ErrorCode.EndpointNotFound, ErrorMessages.GetDefault(ErrorCode.EndpointNotFound));
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) throw new IrisException(ErrorCode.RateLimited, ErrorMessages.GetDefault(ErrorCode.RateLimited));
            if (!response.IsSuccessStatusCode) throw new IrisException(ErrorCode.NetworkUnavailable, ErrorMessages.GetDefault(ErrorCode.NetworkUnavailable));
            return owner;
        }
    }
    private async Task CopyAsync(string url, Stream output, long budget, Action<int>? progress)
    {
        var owner = await OpenAsync(url).ConfigureAwait(false);
        var response = owner.Response;
        if (response.Content.Headers.ContentLength > budget) throw ReleaseIdentity.Invalid();
        operationToken.ThrowIfCancellationRequested();
        var input = await response.Content.ReadAsStreamAsync(operationToken).ConfigureAwait(false);
        long total = 0; byte[] buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                operationToken.ThrowIfCancellationRequested();
                int count = await input.ReadAsync(buffer, operationToken).ConfigureAwait(false);
                operationToken.ThrowIfCancellationRequested();
                if (count == 0) break;
                total = checked(total + count); if (total > budget) throw ReleaseIdentity.Invalid();
                await output.WriteAsync(buffer.AsMemory(0, count), operationToken).ConfigureAwait(false);
                operationToken.ThrowIfCancellationRequested();
                if (response.Content.Headers.ContentLength is > 0 and var length) progress?.Invoke((int)Math.Min(99, total * 100 / length));
            }
            if (response.Content.Headers.ContentLength is long expected && expected != total) throw ReleaseIdentity.Invalid();
        }
        finally { Array.Clear(buffer); }
        owner.Dispose(); _responses.Remove(owner);
    }
    public async Task DownloadFile(string url, string targetFile, Action<int> progress, IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default)
    {
        cancelToken.ThrowIfCancellationRequested(); operationToken.ThrowIfCancellationRequested();
        MaintenancePaths.RequireNoLinks(targetFile);
        var output = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
        _files.Add(output);
        Exception? primary = null;
        try
        {
            await CopyAsync(url, output, ReleasePackage.MaxBytes, progress).ConfigureAwait(false);
            await output.FlushAsync(operationToken).ConfigureAwait(false);
        }
        catch (Exception ex) { primary = ex; _failedFiles.Add(targetFile); }
        try { await ReleaseResourcesAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            _failedFiles.Add(targetFile);
            if (primary != null) throw new AggregateException(primary, ex);
            throw;
        }
        if (primary != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
    }
    public async Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
    {
        using var output = new MemoryStream();
        await CopyAsync(url, output, MetadataBytes, null).ConfigureAwait(false);
        var bytes = output.ToArray();
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        ValidateJson(document.RootElement); return bytes;
    }
    public async Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        => Encoding.UTF8.GetString(await DownloadBytes(url, headers, timeout).ConfigureAwait(false));
    private static void ValidateJson(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject()) { if (!names.Add(property.Name)) throw ReleaseIdentity.Invalid(); ValidateJson(property.Value); }
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            if (element.GetArrayLength() > 256) throw ReleaseIdentity.Invalid();
            foreach (var child in element.EnumerateArray()) ValidateJson(child);
        }
    }
    private async Task ReleaseResourcesAsync()
    {
        var errors = new List<Exception>();
        foreach (var stream in _files.ToArray())
            try { await stream.DisposeAsync().ConfigureAwait(false); _files.Remove(stream); } catch (Exception ex) { errors.Add(ex); }
        foreach (var owner in _responses.ToArray())
            try { owner.Dispose(); _responses.Remove(owner); } catch (Exception ex) { errors.Add(ex); }
        if (_files.Count == 0)
            foreach (var file in _failedFiles.ToArray())
                try { MaintenancePaths.RequireNoLinks(file); File.Delete(file); _failedFiles.Remove(file); } catch (Exception ex) { errors.Add(ex); }
        if (errors.Count != 0) throw new AggregateException(errors);
    }
    public async ValueTask DisposeAsync()
    {
        await ReleaseResourcesAsync().ConfigureAwait(false);
        client.Dispose();
    }
    private sealed class ResponseOwner(HttpResponseMessage response) : IDisposable
    {
        internal HttpResponseMessage Response => response;
        public void Dispose()
        {
            // Retry the actual content, not HttpResponseMessage's one-way disposed flag.
            response.Content.Dispose(); response.Dispose();
        }
    }
}

internal sealed class NoCompatibleReleaseException : Exception;
