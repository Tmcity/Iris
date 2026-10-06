using Iris.Core.Ocr;
using Iris.Core.Settings;

namespace Iris.Core.DependencyInjection;

internal sealed class OcrProviderConfigurationObserver(IOcrProviderRegistry registry) : IProviderConfigurationObserver
{
    public Task RefreshAsync(CancellationToken cancellationToken = default) => registry.RefreshAsync(cancellationToken);
}
