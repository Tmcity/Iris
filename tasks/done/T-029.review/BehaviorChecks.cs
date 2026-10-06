using System.Text.Json;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Ocr;
using Iris.Platform.Windows.Ocr;
using static ProviderScope;

internal static class BehaviorChecks
{
    internal static async Task Run(string directory, Func<string,Func<Task>,Task> check)
    {
        await check("FACTORY_CAPABILITY_ONLY_NO_ENGINE_OR_IMAGE", () => ProviderScope.Run(f =>
        {
            var c = f.Provider.Constraints;
            Require(f.Boundary.CapabilityReads == 1 && f.Boundary.Engines.IsEmpty && f.Boundary.Images.IsEmpty && f.Boundary.LanguageRequests.IsEmpty, "creation exceeded the single capability query");
            Require(f.Provider.Descriptor.IsConfigured && c.MaxLongSide == 10000 && c.MinShortSide == 40 && !c.MaxBytesAppliesToBase64 && c.MaxFormUrlEncodedBytes == null && c.AcceptedFormats.SequenceEqual(new[] { ImageFormat.Png,ImageFormat.Bmp }), "local constraints do not reflect capabilities");
            return Task.CompletedTask;
        }));
        using var cases = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory,"rune-cases.json")));
        foreach (var item in cases.RootElement.EnumerateArray())
        {
            var row = item.Clone();
            await check("ACTUAL_PROVIDER_RUNE_" + row.GetProperty("name").GetString(), () => ProviderScope.Run(async f =>
            {
                f.Boundary.Lines = [new(row.GetProperty("input").GetString(), [])];
                var result = await f.Track(f.Provider.RecognizeAsync(new(await Input(directory), "en"), default));
                Require(result.Lines.Count == 1 && result.Lines[0].Text == row.GetProperty("expected").GetString(), "rune spacing differs from fixed independent expectation");
                Require(result.Lines[0].Bounds == null && result.Lines[0].Confidence == null && result.DetectedLanguage == null && f.AllReleased, "missing metadata was invented or image resources remain");
            }));
        }
        await check("FRACTIONAL_WORD_BOUNDS_FORM_OUTWARD_INTEGER_UNION", () => ProviderScope.Run(async f =>
        {
            var result = await f.Track(f.Provider.RecognizeAsync(new(await Input(directory), "en"), default));
            Require(result.Lines.Single().Bounds == new PixelRect(1,2,19,7) && result.Lines[0].Text == "你好", "fractional words did not produce the required pixel union");
        }));
        var languageCases = new[]
        {
            ("ja-JP", "zh-Hans", new[] { "ja-JP" }, "ja-JP"),
            ("fr-FR", "zh-Hans", new[] { "fr-FR", "zh-Hans-CN" }, "zh-Hans-CN"),
            ((string?)null, "auto", new[] { "<user>" }, "en-US"),
            ((string?)null, "fr-CA", new[] { "fr-CA" }, "fr-CA")
        };
        foreach (var (configured, hint, requests, supported) in languageCases)
            await check("LANGUAGE_PRIORITY_" + (configured ?? "none") + "_" + hint, () => ProviderScope.Run(async f =>
            {
                f.Boundary.Supported.Add(supported);
                await f.Track(f.Provider.RecognizeAsync(new(await Input(directory), hint), default));
                Require(f.Boundary.LanguageRequests.SequenceEqual(requests, StringComparer.OrdinalIgnoreCase), "language priority or fallback sequence changed");
            }, configured));
        await check("MISSING_LANGUAGES_HAVE_EXPLICIT_ERROR", () => ProviderScope.Run(async f =>
        {
            f.Boundary.Supported.Clear(); f.Boundary.UserLanguage = null;
            var task = f.Track(f.Provider.RecognizeAsync(new(await Input(directory),"en"),default),typeof(IrisException));
            try { await task; throw new InvalidOperationException("unavailable languages succeeded"); }
            catch (IrisException ex) { Require(ex.Code == ErrorCode.OcrLanguageUnsupported, "missing language error mismatch"); }
            Require(f.Boundary.Recognitions == 0 && f.AllReleased, "missing language performed OCR or left image resources behind");
        }));
        await check("TEST_CHECKS_LANGUAGE_WITHOUT_IMAGE_OR_RECOGNITION", () => ProviderScope.Run(async f =>
        {
            var result = await f.Track(f.Provider.TestAsync(default));
            Require(result.Success && f.Boundary.Recognitions == 0 && f.Boundary.Images.IsEmpty, "TestAsync performed OCR or image work");
        }));
        await check("LONG_SUPPLEMENTARY_TEXT_REACHES_CANCELLATION_CHECKPOINTS", () =>
        {
            string text = "A" + string.Concat(Enumerable.Repeat("𠀀", 100000));
            int checkpoints = 0; bool canceled = false;
            try { WindowsOcrResults.Convert([new(text, [])], () => { if (++checkpoints == 3) throw new OperationCanceledException(); }); }
            catch (OperationCanceledException) { canceled = true; }
            Require(canceled, "odd-aligned supplementary run skipped all later cancellation checkpoints");
            return Task.CompletedTask;
        });
    }
}
