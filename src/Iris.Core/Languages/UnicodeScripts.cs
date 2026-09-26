using System.Text;

namespace Iris.Core.Languages;

internal enum UnicodeScript
{
    Other,
    Kana,
    Hangul,
    Han,
    Cyrillic,
    Thai,
    Arabic,
    Latin,
}

internal static class UnicodeScripts
{
    /// <summary>码点范围严格按 ARCH §7.3.2 的表格。</summary>
    public static UnicodeScript Classify(Rune r)
    {
        var v = r.Value;
        return v switch
        {
            // 假名：3040–309F、30A0–30FF、31F0–31FF、FF66–FF9F
            >= 0x3040 and <= 0x309F => UnicodeScript.Kana,
            >= 0x30A0 and <= 0x30FF => UnicodeScript.Kana,
            >= 0x31F0 and <= 0x31FF => UnicodeScript.Kana,
            >= 0xFF66 and <= 0xFF9F => UnicodeScript.Kana,

            // 谚文：1100–11FF、3130–318F、A960–A97F、AC00–D7AF、D7B0–D7FF
            >= 0x1100 and <= 0x11FF => UnicodeScript.Hangul,
            >= 0x3130 and <= 0x318F => UnicodeScript.Hangul,
            >= 0xA960 and <= 0xA97F => UnicodeScript.Hangul,
            >= 0xAC00 and <= 0xD7AF => UnicodeScript.Hangul,
            >= 0xD7B0 and <= 0xD7FF => UnicodeScript.Hangul,

            // 汉字：3400–4DBF、4E00–9FFF、F900–FAFF、20000–323AF
            >= 0x3400 and <= 0x4DBF => UnicodeScript.Han,
            >= 0x4E00 and <= 0x9FFF => UnicodeScript.Han,
            >= 0xF900 and <= 0xFAFF => UnicodeScript.Han,
            >= 0x20000 and <= 0x323AF => UnicodeScript.Han,

            // 西里尔：0400–052F
            >= 0x0400 and <= 0x052F => UnicodeScript.Cyrillic,

            // 泰文：0E00–0E7F
            >= 0x0E00 and <= 0x0E7F => UnicodeScript.Thai,

            // 阿拉伯：0600–06FF、0750–077F、08A0–08FF、FB50–FDFF、FE70–FEFF
            >= 0x0600 and <= 0x06FF => UnicodeScript.Arabic,
            >= 0x0750 and <= 0x077F => UnicodeScript.Arabic,
            >= 0x08A0 and <= 0x08FF => UnicodeScript.Arabic,
            >= 0xFB50 and <= 0xFDFF => UnicodeScript.Arabic,
            >= 0xFE70 and <= 0xFEFF => UnicodeScript.Arabic,

            // 拉丁：0041–005A、0061–007A、00C0–024F、1E00–1EFF、FF21–FF3A、FF41–FF5A
            >= 0x0041 and <= 0x005A => UnicodeScript.Latin,
            >= 0x0061 and <= 0x007A => UnicodeScript.Latin,
            >= 0x00C0 and <= 0x024F => UnicodeScript.Latin,
            >= 0x1E00 and <= 0x1EFF => UnicodeScript.Latin,
            >= 0xFF21 and <= 0xFF3A => UnicodeScript.Latin,
            >= 0xFF41 and <= 0xFF5A => UnicodeScript.Latin,

            _ => UnicodeScript.Other,
        };
    }

    /// <summary>ARCH §7.5 中“CJK”的定义：汉字、假名、U+3000–303F、U+FF00–FFEF。谚文不算 CJK。</summary>
    public static bool IsCjkForLineMerge(Rune r)
    {
        var v = r.Value;
        if (v is (>= 0x3000 and <= 0x303F) or (>= 0xFF00 and <= 0xFFEF))
        {
            return true;
        }

        var script = Classify(r);
        return script is UnicodeScript.Han or UnicodeScript.Kana;
    }
}
