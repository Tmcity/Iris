using Iris.Core.Errors;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Iris.Platform.Windows.Ocr;

internal readonly record struct WindowsOcrBox(double X, double Y, double Width, double Height);
internal sealed class WindowsOcrNativeLine(string? text, IReadOnlyList<WindowsOcrBox>? words)
{
    internal string? Text { get; } = text;
    internal IReadOnlyList<WindowsOcrBox>? Words { get; } = words;
}
internal interface IWindowsOcrEngine
{
    string LanguageTag { get; }
    Task<IReadOnlyList<WindowsOcrNativeLine>> RecognizeAsync(SoftwareBitmap bitmap, Action check);
}
internal interface IWindowsOcrNative
{
    uint MaxImageDimension { get; }
    IWindowsOcrEngine? TryLanguage(string tag);
    IWindowsOcrEngine? TryUserLanguages();
}
internal sealed class WindowsOcrNative : IWindowsOcrNative
{
    public uint MaxImageDimension => OcrEngine.MaxImageDimension;
    public IWindowsOcrEngine? TryLanguage(string tag)
    {
        Language language;
        try { if (!Language.IsWellFormed(tag)) return null; language = new Language(tag); }
        catch (ArgumentException) { return null; }
        var engine = OcrEngine.TryCreateFromLanguage(language);
        return engine == null ? null : new Engine(engine);
    }
    public IWindowsOcrEngine? TryUserLanguages()
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        return engine == null ? null : new Engine(engine);
    }
    private sealed class Engine(OcrEngine engine) : IWindowsOcrEngine
    {
        public string LanguageTag => engine.RecognizerLanguage.LanguageTag;
        public async Task<IReadOnlyList<WindowsOcrNativeLine>> RecognizeAsync(SoftwareBitmap bitmap, Action check)
        {
            var result = await engine.RecognizeAsync(bitmap).AsTask().ConfigureAwait(false);
            check();
            if (result.Lines.Count > 20000) throw new WindowsOcrException(ErrorCode.InvalidResponse, "LINE_BUDGET");
            var lines = new List<WindowsOcrNativeLine>(result.Lines.Count); long words = 0, text = 0;
            foreach (var line in result.Lines)
            {
                check(); words += line.Words.Count; text += line.Text?.Length ?? 0;
                if (words > 100000 || text > 1000000) throw new WindowsOcrException(ErrorCode.InvalidResponse, "RESULT_BUDGET");
                var boxes = new List<WindowsOcrBox>(line.Words.Count);
                foreach (var word in line.Words)
                {
                    check(); var r = word.BoundingRect; boxes.Add(new(r.X, r.Y, r.Width, r.Height));
                }
                lines.Add(new(line.Text, boxes));
            }
            return lines;
        }
    }
}
