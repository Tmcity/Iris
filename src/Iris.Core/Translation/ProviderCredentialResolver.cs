using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Iris.Core.Common;
using Iris.Core.Security;
using Iris.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Iris.Core.Translation;

internal readonly record struct CredentialResolutionResult(
    bool IsExplicitUnconfigured,
    ProviderCredentials? Credentials)
{
    public static CredentialResolutionResult ExplicitUnconfigured() => new(true, null);

    public static CredentialResolutionResult Success(ProviderCredentials credentials) => new(false, credentials);
}

/// <summary>
/// 纯凭据解析器（specs/PROVIDER_REGISTRY.md §2.1）。
/// 只读解析 Provider 的凭据集引用或旧格式凭据；不写盘、不迁移、不回退旧键，错误只记录安全分类。
/// </summary>
internal static partial class ProviderCredentialResolver
{
    [GeneratedRegex(@"\Acs-[0-9a-f]{32}\z")]
    private static partial Regex CredentialSetIdRegex();

    public static CredentialResolutionResult Resolve(
        TranslationProviderProfile profile,
        ISecretStore secretStore,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(secretStore);

        // 1. CredentialSetId == null: 兼容旧模式
        if (profile.CredentialSetId == null)
        {
            string? apiKey = null;
            string? secretKey = null;

            try
            {
                apiKey = secretStore.Get(SecretKeys.ApiKey(profile.Id));
            }
            catch (Exception ex)
            {
                if (logger != null) LogLegacyApiKeyReadFailed(logger, profile.Id, ex.GetType().Name);
            }

            try
            {
                secretKey = secretStore.Get(SecretKeys.SecretKey(profile.Id));
            }
            catch (Exception ex)
            {
                if (logger != null) LogLegacySecretKeyReadFailed(logger, profile.Id, ex.GetType().Name);
            }

            if (string.IsNullOrEmpty(apiKey) && string.IsNullOrEmpty(secretKey))
            {
                return CredentialResolutionResult.Success(ProviderCredentials.None);
            }

            return CredentialResolutionResult.Success(new ProviderCredentials(apiKey, secretKey));
        }

        // 2. CredentialSetId != null: 精确引用，必须满足格式要求且禁止回退
        var setId = profile.CredentialSetId;
        if (!CredentialSetIdRegex().IsMatch(setId))
        {
            if (logger != null) LogInvalidCredentialSetIdFormat(logger, profile.Id);
            return CredentialResolutionResult.ExplicitUnconfigured();
        }

        string? rawJson = null;
        try
        {
            rawJson = secretStore.Get(SecretKeys.CredentialSet(setId));
        }
        catch (Exception ex)
        {
            if (logger != null) LogCredentialSetReadFailed(logger, profile.Id, ex.GetType().Name);
            return CredentialResolutionResult.ExplicitUnconfigured();
        }

        if (string.IsNullOrEmpty(rawJson))
        {
            if (logger != null) LogCredentialSetNotFound(logger, profile.Id);
            return CredentialResolutionResult.ExplicitUnconfigured();
        }

        return ParseCredentialSetJson(rawJson, profile.Id, logger);
    }

    private static CredentialResolutionResult ParseCredentialSetJson(string rawJson, string profileId, ILogger? logger)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(rawJson);
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Disallow
            });

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                if (logger != null) LogInvalidJsonRoot(logger, profileId);
                return CredentialResolutionResult.ExplicitUnconfigured();
            }

            var seenProperties = new HashSet<string>(StringComparer.Ordinal);
            string? format = null;
            int? version = null;
            string? apiKey = null;
            string? secretKey = null;
            var hasApiKey = false;
            var hasSecretKey = false;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    if (logger != null) LogUnexpectedToken(logger, profileId);
                    return CredentialResolutionResult.ExplicitUnconfigured();
                }

                var propName = reader.GetString()!;
                if (!seenProperties.Add(propName))
                {
                    if (logger != null) LogDuplicateProperty(logger, profileId);
                    return CredentialResolutionResult.ExplicitUnconfigured();
                }

                if (!reader.Read())
                {
                    if (logger != null) LogTruncatedJson(logger, profileId);
                    return CredentialResolutionResult.ExplicitUnconfigured();
                }

                switch (propName)
                {
                    case "format":
                        if (reader.TokenType != JsonTokenType.String)
                        {
                            return CredentialResolutionResult.ExplicitUnconfigured();
                        }
                        format = reader.GetString();
                        break;

                    case "version":
                        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var v))
                        {
                            return CredentialResolutionResult.ExplicitUnconfigured();
                        }
                        version = v;
                        break;

                    case "apiKey":
                        if (reader.TokenType == JsonTokenType.String)
                        {
                            apiKey = reader.GetString();
                            hasApiKey = true;
                        }
                        else if (reader.TokenType == JsonTokenType.Null)
                        {
                            apiKey = null;
                            hasApiKey = true;
                        }
                        else
                        {
                            return CredentialResolutionResult.ExplicitUnconfigured();
                        }
                        break;

                    case "secretKey":
                        if (reader.TokenType == JsonTokenType.String)
                        {
                            secretKey = reader.GetString();
                            hasSecretKey = true;
                        }
                        else if (reader.TokenType == JsonTokenType.Null)
                        {
                            secretKey = null;
                            hasSecretKey = true;
                        }
                        else
                        {
                            return CredentialResolutionResult.ExplicitUnconfigured();
                        }
                        break;

                    default:
                        // 额外未知字段：检查是否有重复属性或结构异常，继续忽略合法未知字段
                        if (!ValidateSubtreeNoDuplicates(ref reader))
                        {
                            if (logger != null) LogDuplicateProperty(logger, profileId);
                            return CredentialResolutionResult.ExplicitUnconfigured();
                        }
                        break;
                }
            }

            // 检查是否有根对象之后的尾随多余数据
            if (reader.Read())
            {
                if (logger != null) LogTrailingTokens(logger, profileId);
                return CredentialResolutionResult.ExplicitUnconfigured();
            }

            if (format != "iris-provider-credentials" ||
                version != 1 ||
                !hasApiKey ||
                !hasSecretKey)
            {
                if (logger != null) LogInvalidSchemaOrMissingFields(logger, profileId);
                return CredentialResolutionResult.ExplicitUnconfigured();
            }

            if (apiKey == null && secretKey == null)
            {
                return CredentialResolutionResult.Success(ProviderCredentials.None);
            }

            return CredentialResolutionResult.Success(new ProviderCredentials(apiKey, secretKey));
        }
        catch (Exception ex)
        {
            if (logger != null) LogJsonParseFailed(logger, profileId, ex.GetType().Name);
            return CredentialResolutionResult.ExplicitUnconfigured();
        }
    }

    private static bool ValidateSubtreeNoDuplicates(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject && reader.TokenType != JsonTokenType.StartArray)
        {
            return true;
        }

        var stack = new Stack<HashSet<string>>();
        var nestingDepth = 1;

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            stack.Push(new HashSet<string>(StringComparer.Ordinal));
        }

        while (nestingDepth > 0 && reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    nestingDepth++;
                    stack.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;

                case JsonTokenType.EndObject:
                    nestingDepth--;
                    if (stack.Count > 0)
                    {
                        stack.Pop();
                    }
                    break;

                case JsonTokenType.StartArray:
                    nestingDepth++;
                    break;

                case JsonTokenType.EndArray:
                    nestingDepth--;
                    break;

                case JsonTokenType.PropertyName:
                    if (stack.Count == 0)
                    {
                        return false;
                    }
                    var propName = reader.GetString()!;
                    if (!stack.Peek().Add(propName))
                    {
                        return false;
                    }
                    break;
            }
        }

        return nestingDepth == 0;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to read legacy apiKey for profile {ProfileId}: {ExceptionType}")]
    private static partial void LogLegacyApiKeyReadFailed(ILogger logger, string profileId, string exceptionType);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Failed to read legacy secretKey for profile {ProfileId}: {ExceptionType}")]
    private static partial void LogLegacySecretKeyReadFailed(ILogger logger, string profileId, string exceptionType);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Invalid credentialSetId format for profile {ProfileId}")]
    private static partial void LogInvalidCredentialSetIdFormat(ILogger logger, string profileId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Failed to read credential set for profile {ProfileId}: {ExceptionType}")]
    private static partial void LogCredentialSetReadFailed(ILogger logger, string profileId, string exceptionType);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Credential set not found for profile {ProfileId}")]
    private static partial void LogCredentialSetNotFound(ILogger logger, string profileId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Invalid credential set JSON root for profile {ProfileId}")]
    private static partial void LogInvalidJsonRoot(ILogger logger, string profileId);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Unexpected token in credential set JSON for profile {ProfileId}")]
    private static partial void LogUnexpectedToken(ILogger logger, string profileId);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "Duplicate property in credential set JSON for profile {ProfileId}")]
    private static partial void LogDuplicateProperty(ILogger logger, string profileId);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "Truncated credential set JSON for profile {ProfileId}")]
    private static partial void LogTruncatedJson(ILogger logger, string profileId);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "Trailing tokens in credential set JSON for profile {ProfileId}")]
    private static partial void LogTrailingTokens(ILogger logger, string profileId);

    [LoggerMessage(EventId = 11, Level = LogLevel.Warning, Message = "Invalid credential set schema or missing required fields for profile {ProfileId}")]
    private static partial void LogInvalidSchemaOrMissingFields(ILogger logger, string profileId);

    [LoggerMessage(EventId = 12, Level = LogLevel.Warning, Message = "Failed to parse credential set JSON for profile {ProfileId}: {ExceptionType}")]
    private static partial void LogJsonParseFailed(ILogger logger, string profileId, string exceptionType);
}
