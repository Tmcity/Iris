using Iris.Core.Imaging;
using Iris.Core.Languages;

namespace Iris.Core.Ocr;

public sealed record OcrRequest(ImageData Image, string LanguageHint = LanguageCodes.Auto);

/// <param name="Bounds">行在“输入图像”中的像素位置（无位置信息的接口为 null）。</param>
public sealed record OcrLine(string Text, PixelRect? Bounds = null, double? Confidence = null);

public sealed record OcrResult(string ProviderId, IReadOnlyList<OcrLine> Lines, string? DetectedLanguage, TimeSpan Elapsed);

public enum OcrTextLayout
{
    /// <summary>保持 OCR 原始分行。</summary>
    KeepLines,

    /// <summary>
    /// 合并段内换行：相邻两行之间，若前行以句末标点（。！？.!?：:）结尾或行距明显大于平均行距则保留换行，否则合并；
    /// 合并时拉丁文字之间插入空格、行尾连字符 "-" 去除后直接拼接，CJK 之间不插空格。
    /// </summary>
    MergeParagraphs,
}

/// <param name="Text">经 IOcrTextNormalizer 规范化后的文本。</param>
/// <param name="UsedFallback">是否因主服务失败/未配置而使用了备用服务。</param>
public sealed record OcrOutcome(OcrResult Raw, string Text, bool UsedFallback);
