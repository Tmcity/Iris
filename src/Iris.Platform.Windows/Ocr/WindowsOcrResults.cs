using System.Buffers;
using System.Text;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Languages;
using Iris.Core.Ocr;
using Iris.Platform.Windows.Imaging;

namespace Iris.Platform.Windows.Ocr;

internal sealed class WindowsOcrException(ErrorCode code, string stage, string? id = null, string? diagnostics = null)
    : IrisException(code, ErrorMessages.GetDefault(code), providerId: id, detail: $"stage={stage};{diagnostics}");
internal static class WindowsOcrErrors
{
    internal static string Describe(string stage, Exception error) => error switch
    {
        WindowsOcrException known => $"{stage}[{known.Detail}]",
        ImagePreparationException known => $"{stage}[{known.Detail}]",
        AggregateException aggregate => $"{stage}[{string.Join(';', aggregate.InnerExceptions.Select(e => Describe("ERROR", e)))}]",
        _ => $"stage={stage};hr=0x{error.HResult:X8}"
    };
    internal static WindowsOcrException Map(string stage, Exception error, string id)
    {
        var code = error switch
        {
            WindowsOcrException known => known.Code, ImagePreparationException known => known.Code,
            _ when stage is "HEADER" or "BITMAP" && error.HResult is unchecked((int)0x88982F07) or unchecked((int)0x88982F50) or
                unchecked((int)0x88982F60) or unchecked((int)0x88982F61) or unchecked((int)0x88982F62) or unchecked((int)0x88982F63) or
                unchecked((int)0x88982F70) or unchecked((int)0x88982F72) or unchecked((int)0x80070057) => ErrorCode.ImageInvalid,
            _ => ErrorCode.Unknown
        };
        return new(code, stage, id, Describe(stage, error));
    }
}
internal static class WindowsOcrResults
{
    internal static IReadOnlyList<OcrLine> Convert(IReadOnlyList<WindowsOcrNativeLine>? source, Action check)
    {
        if (source == null || source.Count is < 0 or > 20000) throw Invalid();
        long words = 0, textLength = 0;
        var result = new List<OcrLine>(source.Count);
        foreach (var line in source)
        {
            check();
            if (line?.Text == null || line.Words == null || line.Words.Count < 0 || result.Count >= 20000) throw Invalid();
            textLength += line.Text.Length; words += line.Words.Count;
            if (textLength > 1000000 || words > 100000) throw Invalid();
            PixelRect? bounds = null;
            long left = int.MaxValue, top = int.MaxValue, right = 0, bottom = 0;
            foreach (var word in line.Words)
            {
                check();
                double r = word.X + word.Width, b = word.Y + word.Height;
                if (!double.IsFinite(word.X) || !double.IsFinite(word.Y) || !double.IsFinite(word.Width) || !double.IsFinite(word.Height) ||
                    !double.IsFinite(r) || !double.IsFinite(b) || word.X < 0 || word.Y < 0 || word.Width <= 0 || word.Height <= 0 || r > int.MaxValue || b > int.MaxValue) throw Invalid();
                long x = (long)Math.Floor(word.X), y = (long)Math.Floor(word.Y), x1 = (long)Math.Ceiling(r), y1 = (long)Math.Ceiling(b);
                if (x1 <= x || y1 <= y) throw Invalid();
                left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x1); bottom = Math.Max(bottom, y1);
            }
            if (line.Words.Count != 0) bounds = new((int)left, (int)top, checked((int)(right - left)), checked((int)(bottom - top)));
            result.Add(new(RemoveCjkSpaces(line.Text, check), bounds));
        }
        return result.AsReadOnly();
    }
    private static string RemoveCjkSpaces(string text, Action check)
    {
        StringBuilder? builder = null; int copy = 0, nextCheck = 0; bool previousCjk = false;
        for (int i = 0; i < text.Length;)
        {
            if (i >= nextCheck) { check(); nextCheck = i + 1024; }
            if (text[i] == ' ' && previousCjk && i + 1 < text.Length &&
                Rune.DecodeFromUtf16(text.AsSpan(i + 1), out var next, out _) == OperationStatus.Done && UnicodeScripts.IsCjkForLineMerge(next))
            {
                builder ??= new(text.Length); builder.Append(text.AsSpan(copy, i - copy)); copy = i + 1;
            }
            var status = Rune.DecodeFromUtf16(text.AsSpan(i), out var current, out int consumed);
            previousCjk = status == OperationStatus.Done && UnicodeScripts.IsCjkForLineMerge(current);
            i += Math.Max(1, consumed);
        }
        if (builder == null) return text;
        builder.Append(text.AsSpan(copy)); return builder.ToString();
    }
    private static WindowsOcrException Invalid() => new(ErrorCode.InvalidResponse, "NATIVE_RESULT");
}
