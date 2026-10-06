using System.Net.Http.Headers;
using Iris.Core.Errors;
using Iris.Providers.Http;

namespace Iris.Providers.Ocr.Baidu;

// An exchange remains reachable after any failed release. Content is detached from its message
// so retry is not defeated by HttpResponseMessage's already-disposed flag.
internal sealed class BaiduHttpExchange : IAsyncDisposable
{
    private HttpClient? _client;
    private HttpRequestMessage? _request;
    private HttpContent? _requestContent, _responseContent;
    private HttpResponseMessage? _response;
    private Stream? _body;
    private bool _bodyNeedsRelease;
    internal async Task<T> ExecuteAsync<T>(IHttpClientFactory clients, Uri uri, byte[]? form, int maxBytes, bool token,
        BaiduCall call, string id, Func<byte[], int, T> parse)
    {
        call.Check();
        _request = new(HttpMethod.Post, uri);
        if (form != null)
        {
            _requestContent = new ByteArrayContent(form);
            _requestContent.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
            _request.Content = _requestContent;
        }
        call.Check(); _client = clients.CreateClient(HttpClientNames.Ocr);
        call.Check();
        _response = await _client.SendAsync(_request, HttpCompletionOption.ResponseHeadersRead, call.Token).ConfigureAwait(false);
        _responseContent = _response.Content; _response.Content = null;
        int status = (int)_response.StatusCode;
        if (_responseContent.Headers.ContentLength > maxBytes) throw new BaiduException(ErrorCode.InvalidResponse, "BODY_LIMIT", id, status);
        if (status is < 200 or > 299 && (!token || call.Token.IsCancellationRequested)) throw BaiduErrors.Http(status, id, token);
        call.Check();
        _body = await _responseContent.ReadAsStreamAsync(call.Token).ConfigureAwait(false);
        call.Check();
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            call.Check();
            int read = await _body.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, maxBytes + 1 - (int)buffer.Length)), call.Token).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maxBytes) throw new BaiduException(ErrorCode.InvalidResponse, "BODY_LIMIT", id, status);
            buffer.Write(chunk, 0, read);
        }
        call.Check(); return parse(buffer.ToArray(), status);
    }
    internal async Task<List<string>> CleanupAsync()
    {
        var errors = new List<string>();
        if (_request != null) _request.Content = null;
        if (_responseContent != null)
        {
            try { _responseContent.Dispose(); _responseContent = null; if (!_bodyNeedsRelease) _body = null; }
            catch (Exception ex)
            {
                errors.Add(BaiduErrors.Describe("RESPONSE_CONTENT", ex));
                // HttpContent may mark itself disposed before its stream throws. Keep and retry
                // the original stream directly; a second content.Dispose alone is insufficient.
                _bodyNeedsRelease = true;
            }
        }
        if (_bodyNeedsRelease && _body != null)
            try { await _body.DisposeAsync().ConfigureAwait(false); _body = null; _bodyNeedsRelease = false; }
            catch (Exception ex) { errors.Add(BaiduErrors.Describe("BODY_STREAM", ex)); }
        if (_requestContent != null)
            try { _requestContent.Dispose(); _requestContent = null; } catch (Exception ex) { errors.Add(BaiduErrors.Describe("REQUEST_CONTENT", ex)); }
        if (_response != null)
            try { _response.Dispose(); _response = null; } catch (Exception ex) { errors.Add(BaiduErrors.Describe("RESPONSE", ex)); }
        if (_request != null)
            try { _request.Dispose(); _request = null; } catch (Exception ex) { errors.Add(BaiduErrors.Describe("REQUEST", ex)); }
        if (_client != null)
            try { _client.Dispose(); _client = null; } catch (Exception ex) { errors.Add(BaiduErrors.Describe("CLIENT", ex)); }
        return errors;
    }
    public async ValueTask DisposeAsync()
    {
        var errors = await CleanupAsync().ConfigureAwait(false);
        if (errors.Count != 0) throw new BaiduException(ErrorCode.Unknown, "HTTP_CLEANUP", null, diagnostics: string.Join(';', errors));
    }

}
