using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Settings;

namespace Iris.Core.Translation;

/// <summary>
/// 显式坏引用或不可用引用的未配置占位 Provider（specs/PROVIDER_REGISTRY.md §2.1）。
/// 不联网；Descriptor 保留 Profile 信息且 IsConfigured=false；
/// 首次枚举抛 NotConfigured，TestAsync 返回失败，取消按原规则抛出 OperationCanceledException。
/// </summary>
internal sealed class UnavailableTranslationProvider : ITranslationProvider
{
    private readonly TranslationProviderProfile _profile;

    public UnavailableTranslationProvider(TranslationProviderProfile profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        Descriptor = new ProviderDescriptor(
            profile.Id,
            profile.DisplayName,
            profile.Kind.ToString(),
            profile.Model,
            IsConfigured: false);
    }

    public ProviderDescriptor Descriptor { get; }

    public async IAsyncEnumerable<TranslationChunk> TranslateStreamAsync(
        ProviderTranslateRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();

        if (cancellationToken.IsCancellationRequested)
        {
            yield break;
        }

        throw new IrisException(
            ErrorCode.NotConfigured,
            $"Provider '{_profile.DisplayName}' is not configured.",
            providerId: _profile.Id);
    }

    public Task<ProviderTestResult> TestAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var error = new ErrorInfo(
            ErrorCode.NotConfigured,
            $"Provider '{_profile.DisplayName}' is not configured.",
            ProviderId: _profile.Id);

        return Task.FromResult(new ProviderTestResult(
            Success: false,
            Latency: TimeSpan.Zero,
            Error: error));
    }
}
