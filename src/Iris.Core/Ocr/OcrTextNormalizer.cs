using Iris.Core.Imaging;
using Iris.Core.Text;

namespace Iris.Core.Ocr;

internal sealed class OcrTextNormalizer : IOcrTextNormalizer
{
    public string Normalize(IReadOnlyList<OcrLine> lines, OcrTextLayout layout)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (layout is not (OcrTextLayout.KeepLines or OcrTextLayout.MergeParagraphs))
            throw new ArgumentOutOfRangeException(nameof(layout));
        var text = new string[lines.Count]; double height = 0; int count = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i]?.Text == null) throw new ArgumentException("Invalid OCR line.", nameof(lines));
            text[i] = lines[i].Text;
            if (Valid(lines[i].Bounds)) { height += lines[i].Bounds!.Value.Height; count++; }
        }
        if (layout == OcrTextLayout.KeepLines) return string.Join('\n', text);
        double threshold = count == 0 ? 0 : height / count * 1.5;
        return SoftLineBreakMerger.Merge(text, i =>
        {
            var current = lines[i].Bounds; var next = lines[i + 1].Bounds;
            return count != 0 && Valid(current) && Valid(next) &&
                (long)next!.Value.Y - ((long)current!.Value.Y + current.Value.Height) > threshold;
        });
    }
    private static bool Valid(PixelRect? rect) => rect is { X: >= 0, Y: >= 0, Width: > 0, Height: > 0 };
}
