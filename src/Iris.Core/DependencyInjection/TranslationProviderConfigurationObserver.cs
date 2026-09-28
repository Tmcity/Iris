using System;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Settings;
using Iris.Core.Translation;

namespace Iris.Core.DependencyInjection;

/// <summary>
/// 将 IProviderConfigurationObserver 转发到容器中唯一的 ITranslationProviderRegistry 实例。
/// 不拥有也不单独释放 Registry（specs/PROVIDER_REGISTRY.md §4）。
/// </summary>
internal sealed class TranslationProviderConfigurationObserver(ITranslationProviderRegistry registry) : IProviderConfigurationObserver
{
    private readonly ITranslationProviderRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        _registry.RefreshAsync(cancellationToken);
}
