using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Iris.Core.Common;
using Iris.Core.Settings;

namespace Iris.App.Settings;

/// <summary>
/// 翻译服务草稿 ViewModel。管理单个 Profile 的输入、凭据意图、高级选项、revision 与校验。
/// 完整规则见 specs/PROVIDER_SETTINGS_UI.md §2、§3、§4。
/// </summary>
internal sealed partial class TranslationProfileDraftViewModel : INotifyPropertyChanged
{
    private static readonly HashSet<string> SensitiveHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "x-api-key", "api-key"
    };

    [GeneratedRegex(@"\A[A-Za-z0-9!#$%&'*+\-.^_`|~]+\z")]
    private static partial Regex HttpTokenRegex();

    private readonly object _lock = new();
    private long _revision;

    private string _displayName;
    private bool _enabled;
    private string? _baseUrl;
    private string _model;
    private int _timeoutSeconds;
    private string _timeoutText;
    private int? _maxOutputTokens;
    private string? _maxOutputTokensText;
    private double? _temperature;
    private string? _temperatureText;
    private string? _promptTemplateId;

    private CredentialEditKind _credentialEditKind = CredentialEditKind.Keep;
    private string? _replacementApiKey;

    // OpenAI 兼容选项
    private bool _stream = true;
    private bool _includeStreamUsage;
    private MaxTokensField _maxTokensField = MaxTokensField.MaxTokens;
    private bool _stripThinkTags = true;
    private string _chatCompletionsPath = "chat/completions";
    private string _extraHeadersText = string.Empty;

    // Anthropic 选项
    private string? _effort = "low";
    private bool _refusalFallback = true;

    // 提示状态
    private bool _hasHaikuEffortWarning;
    private bool _hasAnthropicV1Warning;

    // 测试与模型列表结果状态
    private string? _testStatusMessage;
    private bool? _testSuccess;
    private long? _testLatencyMs;
    private string? _testSampleTranslation;
    private string? _listModelsStatusMessage;

    public event PropertyChangedEventHandler? PropertyChanged;

    public TranslationProfileDraftViewModel(
        TranslationProviderProfile? baseProfile,
        string id,
        TranslationProviderKind kind,
        string displayName,
        string? baseUrl,
        string model,
        bool enabled = true)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Kind = kind;
        BaseProfile = baseProfile;

        _displayName = displayName ?? string.Empty;
        _enabled = enabled;
        _baseUrl = baseUrl;
        _model = model ?? string.Empty;

        if (baseProfile != null)
        {
            _timeoutSeconds = baseProfile.TimeoutSeconds;
            _maxOutputTokens = baseProfile.MaxOutputTokens;
            _temperature = baseProfile.Temperature;
            _promptTemplateId = baseProfile.PromptTemplateId;

            if (baseProfile.OpenAIOptions != null)
            {
                _stream = baseProfile.OpenAIOptions.Stream;
                _includeStreamUsage = baseProfile.OpenAIOptions.IncludeStreamUsage;
                _maxTokensField = baseProfile.OpenAIOptions.MaxTokensField;
                _stripThinkTags = baseProfile.OpenAIOptions.StripThinkTags;
                _chatCompletionsPath = baseProfile.OpenAIOptions.ChatCompletionsPath ?? "chat/completions";
                if (baseProfile.OpenAIOptions.ExtraHeaders != null)
                {
                    _extraHeadersText = string.Join(Environment.NewLine,
                        baseProfile.OpenAIOptions.ExtraHeaders.Select(kv => $"{kv.Key}: {kv.Value}"));
                }
            }

            if (baseProfile.AnthropicOptions != null)
            {
                _effort = baseProfile.AnthropicOptions.Effort;
                _refusalFallback = baseProfile.AnthropicOptions.RefusalFallback;
            }
        }
        else
        {
            _timeoutSeconds = 60;
            _maxOutputTokens = null;
            _temperature = null;
            _promptTemplateId = null;
            _effort = kind == TranslationProviderKind.Anthropic ? "low" : null;
        }

        _timeoutText = _timeoutSeconds.ToString(CultureInfo.InvariantCulture);
        _maxOutputTokensText = _maxOutputTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _temperatureText = _temperature?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

        UpdateAnthropicWarnings();
    }

    public static TranslationProfileDraftViewModel FromExisting(TranslationProviderProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new TranslationProfileDraftViewModel(
            baseProfile: profile,
            id: profile.Id,
            kind: profile.Kind,
            displayName: profile.DisplayName,
            baseUrl: profile.BaseUrl,
            model: profile.Model,
            enabled: profile.Enabled);
    }

    public static TranslationProfileDraftViewModel CreateFromPreset(TranslationProviderPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var id = "tp-" + Guid.NewGuid().ToString("N")[..12];
        return new TranslationProfileDraftViewModel(
            baseProfile: null,
            id: id,
            kind: preset.Kind,
            displayName: preset.DisplayName,
            baseUrl: preset.BaseUrl,
            model: preset.SuggestedModel ?? string.Empty,
            enabled: true);
    }

    public string Id { get; }
    public TranslationProviderKind Kind { get; }
    public TranslationProviderProfile? BaseProfile { get; private set; }
    public bool IsNew => BaseProfile == null;

    public long Revision => _revision;

    public ObservableCollection<string> AvailableModels { get; } = [];

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (_displayName != value)
            {
                _displayName = value;
                IncrementRevision();
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled != value)
            {
                _enabled = value;
                IncrementRevision();
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    public string? BaseUrl
    {
        get => _baseUrl;
        set
        {
            if (_baseUrl != value)
            {
                _baseUrl = value;
                IncrementRevision();
                UpdateAnthropicWarnings();
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    public string Model
    {
        get => _model;
        set
        {
            var trimmed = value ?? string.Empty;
            if (_model != trimmed)
            {
                _model = trimmed;
                IncrementRevision();

                // Haiku Effort 规则：若模型含 haiku，在草稿中清空 Effort 并提示，旧测试失效；切回时不自动恢复
                if (Kind == TranslationProviderKind.Anthropic)
                {
                    if (_model.Contains("haiku", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_effort != null)
                        {
                            _effort = null;
                            OnPropertyChanged(nameof(Effort));
                            HasHaikuEffortWarning = true;
                            ClearTestResult();
                        }
                    }
                    else
                    {
                        HasHaikuEffortWarning = false;
                    }
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    public string TimeoutText
    {
        get => _timeoutText;
        set
        {
            if (_timeoutText != value)
            {
                _timeoutText = value;
                if (int.TryParse(_timeoutText, out var parsed) && parsed is >= 5 and <= 600)
                {
                    _timeoutSeconds = parsed;
                }
                IncrementRevision();
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    public int TimeoutSeconds => _timeoutSeconds;

    public string? MaxOutputTokensText
    {
        get => _maxOutputTokensText;
        set
        {
            if (_maxOutputTokensText != value)
            {
                _maxOutputTokensText = value;
                if (string.IsNullOrWhiteSpace(_maxOutputTokensText))
                {
                    _maxOutputTokens = null;
                }
                else if (int.TryParse(_maxOutputTokensText, out var parsed) && parsed > 0)
                {
                    _maxOutputTokens = parsed;
                }
                IncrementRevision();
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    public int? MaxOutputTokens => _maxOutputTokens;

    public bool IsTemperatureVisible => Kind != TranslationProviderKind.Anthropic;

    public string? TemperatureText
    {
        get => _temperatureText;
        set
        {
            if (_temperatureText != value)
            {
                _temperatureText = value;
                if (string.IsNullOrWhiteSpace(_temperatureText))
                {
                    _temperature = null;
                }
                else if (double.TryParse(_temperatureText, out var parsed) && parsed is >= 0.0 and <= 2.0 && !double.IsNaN(parsed) && !double.IsInfinity(parsed))
                {
                    _temperature = parsed;
                }
                IncrementRevision();
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    public double? Temperature => _temperature;

    public string? PromptTemplateId
    {
        get => _promptTemplateId;
        set
        {
            var val = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_promptTemplateId != val)
            {
                _promptTemplateId = val;
                IncrementRevision();
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    public CredentialEditKind CredentialEditKind
    {
        get => _credentialEditKind;
        set
        {
            if (_credentialEditKind != value)
            {
                _credentialEditKind = value;
                IncrementRevision();
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsReplaceCredential));
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    public bool IsReplaceCredential => _credentialEditKind == CredentialEditKind.Replace;

    public void SetReplacementApiKey(string? apiKey)
    {
        _replacementApiKey = apiKey;
        IncrementRevision();
        OnPropertyChanged(nameof(IsDirty));
    }

    public void ClearReplacementApiKey()
    {
        _replacementApiKey = null;
        IncrementRevision();
        OnPropertyChanged(nameof(IsDirty));
    }

    // OpenAI 选项
    public bool Stream
    {
        get => _stream;
        set
        {
            if (_stream != value)
            {
                _stream = value;
                IncrementRevision();
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    public bool IncludeStreamUsage
    {
        get => _includeStreamUsage;
        set
        {
            if (_includeStreamUsage != value)
            {
                _includeStreamUsage = value;
                IncrementRevision();
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    public MaxTokensField MaxTokensField
    {
        get => _maxTokensField;
        set
        {
            if (_maxTokensField != value)
            {
                _maxTokensField = value;
                IncrementRevision();
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    public bool StripThinkTags
    {
        get => _stripThinkTags;
        set
        {
            if (_stripThinkTags != value)
            {
                _stripThinkTags = value;
                IncrementRevision();
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    public string ChatCompletionsPath
    {
        get => _chatCompletionsPath;
        set
        {
            if (_chatCompletionsPath != value)
            {
                _chatCompletionsPath = value;
                IncrementRevision();
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    public string ExtraHeadersText
    {
        get => _extraHeadersText;
        set
        {
            if (_extraHeadersText != value)
            {
                _extraHeadersText = value;
                IncrementRevision();
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    // Anthropic 选项
    public string? Effort
    {
        get => _effort;
        set
        {
            if (_effort != value)
            {
                _effort = value;
                IncrementRevision();
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    public bool RefusalFallback
    {
        get => _refusalFallback;
        set
        {
            if (_refusalFallback != value)
            {
                _refusalFallback = value;
                IncrementRevision();
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    public bool HasHaikuEffortWarning
    {
        get => _hasHaikuEffortWarning;
        private set
        {
            if (_hasHaikuEffortWarning != value)
            {
                _hasHaikuEffortWarning = value;
                OnPropertyChanged();
            }
        }
    }

    public bool HasAnthropicV1Warning
    {
        get => _hasAnthropicV1Warning;
        private set
        {
            if (_hasAnthropicV1Warning != value)
            {
                _hasAnthropicV1Warning = value;
                OnPropertyChanged();
            }
        }
    }

    public string? TestStatusMessage
    {
        get => _testStatusMessage;
        set
        {
            if (_testStatusMessage != value)
            {
                _testStatusMessage = value;
                OnPropertyChanged();
            }
        }
    }

    public bool? TestSuccess
    {
        get => _testSuccess;
        set
        {
            if (_testSuccess != value)
            {
                _testSuccess = value;
                OnPropertyChanged();
            }
        }
    }

    public long? TestLatencyMs
    {
        get => _testLatencyMs;
        set
        {
            if (_testLatencyMs != value)
            {
                _testLatencyMs = value;
                OnPropertyChanged();
            }
        }
    }

    public string? TestSampleTranslation
    {
        get => _testSampleTranslation;
        set
        {
            if (_testSampleTranslation != value)
            {
                _testSampleTranslation = value;
                OnPropertyChanged();
            }
        }
    }

    public string? ListModelsStatusMessage
    {
        get => _listModelsStatusMessage;
        set
        {
            if (_listModelsStatusMessage != value)
            {
                _listModelsStatusMessage = value;
                OnPropertyChanged();
            }
        }
    }

    public void ClearTestResult()
    {
        TestStatusMessage = null;
        TestSuccess = null;
        TestLatencyMs = null;
        TestSampleTranslation = null;
    }

    public void IncrementRevision()
    {
        Interlocked.Increment(ref _revision);
    }

    private void UpdateAnthropicWarnings()
    {
        if (Kind == TranslationProviderKind.Anthropic)
        {
            var trimmedUrl = _baseUrl?.Trim().TrimEnd('/');
            HasAnthropicV1Warning = !string.IsNullOrEmpty(trimmedUrl) && trimmedUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            HasAnthropicV1Warning = false;
        }
    }

    public bool IsDirty
    {
        get
        {
            if (BaseProfile == null)
            {
                return true;
            }

            if (_credentialEditKind != CredentialEditKind.Keep)
            {
                return true;
            }

            if (_displayName != BaseProfile.DisplayName) return true;
            if (_enabled != BaseProfile.Enabled) return true;

            var baseNormUrl = string.IsNullOrWhiteSpace(BaseProfile.BaseUrl) ? null : BaseProfile.BaseUrl.Trim();
            var currNormUrl = string.IsNullOrWhiteSpace(_baseUrl) ? null : _baseUrl.Trim();
            if (!string.Equals(baseNormUrl, currNormUrl, StringComparison.Ordinal)) return true;

            if (!string.Equals(_model, BaseProfile.Model, StringComparison.Ordinal)) return true;
            if (_timeoutSeconds != BaseProfile.TimeoutSeconds) return true;
            if (_maxOutputTokens != BaseProfile.MaxOutputTokens) return true;

            if (Kind != TranslationProviderKind.Anthropic)
            {
                if (_temperature != BaseProfile.Temperature) return true;
            }

            if (!string.Equals(_promptTemplateId, BaseProfile.PromptTemplateId, StringComparison.Ordinal)) return true;

            if (Kind == TranslationProviderKind.OpenAICompatible)
            {
                var baseOpts = BaseProfile.OpenAIOptions ?? new OpenAICompatibleOptions();
                if (_stream != baseOpts.Stream) return true;
                if (_includeStreamUsage != baseOpts.IncludeStreamUsage) return true;
                if (_maxTokensField != baseOpts.MaxTokensField) return true;
                if (_stripThinkTags != baseOpts.StripThinkTags) return true;
                if (!string.Equals(_chatCompletionsPath, baseOpts.ChatCompletionsPath, StringComparison.Ordinal)) return true;

                var (currHeaders, _) = ParseExtraHeaders(_extraHeadersText);
                var baseHeaders = baseOpts.ExtraHeaders ?? new Dictionary<string, string>();
                if (currHeaders == null || !DictionaryEqual(currHeaders, baseHeaders)) return true;
            }
            else if (Kind == TranslationProviderKind.Anthropic)
            {
                var baseOpts = BaseProfile.AnthropicOptions ?? new AnthropicOptions();
                if (!string.Equals(_effort, baseOpts.Effort, StringComparison.Ordinal)) return true;
                if (_refusalFallback != baseOpts.RefusalFallback) return true;
            }

            return false;
        }
    }

    private static bool DictionaryEqual(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (k, v) in a)
        {
            if (!b.TryGetValue(k, out var bv) || !string.Equals(v, bv, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    public static (IReadOnlyDictionary<string, string>? Headers, string? Error) ParseExtraHeaders(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (new Dictionary<string, string>(), null);
        }

        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            var colonIdx = trimmed.IndexOf(':');
            if (colonIdx <= 0)
            {
                return (null, "请求头格式错误，应为 'Name: Value'");
            }

            var name = trimmed[..colonIdx].Trim();
            var val = trimmed[(colonIdx + 1)..].Trim();

            if (!HttpTokenRegex().IsMatch(name))
            {
                return (null, $"无效的请求头名称: {name}");
            }

            if (SensitiveHeaderNames.Contains(name))
            {
                return (null, $"不得在附加头中包含敏感身份头: {name}");
            }

            if (dict.ContainsKey(name))
            {
                return (null, $"存在重复的请求头名称: {name}");
            }

            dict[name] = val;
        }

        return (dict, null);
    }

    public string? ValidateForSave()
    {
        if (string.IsNullOrWhiteSpace(_displayName))
        {
            return "服务显示名称不能为空";
        }

        if (int.TryParse(_timeoutText, out var timeout) && timeout is >= 5 and <= 600)
        {
            // ok
        }
        else
        {
            return "超时时间必须在 5 到 600 秒之间";
        }

        if (!string.IsNullOrWhiteSpace(_maxOutputTokensText))
        {
            if (!int.TryParse(_maxOutputTokensText, out var maxTokens) || maxTokens <= 0)
            {
                return "最大输出 Tokens 必须为正整数";
            }
        }

        if (Kind != TranslationProviderKind.Anthropic && !string.IsNullOrWhiteSpace(_temperatureText))
        {
            if (!double.TryParse(_temperatureText, out var temp) || temp is < 0.0 or > 2.0 || double.IsNaN(temp) || double.IsInfinity(temp))
            {
                return "采样温度必须在 0.0 到 2.0 之间";
            }
        }

        if (Kind == TranslationProviderKind.OpenAICompatible)
        {
            if (string.IsNullOrWhiteSpace(_baseUrl))
            {
                return "OpenAI 兼容协议必须提供 BaseUrl";
            }

            if (!Uri.TryCreate(_baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return "BaseUrl 必须为合法的 http(s) 地址";
            }

            if (string.IsNullOrWhiteSpace(_chatCompletionsPath))
            {
                return "ChatCompletionsPath 不能为空";
            }

            if (_chatCompletionsPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                _chatCompletionsPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                _chatCompletionsPath.StartsWith("//", StringComparison.Ordinal) ||
                _chatCompletionsPath.Contains('?') || _chatCompletionsPath.Contains('#'))
            {
                return "ChatCompletionsPath 必须为相对路径且不包含查询或片段";
            }

            var (_, headersError) = ParseExtraHeaders(_extraHeadersText);
            if (headersError != null)
            {
                return headersError;
            }
        }
        else if (Kind == TranslationProviderKind.Anthropic)
        {
            if (!string.IsNullOrWhiteSpace(_baseUrl))
            {
                if (!Uri.TryCreate(_baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    return "Anthropic BaseUrl 必须为合法的 http(s) 地址";
                }
            }

            if (!string.IsNullOrWhiteSpace(_effort))
            {
                var allowed = new[] { "low", "medium", "high", "xhigh", "max" };
                if (!allowed.Contains(_effort, StringComparer.Ordinal))
                {
                    return "Effort 必须为 low/medium/high/xhigh/max 或 null";
                }
            }

            if (_model.Contains("haiku", StringComparison.OrdinalIgnoreCase) && _effort != null)
            {
                return "Haiku 模型不支持 Effort 参数，请清空 Effort";
            }
        }

        if (_enabled && string.IsNullOrWhiteSpace(_model))
        {
            return "已启用的服务必须指定模型名称";
        }

        if (_credentialEditKind == CredentialEditKind.Replace && string.IsNullOrWhiteSpace(_replacementApiKey))
        {
            return "已选择替换 API Key，但尚未输入新 Key";
        }

        return null;
    }

    public string? ValidateForTest()
    {
        var saveErr = ValidateForSave();
        if (saveErr != null)
        {
            return saveErr;
        }

        if (string.IsNullOrWhiteSpace(_model))
        {
            return "测试连接必须指定模型名称";
        }

        return null;
    }

    public string? ValidateForListModels()
    {
        // 模型列表前置校验：不依赖 Model、Prompt、ChatCompletionsPath、MaxTokensField、Effort
        if (string.IsNullOrWhiteSpace(_displayName))
        {
            return "服务显示名称不能为空";
        }

        if (int.TryParse(_timeoutText, out var timeout) && timeout is >= 5 and <= 600)
        {
            // ok
        }
        else
        {
            return "超时时间必须在 5 到 600 秒之间";
        }

        if (Kind == TranslationProviderKind.OpenAICompatible)
        {
            if (string.IsNullOrWhiteSpace(_baseUrl))
            {
                return "OpenAI 兼容协议必须提供 BaseUrl";
            }

            if (!Uri.TryCreate(_baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return "BaseUrl 必须为合法的 http(s) 地址";
            }

            var (_, headersError) = ParseExtraHeaders(_extraHeadersText);
            if (headersError != null)
            {
                return headersError;
            }
        }
        else if (Kind == TranslationProviderKind.Anthropic)
        {
            if (!string.IsNullOrWhiteSpace(_baseUrl))
            {
                if (!Uri.TryCreate(_baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    return "Anthropic BaseUrl 必须为合法的 http(s) 地址";
                }
            }
        }

        if (_credentialEditKind == CredentialEditKind.Replace && string.IsNullOrWhiteSpace(_replacementApiKey))
        {
            return "已选择替换 API Key，但尚未输入新 Key";
        }

        return null;
    }

    public TranslationProviderProfile BuildTargetProfile(int order)
    {
        var normUrl = string.IsNullOrWhiteSpace(_baseUrl) ? null : _baseUrl.Trim();
        var normModel = _model?.Trim() ?? string.Empty;

        OpenAICompatibleOptions? openAiOpts = null;
        if (Kind == TranslationProviderKind.OpenAICompatible)
        {
            var (headers, _) = ParseExtraHeaders(_extraHeadersText);
            openAiOpts = new OpenAICompatibleOptions
            {
                Stream = _stream,
                IncludeStreamUsage = _includeStreamUsage,
                MaxTokensField = _maxTokensField,
                StripThinkTags = _stripThinkTags,
                ChatCompletionsPath = string.IsNullOrWhiteSpace(_chatCompletionsPath) ? "chat/completions" : _chatCompletionsPath.Trim(),
                ExtraHeaders = headers != null && headers.Count > 0 ? headers : null,
            };
        }
        else
        {
            openAiOpts = BaseProfile?.OpenAIOptions;
        }

        AnthropicOptions? anthropicOpts = null;
        if (Kind == TranslationProviderKind.Anthropic)
        {
            anthropicOpts = new AnthropicOptions
            {
                Effort = string.IsNullOrWhiteSpace(_effort) ? null : _effort.Trim(),
                RefusalFallback = _refusalFallback,
            };
        }
        else
        {
            anthropicOpts = BaseProfile?.AnthropicOptions;
        }

        return new TranslationProviderProfile
        {
            Id = Id,
            CredentialSetId = BaseProfile?.CredentialSetId,
            Kind = Kind,
            DisplayName = _displayName.Trim(),
            Enabled = _enabled,
            Order = order,
            BaseUrl = normUrl,
            Model = normModel,
            TimeoutSeconds = _timeoutSeconds,
            MaxOutputTokens = _maxOutputTokens,
            Temperature = Kind == TranslationProviderKind.Anthropic ? null : _temperature,
            PromptTemplateId = _promptTemplateId,
            OpenAIOptions = openAiOpts,
            AnthropicOptions = anthropicOpts,
        };
    }

    public TranslationProviderDraft BuildDraftForTest()
    {
        var target = BuildTargetProfile(BaseProfile?.Order ?? 0);
        var credEdit = BuildCredentialEdit();
        return new TranslationProviderDraft(target, BaseProfile, credEdit);
    }

    public TranslationProviderDraft BuildDraftForListModels()
    {
        var target = BuildTargetProfile(BaseProfile?.Order ?? 0);
        // 如果未填模型，给空字符串（Core 模型列表 guard 允许空模型）
        if (string.IsNullOrEmpty(target.Model))
        {
            target = target with { Model = string.Empty };
        }
        var credEdit = BuildCredentialEdit();
        return new TranslationProviderDraft(target, BaseProfile, credEdit);
    }

    public ProviderCredentialEdit BuildCredentialEdit()
    {
        return _credentialEditKind switch
        {
            CredentialEditKind.Replace => new ProviderCredentialEdit(
                CredentialEditKind.Replace,
                new ProviderCredentials(_replacementApiKey ?? string.Empty, null)),
            CredentialEditKind.Clear => new ProviderCredentialEdit(CredentialEditKind.Clear, null),
            _ => new ProviderCredentialEdit(CredentialEditKind.Keep, null),
        };
    }

    public void SyncBaseline(TranslationProviderProfile updated)
    {
        ArgumentNullException.ThrowIfNull(updated);
        BaseProfile = updated;
        _displayName = updated.DisplayName;
        _enabled = updated.Enabled;
        _baseUrl = updated.BaseUrl;
        _model = updated.Model;
        _timeoutSeconds = updated.TimeoutSeconds;
        _timeoutText = _timeoutSeconds.ToString(CultureInfo.InvariantCulture);
        _maxOutputTokens = updated.MaxOutputTokens;
        _maxOutputTokensText = _maxOutputTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _temperature = updated.Temperature;
        _temperatureText = _temperature?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _promptTemplateId = updated.PromptTemplateId;

        if (updated.OpenAIOptions != null)
        {
            _stream = updated.OpenAIOptions.Stream;
            _includeStreamUsage = updated.OpenAIOptions.IncludeStreamUsage;
            _maxTokensField = updated.OpenAIOptions.MaxTokensField;
            _stripThinkTags = updated.OpenAIOptions.StripThinkTags;
            _chatCompletionsPath = updated.OpenAIOptions.ChatCompletionsPath ?? "chat/completions";
            _extraHeadersText = updated.OpenAIOptions.ExtraHeaders != null
                ? string.Join(Environment.NewLine, updated.OpenAIOptions.ExtraHeaders.Select(kv => $"{kv.Key}: {kv.Value}"))
                : string.Empty;
        }

        if (updated.AnthropicOptions != null)
        {
            _effort = updated.AnthropicOptions.Effort;
            _refusalFallback = updated.AnthropicOptions.RefusalFallback;
        }

        _credentialEditKind = CredentialEditKind.Keep;
        _replacementApiKey = null;

        UpdateAnthropicWarnings();
        ClearTestResult();

        OnPropertyChanged(string.Empty);
    }

    public void ResetCredentialsToKeepAfterSave()
    {
        _credentialEditKind = CredentialEditKind.Keep;
        _replacementApiKey = null;
        OnPropertyChanged(nameof(CredentialEditKind));
        OnPropertyChanged(nameof(IsReplaceCredential));
        OnPropertyChanged(nameof(IsDirty));
    }

    public override string ToString()
    {
        return $"TranslationProfileDraft {{ Id = {Id}, Kind = {Kind}, DisplayName = {DisplayName} }}";
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
