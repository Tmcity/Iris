using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Errors;
using Iris.Core.IO;
using Iris.Core.Serialization;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Microsoft.Extensions.Logging;

namespace Iris.Infrastructure.Settings;

/// <summary>
/// 基于单个 JSON 文件的设置服务实现。
/// </summary>
internal sealed partial class JsonSettingsService : ISettingsService, IDisposable
{
    private readonly IAppPaths _paths;
    private readonly SettingsMigrationRunner _migrationRunner;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<JsonSettingsService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private AppSettings _current;
    private string _currentJson = string.Empty;
    private int _isLoaded;

    public JsonSettingsService(
        IAppPaths paths,
        IEnumerable<ISettingsMigration> migrations,
        TimeProvider timeProvider,
        ILogger<JsonSettingsService> logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _migrationRunner = new SettingsMigrationRunner(migrations ?? throw new ArgumentNullException(nameof(migrations)));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _current = new AppSettings();
    }

    public AppSettings Current => Volatile.Read(ref _current);

    public event EventHandler<SettingsChangedEventArgs>? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _isLoaded) == 1)
            {
                throw new InvalidOperationException("LoadAsync 仅允许调用一次");
            }

            await LoadInternalAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        cancellationToken.ThrowIfCancellationRequested();

        if (Volatile.Read(ref _isLoaded) == 0)
        {
            throw new InvalidOperationException("必须先调用 LoadAsync 后才能调用 UpdateAsync");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var oldSettings = _current;
            var mutated = mutate(oldSettings);

            if (mutated is null)
            {
                throw new InvalidOperationException("mutate 委托返回了 null");
            }

            if (ReferenceEquals(oldSettings, mutated))
            {
                return;
            }

            var fixes = new List<string>();
            var normalized = SettingsNormalizer.Normalize(mutated, fixes);
            if (fixes.Count > 0 && _logger.IsEnabled(LogLevel.Debug))
            {
                var fixSummary = string.Join("; ", fixes);
                LogUpdateNormalizationFixes(_logger, fixSummary);
            }

            var newJson = JsonSerializer.Serialize(normalized, IrisJson.Options) + "\n";
            if (string.Equals(newJson, _currentJson, StringComparison.Ordinal))
            {
                return;
            }

            var settingsPath = _paths.SettingsFile;
            var backupPath = settingsPath + ".bak";
            await AtomicFile.WriteAllTextAsync(settingsPath, newJson, backupPath, cancellationToken).ConfigureAwait(false);

            Volatile.Write(ref _current, normalized);
            _currentJson = newJson;

            // 仍在锁内同步触发 Changed，逐个处理器单独捕获异常
            var handler = Changed;
            if (handler != null)
            {
                var args = new SettingsChangedEventArgs(oldSettings, normalized);
                foreach (var invocation in handler.GetInvocationList())
                {
                    try
                    {
                        ((EventHandler<SettingsChangedEventArgs>)invocation)(this, args);
                    }
                    catch (Exception ex)
                    {
                        LogChangedHandlerFailed(_logger, ex);
                    }
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    private async Task LoadInternalAsync(CancellationToken cancellationToken)
    {
        var settingsPath = _paths.SettingsFile;

        if (!File.Exists(settingsPath))
        {
            var defaults = new AppSettings();
            var defaultJson = JsonSerializer.Serialize(defaults, IrisJson.Options) + "\n";
            await AtomicFile.WriteAllTextAsync(settingsPath, defaultJson, null, cancellationToken).ConfigureAwait(false);

            Volatile.Write(ref _current, defaults);
            _currentJson = defaultJson;
            Volatile.Write(ref _isLoaded, 1);
            LogFileCreated(_logger, settingsPath);
            return;
        }

        string text;
        try
        {
            var bytes = await AtomicFile.ReadAllBytesAsync(settingsPath, cancellationToken).ConfigureAwait(false);
            using var stream = new MemoryStream(bytes);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IrisException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new IrisException(ErrorCode.StorageFailure, ErrorMessages.GetDefault(ErrorCode.StorageFailure), ex, detail: settingsPath);
        }

        JsonObject root;
        try
        {
            var parsedNode = JsonNode.Parse(
                text,
                new JsonNodeOptions { PropertyNameCaseInsensitive = true },
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

            if (parsedNode is not JsonObject parsedRoot)
            {
                LogRootNotObject(_logger);
                await HandleCorruptionAsync(settingsPath, cancellationToken).ConfigureAwait(false);
                return;
            }

            EnsureFullyExpanded(parsedRoot);
            root = parsedRoot;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            LogJsonSyntaxCorrupted(_logger, ex);
            await HandleCorruptionAsync(settingsPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        var dir = Path.GetDirectoryName(settingsPath) ?? string.Empty;
        var fixes = new List<string>();
        int fileSchemaVersion;

        var schemaNode = root["schemaVersion"];
        if (schemaNode is null)
        {
            fileSchemaVersion = AppSettings.CurrentSchemaVersion;
        }
        else if (schemaNode is JsonValue jv && jv.TryGetValue<int>(out var v))
        {
            fileSchemaVersion = v;
        }
        else
        {
            fixes.Add(FormattableString.Invariant($"schemaVersion: 非整数 → {AppSettings.CurrentSchemaVersion}"));
            fileSchemaVersion = AppSettings.CurrentSchemaVersion;
        }

        var shouldNotWriteBack = false;
        var hasMigrated = false;

        if (fileSchemaVersion > AppSettings.CurrentSchemaVersion)
        {
            var targetVersionPath = Path.Combine(dir, FormattableString.Invariant($"settings.v{fileSchemaVersion}.json"));
            if (!File.Exists(targetVersionPath))
            {
                await AtomicFile.CopyAsync(settingsPath, targetVersionPath, overwrite: false, cancellationToken).ConfigureAwait(false);
            }

            LogHigherVersion(_logger, fileSchemaVersion, AppSettings.CurrentSchemaVersion);
            shouldNotWriteBack = true;
        }
        else if (fileSchemaVersion < AppSettings.CurrentSchemaVersion)
        {
            if (_migrationRunner.CanMigrate(fileSchemaVersion, AppSettings.CurrentSchemaVersion))
            {
                var targetBakPath = Path.Combine(dir, FormattableString.Invariant($"settings.v{fileSchemaVersion}.bak.json"));
                await AtomicFile.CopyAsync(settingsPath, targetBakPath, overwrite: true, cancellationToken).ConfigureAwait(false);

                root = _migrationRunner.Run(root, fileSchemaVersion, AppSettings.CurrentSchemaVersion);
                hasMigrated = true;
            }
            else
            {
                fixes.Add(FormattableString.Invariant($"schemaVersion: {fileSchemaVersion} → {AppSettings.CurrentSchemaVersion}（迁移链不完整，已跳过迁移）"));
                LogIncompleteMigrationChain(_logger, fileSchemaVersion, AppSettings.CurrentSchemaVersion);
            }
        }

        // 挽救第 0 步：无条件更新 schemaVersion 为当前数字版本
        root["schemaVersion"] = AppSettings.CurrentSchemaVersion;

        // 挽救第 1-4 步
        SettingsSalvager.Salvage(root, fixes);

        AppSettings deserialized;
        try
        {
            deserialized = JsonSerializer.Deserialize<AppSettings>(root.ToJsonString(), IrisJson.Options)
                           ?? throw new JsonException("反序列化结果为空");
        }
        catch (Exception ex)
        {
            LogSalvageDeserializationFailed(_logger, ex);
            await HandleCorruptionAsync(settingsPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        // 强类型规范化 N1–N14
        var normalized = SettingsNormalizer.Normalize(deserialized, fixes);
        var formattedJson = JsonSerializer.Serialize(normalized, IrisJson.Options) + "\n";

        if ((hasMigrated || fixes.Count > 0) && !shouldNotWriteBack)
        {
            var backupFile = settingsPath + ".bak";
            await AtomicFile.WriteAllTextAsync(settingsPath, formattedJson, backupFile, cancellationToken).ConfigureAwait(false);
            _currentJson = formattedJson;

            if (fixes.Count > 0 && _logger.IsEnabled(LogLevel.Warning))
            {
                var displayed = fixes.Take(50);
                var summary = string.Join("; ", displayed);
                if (fixes.Count > 50)
                {
                    summary += FormattableString.Invariant($"; ……另有 {fixes.Count - 50} 条");
                }

                LogRepairsApplied(_logger, fixes.Count, summary);
            }
        }
        else
        {
            _currentJson = formattedJson;
        }

        Volatile.Write(ref _current, normalized);
        Volatile.Write(ref _isLoaded, 1);
        LogLoadSuccess(_logger, settingsPath, _current.SchemaVersion, fixes.Count);
    }

    private static void EnsureFullyExpanded(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (KeyValuePair<string, JsonNode?> pair in obj)
            {
                if (pair.Value is not null)
                {
                    EnsureFullyExpanded(pair.Value);
                }
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (JsonNode? item in arr)
            {
                if (item is not null)
                {
                    EnsureFullyExpanded(item);
                }
            }
        }
    }

    private async Task HandleCorruptionAsync(string settingsPath, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(settingsPath) ?? string.Empty;
        var now = _timeProvider.GetLocalNow();
        var timestamp = now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var backupPath = Path.Combine(dir, $"settings.corrupt-{timestamp}.json");

        if (File.Exists(backupPath))
        {
            for (var counter = 1; ; counter++)
            {
                var candidate = Path.Combine(dir, FormattableString.Invariant($"settings.corrupt-{timestamp}-{counter}.json"));
                if (!File.Exists(candidate))
                {
                    backupPath = candidate;
                    break;
                }
            }
        }

        if (File.Exists(settingsPath))
        {
            await AtomicFile.MoveAsync(settingsPath, backupPath, overwrite: false, cancellationToken).ConfigureAwait(false);
        }

        var defaultSettings = new AppSettings();
        Volatile.Write(ref _current, defaultSettings);
        Volatile.Write(ref _isLoaded, 1);

        try
        {
            var defaultJson = JsonSerializer.Serialize(defaultSettings, IrisJson.Options) + "\n";
            await AtomicFile.WriteAllTextAsync(settingsPath, defaultJson, null, cancellationToken).ConfigureAwait(false);
            _currentJson = defaultJson;
        }
        catch (Exception ex)
        {
            LogCorruptionWriteFailed(_logger, ex, settingsPath);
        }

        throw new IrisException(ErrorCode.SettingsCorrupted, ErrorMessages.GetDefault(ErrorCode.SettingsCorrupted), detail: backupPath);
    }


    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "设置文件不存在，已初始化为默认设置并写入 {path}")]
    private static partial void LogFileCreated(ILogger logger, string path);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "设置文件版本 ({fileVersion}) 高于当前程序支持的版本 ({currentVersion})，将尽力加载且不回写")]
    private static partial void LogHigherVersion(ILogger logger, int fileVersion, int currentVersion);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "设置加载完成，已自动修复 {count} 项：{summary}")]
    private static partial void LogRepairsApplied(ILogger logger, int count, string summary);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "设置加载成功: {path}, SchemaVersion={schemaVersion}, 修复条数={fixCount}")]
    private static partial void LogLoadSuccess(ILogger logger, string path, int schemaVersion, int fixCount);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "设置文件 JSON 语法损坏")]
    private static partial void LogJsonSyntaxCorrupted(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "设置文件根节点不是 JSON 对象（文件为空或为数组）")]
    private static partial void LogRootNotObject(ILogger logger);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "设置挽救后反序列化仍失败，执行损坏恢复")]
    private static partial void LogSalvageDeserializationFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 8, Level = LogLevel.Error, Message = "损坏恢复写入默认设置文件失败: {path}")]
    private static partial void LogCorruptionWriteFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(EventId = 9, Level = LogLevel.Debug, Message = "UpdateAsync 规范化修复: {fixes}")]
    private static partial void LogUpdateNormalizationFixes(ILogger logger, string fixes);

    [LoggerMessage(EventId = 10, Level = LogLevel.Error, Message = "Settings Changed 事件处理器执行失败")]
    private static partial void LogChangedHandlerFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 11, Level = LogLevel.Warning, Message = "设置文件版本 ({fileVersion}) 到 ({currentVersion}) 的迁移链不完整，已跳过迁移")]
    private static partial void LogIncompleteMigrationChain(ILogger logger, int fileVersion, int currentVersion);
}
