using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Retry;
using Iris.Core.Translation;
using Iris.Providers.Http;
using Iris.Providers.Translation.Anthropic;
using Iris.Providers.Translation.OpenAICompatible;

namespace Iris.Providers.DependencyInjection;

/// <summary>
/// Providers 依赖注入扩展。
/// </summary>
public static class ProvidersServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Providers 层的 HTTP 基础设施。调用方须事先注册 ISettingsService 与日志。
    /// </summary>
    public static IServiceCollection AddIrisProviders(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<DynamicWebProxy>();
        services.AddTransient<HttpLoggingHandler>();
        services.AddTransient<Iris.Providers.Ocr.Baidu.BaiduOcrTransportSafetyHandler>();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ITranslationProviderFactory, OpenAICompatibleTranslationProviderFactory>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ITranslationProviderFactory, AnthropicTranslationProviderFactory>());

        services.TryAddEnumerable(ServiceDescriptor.Singleton<Iris.Core.Ocr.IOcrProviderFactory, Iris.Providers.Ocr.Baidu.BaiduOcrProviderFactory>());

        var informationalVersion = typeof(ProvidersServiceCollectionExtensions).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = informationalVersion?.Split('+')[0] ?? "1.0.0";
        var userAgent = $"Iris/{version}";

        // iris.llm: 大模型流式请求客户端
        var llm = services.AddHttpClient(HttpClientNames.Llm)
            .ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
            {
                Proxy = sp.GetRequiredService<DynamicWebProxy>(),
                UseProxy = true,
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectTimeout = TimeSpan.FromSeconds(15),
                AutomaticDecompression = DecompressionMethods.All,
            })
            .ConfigureHttpClient(client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            })
            .RemoveAllLoggers();

        llm.AddResilienceHandler("iris-llm", (builder, context) =>
        {
            var timeProvider = context.ServiceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
            builder.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 1,
                ShouldHandle = args =>
                {
                    if (args.Outcome.Exception is HttpRequestException)
                    {
                        return ValueTask.FromResult(true);
                    }

                    if (args.Outcome.Result != null)
                    {
                        var statusCode = (int)args.Outcome.Result.StatusCode;
                        if (statusCode is 408 or 429 || statusCode >= 500)
                        {
                            return ValueTask.FromResult(true);
                        }
                    }

                    return ValueTask.FromResult(false);
                },
                DelayGenerator = args =>
                {
                    var delay = LlmRetryDelay.Compute(args.Outcome.Result, timeProvider);
                    return ValueTask.FromResult<TimeSpan?>(delay);
                },
            });
        });
        llm.AddHttpMessageHandler<HttpLoggingHandler>();

        // iris.ocr: OCR 与 Token 请求客户端
        var ocr = services.AddHttpClient(HttpClientNames.Ocr)
            .ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
            {
                Proxy = sp.GetRequiredService<DynamicWebProxy>(),
                UseProxy = true,
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectTimeout = TimeSpan.FromSeconds(15),
                AutomaticDecompression = DecompressionMethods.All,
            })
            .ConfigureHttpClient(client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            })
            .RemoveAllLoggers();

        ocr.AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 2;
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
        });
        ocr.AddHttpMessageHandler<HttpLoggingHandler>();
        ocr.AddHttpMessageHandler<Iris.Providers.Ocr.Baidu.BaiduOcrTransportSafetyHandler>();

        return services;
    }
}
