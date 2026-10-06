using System.Runtime.CompilerServices;
using Iris.App.Hosting;
using Iris.Core.Imaging;
using Iris.Core.Storage;
using Iris.Platform.Windows.Imaging;
using Microsoft.Extensions.DependencyInjection;

internal static class OwnershipChecks
{
    internal static async Task Run(IImagePreprocessor processor, string directory, Func<string, Func<Task>, Task> check)
    {
        await check("ACTUAL_APP_HOST_REGISTERS_SAME_LAZY_PREPROCESSOR", async () =>
        {
            var boundary = new PreparationBoundary();
            using var host = AppHostBuilder.Build([], new ReviewPaths(directory), new Serilog.Core.LoggingLevelSwitch(), 0,
                services => services.AddSingleton<IImagePreparationFactory>(boundary));
            var concrete = host.Services.GetRequiredService<WinRtImagePreprocessor>();
            var port = host.Services.GetRequiredService<IImagePreprocessor>();
            GeometryChecks.Require(ReferenceEquals(concrete, port), "App resolves different concrete and interface instances");
            await concrete.StopAsync();
            GeometryChecks.Require(boundary.Decoders.IsEmpty && boundary.Encoders.IsEmpty, "unused construction/stop allocated a codec");
        });
        await check("LIVE_SERVICE_DOES_NOT_RETAIN_COMPLETED_IMAGES", async () =>
        {
            var weak = await PrepareAndRelease(processor, directory);
            var control = Control();
            // Let result continuations and worker stacks unwind. No Stop, retry or new
            // preparation is invoked on this still-live instance before collection.
            for (int i = 0; i < 12 && (weak.Any(w => w.IsAlive) || control.IsAlive); i++)
            {
                await Task.Yield();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            }
            Console.WriteLine($"PREPROCESSOR_GC input={weak[0].IsAlive} inputBytes={weak[1].IsAlive} output={weak[2].IsAlive} outputBytes={weak[3].IsAlive} control={control.IsAlive}");
            GeometryChecks.Require(!control.IsAlive, "GC positive control did not collect; no product conclusion is valid");
            GeometryChecks.Require(weak.All(w => !w.IsAlive), "live service retained a completed input or output");
            GC.KeepAlive(processor);
        });
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Control() => new(new byte[1024]);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> PrepareAndRelease(IImagePreprocessor processor, string directory)
    {
        ImageData? source = new(await File.ReadAllBytesAsync(Path.Combine(directory, "fixtures", "format.png")), ImageFormat.Png, 17, 9);
        ImageData? output = await processor.PrepareAsync(source, new(1_000_000, false, 100, 0, [ImageFormat.Bmp]), default);
        WeakReference[] weak = [new(source), new(source.Bytes), new(output), new(output.Bytes)];
        source = null; output = null;
        return weak;
    }
    private sealed class ReviewPaths(string directory) : IAppPaths
    {
        public bool IsPortable => true;
        public string DataDirectory => directory;
        public string SettingsFile => Path.Combine(directory, "unused-settings.json");
        public string SecretsFile => Path.Combine(directory, "unused-secrets.bin");
        public string DatabaseFile => Path.Combine(directory, "unused-history.db");
        public string LogDirectory => Path.Combine(directory, "unused-logs");
    }
}
