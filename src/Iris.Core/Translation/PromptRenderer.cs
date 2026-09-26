using System.Text.RegularExpressions;
using Iris.Core.Languages;

namespace Iris.Core.Translation;

internal sealed partial class PromptRenderer : IPromptRenderer
{
    private readonly ILanguageCatalog _catalog;

    public PromptRenderer(ILanguageCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }

    [GeneratedRegex(@"\{\{([a-z_]+)\}\}")]
    private static partial Regex PlaceholderRegex();

    public RenderedPrompt Render(PromptTemplate template, ProviderTranslateRequest request)
    {
        var sourceName = _catalog.GetPromptName(request.SourceLanguage);
        var targetName = _catalog.GetPromptName(request.TargetLanguage);

        string Evaluator(Match match)
        {
            var placeholder = match.Groups[1].Value;
            return placeholder switch
            {
                "source_language" => sourceName,
                "target_language" => targetName,
                "text" => request.Text,
                _ => match.Value,
            };
        }

        var renderedSystem = PlaceholderRegex().Replace(template.System, Evaluator);
        var renderedUser = PlaceholderRegex().Replace(template.User, Evaluator);

        return new RenderedPrompt(renderedSystem, renderedUser);
    }
}
