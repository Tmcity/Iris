using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Languages;
using Iris.Core.Translation;
using Iris.Providers.Http;
using Microsoft.Extensions.Logging;

namespace Iris.Providers.Translation;

internal static partial class ProviderCallGuard
{
    public static async IAsyncEnumerable<TranslationChunk> GuardStreamAsync(
        Func<CancellationToken, Action, IAsyncEnumerable<TranslationChunk>> core,
        TimeSpan timeout,
        TimeProvider timeProvider,
        string providerId,
        Func<Exception, IrisException?>? mapSpecific,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        using var timeoutCts = new CancellationTokenSource(timeout, timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        Action resetTimeout = () =>
        {
            try
            {
                timeoutCts.CancelAfter(timeout);
            }
            catch (ObjectDisposedException)
            {
            }
        };

        IAsyncEnumerator<TranslationChunk>? enumerator = null;
        try
        {
            enumerator = core(linkedCts.Token, resetTimeout).GetAsyncEnumerator(linkedCts.Token);
        }
        catch (Exception ex)
        {
            throw ClassifyException(ex, timeoutCts, timeout, providerId, mapSpecific, cancellationToken);
        }

        await using (enumerator.ConfigureAwait(false))
        {
            while (true)
            {
                TranslationChunk current;
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    if (hasNext)
                    {
                        current = enumerator.Current;
                    }
                    else
                    {
                        break;
                    }
                }
                catch (Exception ex)
                {
                    throw ClassifyException(ex, timeoutCts, timeout, providerId, mapSpecific, cancellationToken);
                }

                yield return current;
            }
        }
    }

    public static async Task<T> GuardAsync<T>(
        Func<CancellationToken, Task<T>> call,
        TimeSpan timeout,
        TimeProvider timeProvider,
        string providerId,
        Func<Exception, IrisException?>? mapSpecific,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        using var timeoutCts = new CancellationTokenSource(timeout, timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            return await call(linkedCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw ClassifyException(ex, timeoutCts, timeout, providerId, mapSpecific, cancellationToken);
        }
    }

    public static async Task<ProviderTestResult> RunTestAsync(
        ITranslationProvider provider,
        string targetLanguage,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        cancellationToken.ThrowIfCancellationRequested();

        var request = new ProviderTranslateRequest("Hello, world.", LanguageCodes.English, targetLanguage);
        var startTimestamp = timeProvider.GetTimestamp();
        var sb = new StringBuilder();

        try
        {
            await foreach (var chunk in provider.TranslateStreamAsync(request, cancellationToken).ConfigureAwait(false))
            {
                if (!string.IsNullOrEmpty(chunk.Delta))
                {
                    sb.Append(chunk.Delta);
                }
            }

            var elapsed = timeProvider.GetElapsedTime(startTimestamp);
            return new ProviderTestResult(true, elapsed, Sample: sb.ToString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IrisException ex)
        {
            var elapsed = timeProvider.GetElapsedTime(startTimestamp);
            return new ProviderTestResult(false, elapsed, Error: ex.ToErrorInfo());
        }
        catch (Exception ex)
        {
            var elapsed = timeProvider.GetElapsedTime(startTimestamp);
            var exType = ex.GetType().Name;
            var providerId = provider.Descriptor.Id;
            LogTestFailed(logger, providerId, exType);
            var errorInfo = new ErrorInfo(
                ErrorCode.Unknown,
                ErrorMessages.GetDefault(ErrorCode.Unknown),
                Detail: exType,
                ProviderId: providerId);
            return new ProviderTestResult(false, elapsed, Error: errorInfo);
        }
    }

    private static Exception ClassifyException(
        Exception ex,
        CancellationTokenSource timeoutCts,
        TimeSpan timeout,
        string providerId,
        Func<Exception, IrisException?>? mapSpecific,
        CancellationToken userToken)
    {
        if (userToken.IsCancellationRequested)
        {
            if (ex is OperationCanceledException oce && oce.CancellationToken == userToken)
            {
                return oce;
            }
            return new OperationCanceledException(userToken);
        }

        if (timeoutCts.IsCancellationRequested)
        {
            return HttpErrorMapper.Timeout(providerId, timeout);
        }

        if (ex is IrisException irisEx)
        {
            return irisEx;
        }

        if (mapSpecific != null)
        {
            var specific = mapSpecific(ex);
            if (specific != null)
            {
                return specific;
            }
        }

        var mapped = HttpErrorMapper.TryMapException(ex, providerId);
        if (mapped != null)
        {
            return mapped;
        }

        return ex;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "测试翻译异常: ProviderId={ProviderId}, Exception={ExceptionType}")]
    private static partial void LogTestFailed(ILogger logger, string providerId, string exceptionType);
}
