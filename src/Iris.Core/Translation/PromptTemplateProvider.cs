using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Iris.Core.Settings;

namespace Iris.Core.Translation;

internal sealed partial class PromptTemplateProvider : IPromptTemplateProvider
{
    private readonly ISettingsService _settings;
    private readonly ILogger<PromptTemplateProvider> _logger;

    private static readonly FrozenDictionary<string, PromptTemplate> BuiltInTemplates =
        new Dictionary<string, PromptTemplate>(StringComparer.OrdinalIgnoreCase)
        {
            [BuiltInPrompts.DefaultTemplateId] = BuiltInPrompts.Default,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, byte> _warnedTemplateIds =
        new(StringComparer.OrdinalIgnoreCase);

    public PromptTemplateProvider(ISettingsService settings, ILogger<PromptTemplateProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _logger = logger;
    }

    public PromptTemplate Get(string? templateId)
    {
        return Get(templateId, _settings.Current.Translation);
    }

    public PromptTemplate Get(string? templateId, TranslationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var effectiveId = string.IsNullOrWhiteSpace(templateId)
            ? settings.DefaultPromptTemplateId
            : templateId.Trim();

        PromptTemplate? template = null;

        // 1. 先查 CustomPrompts（不区分大小写）
        if (settings.CustomPrompts != null)
        {
            foreach (var custom in settings.CustomPrompts)
            {
                if (string.Equals(custom.Id, effectiveId, StringComparison.OrdinalIgnoreCase))
                {
                    template = custom;
                    break;
                }
            }
        }

        // 2. 再查内置模板（不区分大小写）
        if (template == null && BuiltInTemplates.TryGetValue(effectiveId, out var builtIn))
        {
            template = builtIn;
        }

        // 3. 找不到 → 回退默认模板
        if (template == null)
        {
            return BuiltInPrompts.Default;
        }

        // 4. 验证有效性：System 与 User 都不包含 {{text}} 的模板视为无效
        var hasText = template.System.Contains(PromptPlaceholders.Text, StringComparison.Ordinal)
                   || template.User.Contains(PromptPlaceholders.Text, StringComparison.Ordinal);

        if (!hasText)
        {
            if (_warnedTemplateIds.TryAdd(template.Id, 0))
            {
                LogInvalidTemplateFallback(_logger, template.Id);
            }
            return BuiltInPrompts.Default;
        }

        return template;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Prompt 模板 '{TemplateId}' 未包含 {{text}} 占位符，回退到默认模板")]
    private static partial void LogInvalidTemplateFallback(ILogger logger, string templateId);
}
