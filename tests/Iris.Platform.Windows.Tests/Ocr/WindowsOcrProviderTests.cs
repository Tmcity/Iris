using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Ocr;
using Iris.Core.Settings;
using Iris.Platform.Windows.DependencyInjection;
using Iris.Platform.Windows.Ocr;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Windows.Graphics.Imaging;
using static Iris.Platform.Windows.Tests.Ocr.WindowsOcrTestScope;

namespace Iris.Platform.Windows.Tests.Ocr;

public sealed class WindowsOcrProviderTests
{
    [Theory]
    [InlineData(40U, 40)][InlineData(10000U, 10000)][InlineData(uint.MaxValue, 32768)]
    public Task Factory_QueriesCapabilityOnceAndNeedsNoCredentials(uint capability, int expected) => Run(async f =>
    {
        f.Native.Capability = capability; f.Profile = f.Profile with { Enabled = false, DisplayName = " " };
        var p = f.Provider; p.Descriptor.IsConfigured.ShouldBeTrue(); p.Descriptor.DisplayName.ShouldBe(f.Profile.Id);
        p.Constraints.MaxLongSide.ShouldBe(expected); p.Constraints.MinShortSide.ShouldBe(40);
        p.Constraints.MaxEncodedBytes.ShouldBe(285212672); p.Constraints.MaxBytesAppliesToBase64.ShouldBeFalse(); p.Constraints.MaxFormUrlEncodedBytes.ShouldBeNull();
        p.Constraints.AcceptedFormats.ShouldBe(new[] { ImageFormat.Png, ImageFormat.Bmp });
        f.Native.CapabilityCalls.ShouldBe(1); f.Native.Engines.ShouldBeEmpty(); f.Images.Items.ShouldBeEmpty();
        (await p.TestAsync(default)).Success.ShouldBeTrue(); f.Native.CapabilityCalls.ShouldBe(1); f.Native.RecognitionCalls.ShouldBe(0); f.Images.Items.ShouldBeEmpty();
    });

    [Theory]
    [InlineData(0U)][InlineData(39U)]
    public Task InvalidCapability_IsUnavailableWithoutInventedConstraint(uint maximum) => Run(async f =>
    {
        f.Native.Capability = maximum;
        Should.Throw<IrisException>(() => _ = f.Provider).Code.ShouldBe(ErrorCode.ProviderUnavailable);
        f.Native.CapabilityCalls.ShouldBe(1); f.Native.Engines.ShouldBeEmpty(); await Task.CompletedTask;
    });

    [Fact]
    public Task Factory_RejectsBadProfileBeforeCapabilityAndSanitizesNativeFailure() => Run(async f =>
    {
        var factory = new WindowsOcrProviderFactory(f.Native, f.Images, f.Time);
        Should.Throw<ArgumentNullException>(() => factory.Create(null!, ProviderCredentials.None));
        Should.Throw<ArgumentException>(() => factory.Create(f.Profile with { Kind = OcrProviderKind.BaiduCloud }, ProviderCredentials.None));
        foreach (var profile in new[] { f.Profile with { Id = "" }, f.Profile with { TimeoutSeconds = 4 }, f.Profile with { TimeoutSeconds = 121 } })
            Should.Throw<IrisException>(() => factory.Create(profile, ProviderCredentials.None)).Code.ShouldBe(ErrorCode.InvalidArgument);
        f.Native.CapabilityCalls.ShouldBe(0);
        f.Failures["CAPABILITY"] = new IOException("private capability", unchecked((int)0x81234567));
        var error = Should.Throw<IrisException>(() => _ = f.Provider); error.Code.ShouldBe(ErrorCode.ProviderUnavailable);
        error.Detail!.ShouldContain("81234567"); error.Detail!.ShouldNotContain("private"); error.InnerException.ShouldBeNull(); await Task.CompletedTask;
    });

    [Theory]
    [InlineData("zh-Hans", "zh-Hans-CN")][InlineData("ZH-HANT", "zh-Hant-TW")]
    [InlineData("en", "en-US")][InlineData("ja", "ja-JP")][InlineData("ko", "ko-KR")][InlineData("fr-FR", "fr-FR")]
    public Task HintMapping_UsesReturnedEngineWithoutFakingDetection(string hint, string tag) => Run(async f =>
    {
        var result = await f.Recognize(await Image(), hint); f.Native.Attempts.ShouldBe(new[] { tag });
        f.Native.UserCalls.ShouldBe(0); result.DetectedLanguage.ShouldBeNull(); result.Lines.ShouldAllBe(l => l.Confidence == null);
    });

    [Theory]
    [InlineData("ja-JP", "en", "ja-JP", 0)]
    [InlineData("not a tag", "en", "en-US", 0)]
    [InlineData("en-US", "en", "USER", 1)]
    [InlineData(null, "auto", "USER", 1)]
    public Task LanguagePriorityAndDuplicateCandidates_ArePerRequest(string? option, string hint, string selected, int users) => Run(async f =>
    {
        f.Profile = f.Profile with { WindowsOptions = new() { LanguageTag = option } };
        if (selected == "USER") f.Native.Languages.Clear();
        var result = await f.Recognize(await Image(), hint); result.Lines.ShouldNotBeEmpty();
        f.Native.UserCalls.ShouldBe(users);
        if (selected == "USER" && option != null) f.Native.Attempts.ShouldBe(new[] { "en-US" });
        else if (selected != "USER") f.Native.Attempts.Last().ShouldBe(selected);
        await f.Recognize(await Image(), "fr-FR"); f.Native.Engines.Count.ShouldBe(2);
    });

    [Fact]
    public Task MissingPackagesAndNativeErrors_AreDifferentAndTestDoesNotRecognize() => Run(async f =>
    {
        f.Native.UserLanguage = null; f.Native.Languages.Clear();
        (await f.Provider.TestAsync(default)).Error!.Code.ShouldBe(ErrorCode.OcrLanguageUnsupported);
        var image = await Image();
        (await Should.ThrowAsync<IrisException>(() => f.Recognize(image, expected: typeof(IrisException)))).Code.ShouldBe(ErrorCode.OcrLanguageUnsupported);
        f.Failures["USER"] = new IOException("private preference");
        (await f.Provider.TestAsync(default)).Error!.Code.ShouldBe(ErrorCode.Unknown);
        f.Images.Items.ShouldBeEmpty(); f.Native.RecognitionCalls.ShouldBe(0);
    });

    [Theory]
    [InlineData(ImageFormat.Png)][InlineData(ImageFormat.Bmp)]
    public Task ActualDecoder_PassesMatchingSoftwareBitmapToEngine(ImageFormat format) => Run(async f =>
    {
        var image = await Image(80, 60, format); var original = image.Bytes.ToArray();
        var gate = f.Gate("RECOGNIZE"); var task = f.Recognize(image);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        var bitmap = f.Native.Engines.Single().SeenBitmap!;
        bitmap.BitmapPixelFormat.ShouldBe(BitmapPixelFormat.Bgra8); bitmap.PixelWidth.ShouldBe(80); bitmap.PixelHeight.ShouldBe(60);
        f.Images.Items.Single().BitmapDisposed.ShouldBeFalse();
        gate.Release(); await task; f.Images.Items.Single().BitmapDisposed.ShouldBeTrue(); image.Bytes.ShouldBe(original);
    });

    [Theory]
    [InlineData(0, ErrorCode.ImageInvalid)][InlineData(1, ErrorCode.ImageInvalid)][InlineData(2, ErrorCode.ImageInvalid)]
    [InlineData(3, ErrorCode.ImageTooLarge)][InlineData(4, ErrorCode.ImageInvalid)][InlineData(5, ErrorCode.ImageInvalid)]
    [InlineData(6, ErrorCode.ImageInvalid)][InlineData(7, ErrorCode.ImageTooLarge)]
    public Task InvalidInputAndHeaders_AreCheckedBeforeRecognition(int kind, ErrorCode code) => Run(async f =>
    {
        var image = await Image();
        image = kind switch
        {
            0 => image with { Bytes = [] }, 1 => image with { Format = ImageFormat.Jpeg }, 2 => image with { PixelWidth = 0 },
            3 => image with { PixelWidth = 32769 }, 4 => image with { Bytes = [1, 2, 3] },
            5 => image with { PixelWidth = 41 }, 6 => image with { Bytes = image.Bytes[..(image.Bytes.Length / 2)] },
            _ => image with { PixelWidth = 32768, PixelHeight = 32768 }
        };
        (await Should.ThrowAsync<IrisException>(() => f.Recognize(image, expected: typeof(IrisException)))).Code.ShouldBe(code);
        f.Native.RecognitionCalls.ShouldBe(0);
    });

    [Fact]
    public Task ActualDi_RegistersLocalFactoryWithReplaceableNativeBoundary() => Run(async f =>
    {
        var services = new ServiceCollection(); services.AddSingleton<IWindowsOcrNative>(f.Native); services.AddSingleton<IWindowsOcrImageFactory>(f.Images); services.AddSingleton<TimeProvider>(f.Time);
        services.AddIrisPlatformWindows();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetServices<IOcrProviderFactory>().Single(p => p.Kind == OcrProviderKind.WindowsLocal);
        using var local = (WindowsOcrProvider)factory.Create(f.Profile, ProviderCredentials.None);
        local.Descriptor.IsConfigured.ShouldBeTrue(); f.Native.CapabilityCalls.ShouldBe(1); f.Native.Engines.ShouldBeEmpty();
        await Task.CompletedTask;
    });
}
