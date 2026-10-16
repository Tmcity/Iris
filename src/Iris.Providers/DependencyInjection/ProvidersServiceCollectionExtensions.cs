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
        services.TryAddSingleton<NetworkTransportFactory>();
        services.TryAddSingleton<NetworkRouteResolver>();
        services.TryAddSingleton<NetworkTransportManager>();
        services.TryAddSingleton<ProxyConnectivityTransportFactory>();
        services.TryAddSingleton<ProxyConnectivityTester>();
        services.TryAddSingleton<Iris.Core.Network.IProxyConnectivityTester>(sp => sp.GetRequiredService<ProxyConnectivityTester>());
        services.TryAddSingleton<Iris.Core.Network.INetworkTransportLifetime, NetworkTransportLifetime>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Iris.Core.Network.INetworkConfigurationObserver, NetworkConfigurationObserver>());
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
            .ConfigurePrimaryHttpMessageHandler(sp => new NetworkRoutingHandler(sp.GetRequiredService<NetworkTransportManager>()))
            .ConfigureHttpClient(client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            })
            .RemoveAllLoggers();

        llm.AddHttpMessageHandler(sp => new NetworkSnapshotHandler(HttpClientNames.Llm,
            sp.GetRequiredService<Iris.Core.Network.INetworkProxySnapshotSource>(), sp.GetRequiredService<NetworkTransportManager>()));
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
            .ConfigurePrimaryHttpMessageHandler(sp => new NetworkRoutingHandler(sp.GetRequiredService<NetworkTransportManager>()))
            .ConfigureHttpClient(client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            })
            .RemoveAllLoggers();

        ocr.AddHttpMessageHandler(sp => new NetworkSnapshotHandler(HttpClientNames.Ocr,
            sp.GetRequiredService<Iris.Core.Network.INetworkProxySnapshotSource>(), sp.GetRequiredService<NetworkTransportManager>()));
        ocr.AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 2;
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
        });
        ocr.AddHttpMessageHandler<HttpLoggingHandler>();
        ocr.AddHttpMessageHandler<Iris.Providers.Ocr.Baidu.BaiduOcrTransportSafetyHandler>();

        services.AddHttpClient(HttpClientNames.Updates)
            .ConfigurePrimaryHttpMessageHandler(sp => new NetworkRoutingHandler(sp.GetRequiredService<NetworkTransportManager>()))
            .ConfigureHttpClient(client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            })
            .RemoveAllLoggers()
            .AddHttpMessageHandler(sp => new NetworkSnapshotHandler(HttpClientNames.Updates,
                sp.GetRequiredService<Iris.Core.Network.INetworkProxySnapshotSource>(), sp.GetRequiredService<NetworkTransportManager>()));

        return services;
    }
}
