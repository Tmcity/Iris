using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Iris.Providers.Translation.Anthropic;

/// <summary>
/// 挂载到 AnthropicClient.Handlers 的 DelegatingHandler。
/// 负责透明插入 SseEventBoundaryStream 进行流式心跳续期，并在取流或包装失败时妥善释放 HttpResponseMessage。
/// </summary>
internal sealed class AnthropicProgressTrackingHandler : DelegatingHandler
{
    private readonly Func<Action?> _getResetTimeout;

    public AnthropicProgressTrackingHandler(Func<Action?> getResetTimeout)
    {
        _getResetTimeout = getResetTimeout ?? throw new ArgumentNullException(nameof(getResetTimeout));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var resetTimeout = _getResetTimeout();
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (resetTimeout == null || response.Content == null)
        {
            return response;
        }

        HttpResponseMessage? responseToDispose = response;
        try
        {
            var originalContent = response.Content;
            var originalStream = await originalContent.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var trackingStream = new SseEventBoundaryStream(originalStream, originalContent, resetTimeout);
            var newContent = new StreamContent(trackingStream);
            foreach (var header in originalContent.Headers)
            {
                newContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            response.Content = newContent;

            // 成功包装并转移所有权给调用方 / SDK
            responseToDispose = null;
            return response;
        }
        finally
        {
            // 若在取流、创建包装或复制响应头时抛出异常或被取消，释放已取得的响应
            responseToDispose?.Dispose();
        }
    }
}
