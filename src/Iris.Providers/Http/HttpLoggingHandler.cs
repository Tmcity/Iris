using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Iris.Providers.Http;

/// <summary>
/// 仅记录安全 URL（不含查询参数）、方法、状态码与耗时的日志处理器。
/// 严禁记录请求头与请求体。
/// </summary>
internal sealed partial class HttpLoggingHandler : DelegatingHandler
{
    private readonly ILogger<HttpLoggingHandler> _logger;

    public HttpLoggingHandler(ILogger<HttpLoggingHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var safeUrl = SafeHttpDiagnostics.ToSafeUrl(request.RequestUri);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            LogRequestSuccess(_logger, request.Method.Method, safeUrl, (int)response.StatusCode, stopwatch.ElapsedMilliseconds);
            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var exceptionType = ex.GetType().Name;
            LogRequestFailed(_logger, request.Method.Method, safeUrl, exceptionType);
            throw;
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "{Method} {SafeUrl} → {StatusCode}（{ElapsedMs} ms）")]
    private static partial void LogRequestSuccess(ILogger logger, string method, string safeUrl, int statusCode, long elapsedMs);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "{Method} {SafeUrl} 失败：{ExceptionType}")]
    private static partial void LogRequestFailed(ILogger logger, string method, string safeUrl, string exceptionType);
}
