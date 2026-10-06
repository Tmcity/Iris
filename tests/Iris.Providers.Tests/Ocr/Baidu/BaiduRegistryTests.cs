using System.Runtime.ExceptionServices;
using Iris.Core.DependencyInjection;
using Iris.Core.Errors;
using Iris.Core.Ocr;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Providers.Ocr.Baidu;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Iris.Providers.Tests.Ocr.Baidu;

public sealed class BaiduRegistryTests
{
    [Fact]
    public Task RegistryRelease_RetainsFailedHttpStream_AndJoinsOriginalCleanup() => BaiduTestScope.Run(async f =>
    {
        var settings = Substitute.For<ISettingsService>(); settings.Current.Returns(new AppSettings { Ocr = new() { Providers = [] } });
        var services = new ServiceCollection(); services.AddIrisCore();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(settings); services.AddSingleton(Substitute.For<ISecretStore>());
        services.AddSingleton<IOcrProviderFactory>(new BaiduOcrProviderFactory(f, f.Time));
        await using var container = services.BuildServiceProvider();
        var registry = container.GetRequiredService<IOcrProviderRegistry>();
        IOcrProviderSnapshot? lease = null; Task? release = null;
        using var open = new ManualResetEventSlim();
        Exception? primary = null; var errors = new List<Exception>();
        try
        {
            lease = await registry.CreateTransientAsync(BaiduTestScope.Profile() with { Enabled = false }, new(BaiduTestScope.ApiKey, BaiduTestScope.Secret));
            var provider = lease.Providers.Single().Provider;
            (await provider.TestAsync(default)).Success.ShouldBeTrue();
            f.FailStreamDispose = true;
            await Should.ThrowAsync<IrisException>(() => provider.RecognizeAsync(BaiduTestScope.Input(), default));
            var original = f.Streams.Last(); original.Released.ShouldBeFalse();
            await Should.ThrowAsync<IrisException>(() => lease.DisposeAsync().AsTask());
            original.Released.ShouldBeFalse();
            f.FailStreamDispose = false;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.BeforeStreamDispose = () => { entered.TrySetResult(); open.Wait(); };
            release = lease.DisposeAsync().AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); release.IsCompleted.ShouldBeFalse();
            var retry = registry.RetryCleanupAsync(); retry.IsCompleted.ShouldBeFalse();
            open.Set(); await release; await retry;
            original.Released.ShouldBeTrue(); f.Streams.ShouldAllBe(s => s.Released); f.Clients.ShouldAllBe(c => c.Released);
            f.Requests.Count.ShouldBe(2); // One token and one recognition, never replay either for cleanup.
            await registry.StopAsync();
        }
        catch (Exception ex) { primary = ex; }
        finally
        {
            open.Set(); f.BeforeStreamDispose = null; f.FailStreamDispose = false;
            if (release != null) try { await release; } catch (Exception ex) { errors.Add(ex); }
            if (lease != null) try { await lease.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { await registry.StopAsync(); } catch (Exception ex) { errors.Add(ex); }
        }
        if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) errors.Insert(0, primary);
        if (errors.Count != 0) throw new AggregateException(errors);
    });
}
