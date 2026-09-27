using System;
using System.Net.Http;
using Iris.Core.Common;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Microsoft.Extensions.Logging;

namespace Iris.Providers.Translation.Anthropic;

internal sealed class AnthropicTranslationProviderFactory : ITranslationProviderFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPromptTemplateProvider _promptTemplates;
    private readonly IPromptRenderer _promptRenderer;
    private readonly ISettingsService _settings;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;

    public AnthropicTranslationProviderFactory(
        IHttpClientFactory httpClientFactory,
        IPromptTemplateProvider promptTemplates,
        IPromptRenderer promptRenderer,
        ISettingsService settings,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(promptTemplates);
        ArgumentNullException.ThrowIfNull(promptRenderer);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _httpClientFactory = httpClientFactory;
        _promptTemplates = promptTemplates;
        _promptRenderer = promptRenderer;
        _settings = settings;
        _timeProvider = timeProvider;
        _loggerFactory = loggerFactory;
    }

    public TranslationProviderKind Kind => TranslationProviderKind.Anthropic;

    public ITranslationProvider Create(TranslationProviderProfile profile, ProviderCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(credentials);

        if (profile.Kind != TranslationProviderKind.Anthropic)
        {
            throw new ArgumentException($"Expected kind {TranslationProviderKind.Anthropic}, but got {profile.Kind}", nameof(profile));
        }

        return new AnthropicTranslationProvider(
            profile,
            credentials,
            _httpClientFactory,
            _promptTemplates,
            _promptRenderer,
            _settings,
            _timeProvider,
            _loggerFactory.CreateLogger<AnthropicTranslationProvider>());
    }
}
