using System.Text.Json;
using Iris.Core.Common;
using Iris.Core.Imaging;
using Iris.Core.Ocr;
using Iris.Core.Settings;
using Iris.Platform.Windows.Ocr;
using Shouldly;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Iris.Platform.Windows.Tests.Ocr;

public sealed class WindowsOcrSdkSmokeTests
{
    [Fact]
    public async Task RealSdk_RecognizesOnlyOwnedSyntheticHelloPng()
    {
        var native = new ObservedNative(); WindowsOcrProvider? provider = null;
        string[] installed = []; string? text = null; Exception? failure = null;
        bool missing = false;
        try
        {
            provider = (WindowsOcrProvider)new WindowsOcrProviderFactory(native, new WindowsOcrImageFactory(), TimeProvider.System).Create(
                new() { Id = "sdk-smoke", DisplayName = "SDK smoke", Kind = OcrProviderKind.WindowsLocal, TimeoutSeconds = 30,
                    WindowsOptions = new() { LanguageTag = "en-US" } }, ProviderCredentials.None);
            installed = OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToArray();
            string? language = installed.FirstOrDefault(t => t.StartsWith("en", StringComparison.OrdinalIgnoreCase));
            if (language == null) missing = true;
            else
            {
                using var source = typeof(WindowsOcrSdkSmokeTests).Assembly.GetManifestResourceStream("Iris.WindowsOcr.SyntheticHello.png")!;
                using var bytes = new MemoryStream(); await source.CopyToAsync(bytes, TestContext.Current.CancellationToken);
                var result = await provider.RecognizeAsync(new(new(bytes.ToArray(), ImageFormat.Png, 640, 160), language), TestContext.Current.CancellationToken);
                text = string.Join('\n', result.Lines.Select(l => l.Text));
                text.ShouldContain("HELLO"); text.ShouldContain("123");
                result.DetectedLanguage.ShouldBeNull();
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            if (provider != null)
                try { await provider.DisposeAsync(); } catch (Exception cleanup) { failure = failure == null ? cleanup : new AggregateException(failure, cleanup); }
            string directory = Path.Combine(Path.GetTempPath(), "iris-t029-sdk-smoke"); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
            {
                status = failure != null ? "Failed" : missing ? "SkippedNoEnglishLanguage" : "Passed",
                native.Capability, native.SelectedLanguage, installed, native.RawSdkLines, processedSyntheticText = text,
                native.CapabilityFailureHResult, failureHResult = failure == null ? null : $"0x{failure.HResult:X8}",
                safeDetail = (failure as Iris.Core.Errors.IrisException)?.Detail,
                syntheticInput = "640x160 white Arial HELLO 123 PNG", realSdk = true, userImage = false
            }));
        }
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (missing) Assert.Skip("[ENVIRONMENT LIMITATION] Windows OCR synthetic HELLO smoke: no installed English OCR language; no language installation attempted.");
    }
    private sealed class ObservedNative : IWindowsOcrNative
    {
        private readonly WindowsOcrNative _actual = new();
        internal uint? Capability;
        internal string? SelectedLanguage, CapabilityFailureHResult;
        internal string?[]? RawSdkLines;
        public uint MaxImageDimension
        {
            get
            {
                try { uint value = _actual.MaxImageDimension; Capability = value; return value; }
                catch (Exception ex) { CapabilityFailureHResult = $"0x{ex.HResult:X8}"; throw; }
            }
        }
        public IWindowsOcrEngine? TryLanguage(string tag) => Observe(_actual.TryLanguage(tag));
        public IWindowsOcrEngine? TryUserLanguages() => Observe(_actual.TryUserLanguages());
        private ObservedEngine? Observe(IWindowsOcrEngine? engine)
        { if (engine == null) return null; SelectedLanguage = engine.LanguageTag; return new ObservedEngine(this, engine); }
        private sealed class ObservedEngine(ObservedNative owner, IWindowsOcrEngine actual) : IWindowsOcrEngine
        {
            public string LanguageTag => actual.LanguageTag;
            public async Task<IReadOnlyList<WindowsOcrNativeLine>> RecognizeAsync(SoftwareBitmap bitmap, Action check)
            { var lines = await actual.RecognizeAsync(bitmap, check); owner.RawSdkLines = lines.Select(l => l.Text).ToArray(); return lines; }
        }
    }
}
