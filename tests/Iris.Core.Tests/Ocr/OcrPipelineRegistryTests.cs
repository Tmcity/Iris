using Iris.Core.DependencyInjection;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Ocr;
using Iris.Core.Security;
using Iris.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using static Iris.Core.Tests.Ocr.OcrRegistryTestScope;

namespace Iris.Core.Tests.Ocr;

public sealed class OcrPipelineRegistryTests
{
    [Fact]
    public Task ActualRegistryServiceNormalizer_UsesOldSnapshotAcrossReconfiguration() => Run(async f =>
    {
        f.Settings.Value = Config(Profile("main"), Profile("backup")) with { Ocr = new() { Providers = [Profile("main"), Profile("backup")], ActiveProviderId = "main", FallbackProviderId = "backup", LanguageHint = "ja" } };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); f.Releases.Add(() => release.TrySetResult());
        var hints = new List<string>();
        f.Providers.Configure = p => p.Recognition = async (request, _) =>
        {
            hints.Add(request.LanguageHint);
            if (p.Profile.Id == "main") { entered.TrySetResult(); await release.Task; throw new IrisException(ErrorCode.Timeout, "safe"); }
            return new(p.Profile.Id, [new("你"), new("好")], null, TimeSpan.FromSeconds(1));
        };
        var images = Substitute.For<IImagePreprocessor>();
        images.PrepareAsync(Arg.Any<ImageData>(), Arg.Any<ImageConstraints>(), Arg.Any<CancellationToken>()).Returns(c => Task.FromResult(c.Arg<ImageData>()));
        await using var service = new OcrService(f.Registry, images, new OcrTextNormalizer());
        var work = f.Track(service.RecognizeAsync(new([1], ImageFormat.Png, 40, 40)));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await f.Settings.UpdateAsync(_ => Config(Profile("new"))); await f.Registry.RefreshAsync();
            f.Providers.Created.Where(p => p.Profile.Id != "new").ShouldAllBe(p => !p.Disposed);
            release.TrySetResult(); var result = await work;
            result.Raw.ProviderId.ShouldBe("backup"); result.Text.ShouldBe("你好"); result.UsedFallback.ShouldBeTrue();
            hints.ShouldBe(new[] { "ja", "ja" });
            f.Providers.Created.Where(p => p.Profile.Id != "new").ShouldAllBe(p => p.Disposed);
        }
        finally { release.TrySetResult(); await work; }
    });
    [Fact]
    public async Task Di_IsUniqueLazyAndAsyncDisposable()
    {
        var settings = new Store(); var secrets = new Secrets(); var factories = new Factory();
        var services = new ServiceCollection(); services.AddIrisCore(); services.AddIrisCore();
        services.AddSingleton<ISettingsService>(settings); services.AddSingleton<ISecretStore>(secrets); services.AddSingleton<IOcrProviderFactory>(factories);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>)); services.AddSingleton(Substitute.For<IImagePreprocessor>());
        await using var container = services.BuildServiceProvider();
        var service = container.GetRequiredService<IOcrService>(); container.GetServices<IOcrService>().Single().ShouldBeSameAs(service);
        container.GetRequiredService<OcrService>().ShouldBeSameAs(service);
        container.GetServices<IOcrTextNormalizer>().Count().ShouldBe(1);
        settings.Reads.ShouldBe(0); secrets.Reads.ShouldBeEmpty(); factories.Created.ShouldBeEmpty(); await service.StopAsync(TestContext.Current.CancellationToken);
    }
}
