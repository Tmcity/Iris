namespace Iris.Core.Languages;

public interface ILanguageCatalog
{
    /// <summary>全部支持的语言（不含 Auto），顺序即 UI 下拉顺序。</summary>
    IReadOnlyList<LanguageInfo> All { get; }

    LanguageInfo? Find(string code);

    /// <summary>同一语系判定：比较 BCP-47 主语言子标签（第一个 '-' 之前的部分，不区分大小写），因此 zh-Hans 与 zh-Hant 同族、en 与 en-US 同族；任一方为 Auto、null 或空白时返回 false。</summary>
    bool IsSameFamily(string codeA, string codeB);

    /// <summary>Prompt 中使用的语言名；Auto 返回 "the auto-detected language"；不在目录中的代码原样返回（去除首尾空白）。</summary>
    string GetPromptName(string code);
}
