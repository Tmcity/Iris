namespace Iris.Core.Languages;

public interface ILanguageCatalog
{
    /// <summary>全部支持的语言（不含 Auto），顺序即 UI 下拉顺序。</summary>
    IReadOnlyList<LanguageInfo> All { get; }

    LanguageInfo? Find(string code);

    /// <summary>同一语系判定：zh-Hans 与 zh-Hant 视为同族；其余按代码相等。</summary>
    bool IsSameFamily(string codeA, string codeB);

    /// <summary>Prompt 中使用的语言名；Auto 返回 "the auto-detected language"。</summary>
    string GetPromptName(string code);
}
