using System.Runtime.ExceptionServices;
using Iris.Core.DependencyInjection;
using Iris.Core.Errors;
using Iris.Core.Ocr;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Platform.Windows.Ocr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Iris.Platform.Windows.Tests.Ocr;

public sealed class WindowsOcrRegistryTests
{
    [Fact]
    public Task RetiredLease_RetriesOriginalActualBitmap_AndWaitsAsyncDisposal() => WindowsOcrTestScope.Run(async f =>
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings { Ocr = new() { Providers = [f.Profile] } });
        var services = new ServiceCollection(); services.AddIrisCore();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(settings); services.AddSingleton(Substitute.For<ISecretStore>());
        services.AddSingleton<IOcrProviderFactory>(new WindowsOcrProviderFactory(f.Native, f.Images, f.Time));
        await using var container = services.BuildServiceProvider();
        var registry = container.GetRequiredService<IOcrProviderRegistry>();
        IOcrProviderSnapshot? lease = null; Task? release = null; Action? open = null;
        Exception? primary = null; var errors = new List<Exception>();
        try
        {
            lease = await registry.AcquireSnapshotAsync();
            var provider = lease.Providers.Single().Provider;
            f.Failures["BITMAP_DISPOSE"] = new IOException("private bitmap cleanup");
            var image = await WindowsOcrTestScope.Image();
            await Should.ThrowAsync<IrisException>(() => provider.RecognizeAsync(new(image, "en"), default));
            var original = f.Images.Items.Single(); original.BitmapDisposed.ShouldBeFalse();
            settings.Current.Returns(new AppSettings { Ocr = new() { Providers = [] } });
            settings.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(settings, new SettingsChangedEventArgs(lease.Settings, settings.Current));
            await registry.RefreshAsync();
            await Should.ThrowAsync<IrisException>(() => lease.DisposeAsync().AsTask());
            Should.Throw<ObjectDisposedException>(() => lease.Providers);
            original.BitmapDisposed.ShouldBeFalse();
            f.Failures.Clear(); var gate = f.Gate("BITMAP_DISPOSE"); open = gate.Release;
            release = lease.DisposeAsync().AsTask();
            await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            release.IsCompleted.ShouldBeFalse();
            var retry = registry.RetryCleanupAsync(); retry.IsCompleted.ShouldBeFalse();
            open(); await release; await retry;
            original.BitmapDisposed.ShouldBeTrue(); original.StreamDisposed.ShouldBeTrue(); original.WriterDisposed.ShouldBeTrue();
            f.Images.Items.Count.ShouldBe(1); f.Native.RecognitionCalls.ShouldBe(1);
            await registry.StopAsync();
        }
        catch (Exception ex) { primary = ex; }
        finally
        {
            open?.Invoke(); f.Before = null; f.Failures.Clear();
            if (release != null) try { await release; } catch (Exception ex) { errors.Add(ex); }
            if (lease != null) try { await lease.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { await registry.StopAsync(); } catch (Exception ex) { errors.Add(ex); }
        }
        if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) errors.Insert(0, primary);
        if (errors.Count != 0) throw new AggregateException(errors);
    });
}
