using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Iris.Core.Errors;
using Iris.Core.IO;
using Iris.Core.Security;
using Iris.Core.Storage;
using Microsoft.Extensions.Logging;

namespace Iris.Platform.Windows.Security;

internal sealed partial class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = "Iris.Secrets.v1"u8.ToArray();
    private const int MaxRetries = 5;
    private const int RetryDelayMs = 50;

    private readonly IAppPaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DpapiSecretStore> _logger;
    private readonly Lock _gate = new();

    private Dictionary<string, byte[]> _entries;
    private bool _degraded;
    private readonly HashSet<string> _decryptWarned = new(StringComparer.Ordinal);

    public event EventHandler<string>? Changed;

    public DpapiSecretStore(IAppPaths paths, TimeProvider timeProvider, ILogger<DpapiSecretStore> logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        LoadInitial();
    }

    public string? Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        byte[]? cipher = null;
        lock (_gate)
        {
            if (_degraded)
            {
                return null;
            }

            if (!_entries.TryGetValue(key, out cipher))
            {
                return null;
            }
        }

        try
        {
            var plainBytes = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException ex)
        {
            lock (_gate)
            {
                if (_decryptWarned.Add(key))
                {
                    LogDecryptionFailed(_logger, key, ex);
                }
            }

            return null;
        }
    }

    public void Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length == 0)
        {
            Remove(key);
            return;
        }

        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        Delegate[]? invocations = null;

        lock (_gate)
        {
            if (_degraded)
            {
                ReloadUnderLock();
                if (_degraded)
                {
                    throw new IrisException(
                        ErrorCode.SecretUnavailable,
                        ErrorMessages.GetDefault(ErrorCode.SecretUnavailable),
                        detail: _paths.SecretsFile);
                }
            }

            var nextEntries = new Dictionary<string, byte[]>(_entries, StringComparer.Ordinal)
            {
                [key] = cipher,
            };

            var bytes = SecretsFileCodec.Serialize(nextEntries);
            AtomicFile.WriteAllBytes(_paths.SecretsFile, bytes, backupPath: null);

            _entries = nextEntries;
            _decryptWarned.Remove(key);
            invocations = Changed?.GetInvocationList();
        }

        if (invocations != null)
        {
            FireChanged(invocations, key);
        }
    }

    public bool Remove(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        Delegate[]? invocations = null;

        lock (_gate)
        {
            if (_degraded)
            {
                ReloadUnderLock();
                if (_degraded)
                {
                    throw new IrisException(
                        ErrorCode.SecretUnavailable,
                        ErrorMessages.GetDefault(ErrorCode.SecretUnavailable),
                        detail: _paths.SecretsFile);
                }
            }

            if (!_entries.ContainsKey(key))
            {
                return false;
            }

            var nextEntries = new Dictionary<string, byte[]>(_entries, StringComparer.Ordinal);
            nextEntries.Remove(key);

            var bytes = SecretsFileCodec.Serialize(nextEntries);
            AtomicFile.WriteAllBytes(_paths.SecretsFile, bytes, backupPath: null);

            _entries = nextEntries;
            _decryptWarned.Remove(key);
            invocations = Changed?.GetInvocationList();
        }

        if (invocations != null)
        {
            FireChanged(invocations, key);
        }

        return true;
    }

    private void LoadInitial()
    {
        var secretsPath = _paths.SecretsFile;
        if (!File.Exists(secretsPath))
        {
            _entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            _degraded = false;
            return;
        }

        byte[] bytes;
        try
        {
            bytes = ReadFileWithRetry(secretsPath);
        }
        catch (Exception ex)
        {
            _entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            _degraded = true;
            LogReadFailed(_logger, secretsPath, ex);
            return;
        }

        var parseResult = SecretsFileCodec.Parse(bytes);
        if (parseResult.IsCorrupted)
        {
            LogSecretsFileCorrupted(_logger, secretsPath);
            var backupPath = GetCorruptBackupPath(secretsPath);
            try
            {
                AtomicFile.Move(secretsPath, backupPath, overwrite: false);
                _entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                _degraded = false;
                LogCorruptBackupCreated(_logger, backupPath);
            }
            catch (Exception ex)
            {
                _entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                _degraded = true;
                LogCorruptBackupFailed(_logger, backupPath, ex);
            }

            return;
        }

        _entries = new Dictionary<string, byte[]>(parseResult.Entries, StringComparer.Ordinal);
        _degraded = false;

        foreach (var droppedKey in parseResult.DroppedKeys)
        {
            LogEntryDropped(_logger, droppedKey);
        }
    }

    private void ReloadUnderLock()
    {
        var secretsPath = _paths.SecretsFile;
        if (!File.Exists(secretsPath))
        {
            _entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            _degraded = false;
            return;
        }

        byte[] bytes;
        try
        {
            bytes = ReadFileWithRetry(secretsPath);
        }
        catch (Exception ex)
        {
            _degraded = true;
            LogReadFailed(_logger, secretsPath, ex);
            return;
        }

        var parseResult = SecretsFileCodec.Parse(bytes);
        if (parseResult.IsCorrupted)
        {
            LogSecretsFileCorrupted(_logger, secretsPath);
            var backupPath = GetCorruptBackupPath(secretsPath);
            try
            {
                AtomicFile.Move(secretsPath, backupPath, overwrite: false);
                _entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                _degraded = false;
                LogCorruptBackupCreated(_logger, backupPath);
            }
            catch (Exception ex)
            {
                _degraded = true;
                LogCorruptBackupFailed(_logger, backupPath, ex);
            }

            return;
        }

        _entries = new Dictionary<string, byte[]>(parseResult.Entries, StringComparer.Ordinal);
        _degraded = false;

        foreach (var droppedKey in parseResult.DroppedKeys)
        {
            LogEntryDropped(_logger, droppedKey);
        }
    }

    private string GetCorruptBackupPath(string secretsFile)
    {
        var dir = Path.GetDirectoryName(secretsFile);
        if (string.IsNullOrEmpty(dir))
        {
            dir = ".";
        }

        var timestamp = _timeProvider.GetLocalNow().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var candidate = Path.Combine(dir, FormattableString.Invariant($"secrets.corrupt-{timestamp}.dat"));
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var counter = 1;
        while (true)
        {
            candidate = Path.Combine(dir, FormattableString.Invariant($"secrets.corrupt-{timestamp}-{counter}.dat"));
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            counter++;
        }
    }

    private void FireChanged(Delegate[] invocations, string key)
    {
        foreach (var invocation in invocations)
        {
            try
            {
                ((EventHandler<string>)invocation)(this, key);
            }
            catch (Exception ex)
            {
                LogChangedHandlerFailed(_logger, key, ex);
            }
        }
    }

    private static byte[] ReadFileWithRetry(string path)
    {
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                return File.ReadAllBytes(path);
            }
            catch (Exception ex) when (attempt < MaxRetries && IsTransient(ex))
            {
                Thread.Sleep(RetryDelayMs);
            }
        }

        return File.ReadAllBytes(path);
    }

    private static bool IsTransient(Exception ex)
    {
        if (ex is UnauthorizedAccessException)
        {
            return true;
        }

        if (ex is IOException io)
        {
            return io.HResult == unchecked((int)0x80070020) || io.HResult == unchecked((int)0x80070021);
        }

        return false;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "读取 secrets.dat 失败，进入只读降级模式: {Path}")]
    private static partial void LogReadFailed(ILogger logger, string path, Exception ex);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "secrets.dat 格式损坏: {Path}")]
    private static partial void LogSecretsFileCorrupted(ILogger logger, string path);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "损坏的 secrets.dat 已移动备份至: {BackupPath}")]
    private static partial void LogCorruptBackupCreated(ILogger logger, string backupPath);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "备份损坏的 secrets.dat 失败: {BackupPath}")]
    private static partial void LogCorruptBackupFailed(ILogger logger, string backupPath, Exception ex);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "secrets.dat 中条目已丢弃（值非字符串或 Base64 格式错误）: {Key}")]
    private static partial void LogEntryDropped(ILogger logger, string key);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "解密密钥失败: {Key}")]
    private static partial void LogDecryptionFailed(ILogger logger, string key, Exception ex);

    [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "密钥变更事件处理器失败: {Key}")]
    private static partial void LogChangedHandlerFailed(ILogger logger, string key, Exception ex);
}
