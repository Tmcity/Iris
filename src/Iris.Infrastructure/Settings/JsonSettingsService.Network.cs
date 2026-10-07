using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Iris.Core.Errors;
using Iris.Core.IO;
using Iris.Core.Network;
using Iris.Core.Security;
using Iris.Core.Settings;

namespace Iris.Infrastructure.Settings;

internal sealed partial class JsonSettingsService
{
    private readonly Func<ISecretStore>? _secretStoreFactory;
    private byte[]? _diskFingerprint;
    private string _safeDiskJson = "";
    private JsonObject? _safeRoot;
    private int _readOnly;
    public bool IsReadOnly => Volatile.Read(ref _readOnly) != 0;

    // Schema detection precedes expansion/salvage, including integers outside the model's Int32 range.
    private static bool IsFutureSchema(string text)
    {
        using var doc = JsonDocument.Parse(text, new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
        var versions = doc.RootElement.EnumerateObject().Where(p => p.Name.Equals("schemaVersion", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (versions.Length == 0) return false;
        if (versions.Length != 1 || versions[0].Value.ValueKind != JsonValueKind.Number
            || !BigInteger.TryParse(versions[0].Value.GetRawText(), System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out var version)) throw new JsonException();
        return version > AppSettings.CurrentSchemaVersion;
    }

    private void PublishReadOnly(string text)
    {
        AppSettings preview = new();
        try
        {
            var root = JsonNode.Parse(text, new() { PropertyNameCaseInsensitive = true }, new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject();
            root["schemaVersion"] = AppSettings.CurrentSchemaVersion;
            root["network"] = new JsonObject();
            var fixes = new List<string>();
            SettingsSalvager.Salvage(root, fixes);
            preview = SettingsNormalizer.Normalize(JsonSerializer.Deserialize<AppSettings>(root, Core.Serialization.IrisJson.Options) ?? new(), fixes);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        { /* An unknown future format permits only a default, non-networked preview. Never salvage its file. */ }
        Volatile.Write(ref _readOnly, 1);
        Volatile.Write(ref _current, SettingsSnapshot.Create(preview));
        Volatile.Write(ref _isLoaded, 1);
    }

    private async Task ImportProxyAsync(JsonObject root, CancellationToken cancellationToken)
    {
        if (root["network"] is not JsonObject network || network["customProxyUrl"] is not JsonValue urlNode
            || !urlNode.TryGetValue<string>(out var url) || string.IsNullOrEmpty(url)) return;
        // Even malformed legacy addresses may contain plaintext userinfo. Never let salvage back them up.
        bool userInfo = NetworkProxyCodec.HasUserInfo(url);
        // An explicit reference is authoritative. Contradictory plaintext cannot be resolved by guessing.
        if (network["proxyCredentialSetId"] != null)
        {
            if (userInfo) throw NetworkProxyCodec.StorageFailure();
            return;
        }
        Uri uri;
        try { uri = NetworkProxyCodec.ParseUri(url, allowUserInfo: true); }
        catch (IrisException)
        {
            if (userInfo) throw NetworkProxyCodec.StorageFailure();
            return; // Preserve invalid Custom intent; sending will fail before Sockets.
        }
        string? stagedId = null;
        bool backupPublished = false;
        ISecretStore? secrets = null;
        try
        {
            string? importedFixedValue = null;
            ProxyCredentials? credentials;
            if (userInfo)
            {
                credentials = NetworkProxyCodec.ReadUserInfo(url);
                secrets = await Task.Run(() => _secretStoreFactory?.Invoke() ?? throw NetworkProxyCodec.StorageFailure(), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (_secretStoreFactory == null) return;
                secrets = await Task.Run(_secretStoreFactory, cancellationToken).ConfigureAwait(false);
                importedFixedValue = await Task.Run(() => secrets.Get(SecretKeys.LegacyProxyCredentials), cancellationToken).ConfigureAwait(false);
                if (importedFixedValue == null) return;
                // Unknown or differently bound fixed data remains untouched and never becomes an implicit credential.
                try { credentials = NetworkProxyCodec.Deserialize(importedFixedValue, uri); }
                catch (IrisException) { throw NetworkProxyCodec.StorageFailure(); }
            }
            stagedId = "pc-" + Guid.NewGuid().ToString("N");
            var value = NetworkProxyCodec.Serialize(uri, credentials);
            await Task.Run(() => secrets.Set(SecretKeys.ProxyCredentialSet(stagedId), value), cancellationToken).ConfigureAwait(false);
            network["customProxyUrl"] = uri.AbsoluteUri;
            network["proxyCredentialSetId"] = stagedId;
            string safeJson = root.ToJsonString(Core.Serialization.IrisJson.Options) + "\n";
            await VerifyDiskAsync(cancellationToken).ConfigureAwait(false);
            await AtomicFile.WriteAllTextAsync(_paths.SettingsFile + ".bak", safeJson, null, cancellationToken).ConfigureAwait(false);
            backupPublished = true;
            await ReplaceCurrentAsync(safeJson, cancellationToken).ConfigureAwait(false);
            _safeDiskJson = safeJson;
            if (importedFixedValue != null)
            {
                try
                {
                    await Task.Run(() =>
                    {
                        if (secrets.Get(SecretKeys.LegacyProxyCredentials) == importedFixedValue)
                            secrets.Remove(SecretKeys.LegacyProxyCredentials);
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) { LogProxyCleanupFailed(_logger, ex.GetType().Name); }
            }
        }
        catch (Exception ex)
        {
            if (stagedId != null && !backupPublished && secrets != null)
            {
                try { await Task.Run(() => secrets.Remove(SecretKeys.ProxyCredentialSet(stagedId)), CancellationToken.None).ConfigureAwait(false); }
                catch (Exception cleanup) { LogProxyCleanupFailed(_logger, cleanup.GetType().Name); }
            }
            if (ex is OperationCanceledException) throw;
            throw NetworkProxyCodec.StorageFailure();
        }
    }

    private async Task VerifyDiskAsync(CancellationToken cancellationToken)
    {
        try
        {
            var actual = File.Exists(_paths.SettingsFile)
                ? SHA256.HashData(await File.ReadAllBytesAsync(_paths.SettingsFile, cancellationToken).ConfigureAwait(false)) : null;
            if (actual == null ? _diskFingerprint != null : _diskFingerprint == null || !actual.AsSpan().SequenceEqual(_diskFingerprint))
                throw NetworkProxyCodec.StorageFailure();
        }
        catch (OperationCanceledException) { throw; }
        catch { throw NetworkProxyCodec.StorageFailure(); }
    }

    private async Task ReplaceCurrentAsync(string json, CancellationToken cancellationToken)
    {
        // Private same-directory temp; never copy the potentially externally edited original into a backup.
        var temp = _paths.SettingsFile + ".tmp";
        try
        {
            await VerifyDiskAsync(cancellationToken).ConfigureAwait(false);
            var bytes = Encoding.UTF8.GetBytes(json);
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                await Task.Run(() => stream.Flush(true), CancellationToken.None).ConfigureAwait(false);
            }
            // The final fingerprint check detects cooperative external changes; it is not an external-process CAS.
            await VerifyDiskAsync(cancellationToken).ConfigureAwait(false);
            File.Move(temp, _paths.SettingsFile, overwrite: true);
            _diskFingerprint = SHA256.HashData(bytes);
        }
        catch (OperationCanceledException) { throw; }
        catch { throw NetworkProxyCodec.StorageFailure(); }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) { LogProxyCleanupFailed(_logger, ex.GetType().Name); }
        }
    }

    private async Task WriteSafeSettingsAsync(string json, string backup, CancellationToken cancellationToken)
    {
        try
        {
            await VerifyDiskAsync(cancellationToken).ConfigureAwait(false);
            await AtomicFile.WriteAllTextAsync(backup, _safeDiskJson, null, cancellationToken).ConfigureAwait(false);
            await ReplaceCurrentAsync(json, cancellationToken).ConfigureAwait(false);
            _safeDiskJson = json;
        }
        catch (OperationCanceledException) { throw; }
        catch { throw NetworkProxyCodec.StorageFailure(); }
    }

    private static JsonObject MergeKnown(JsonObject? original, JsonObject known)
    {
        var merged = original?.DeepClone().AsObject() ?? new JsonObject();
        foreach (var field in known)
            merged[field.Key] = field.Value is JsonObject child ? MergeKnown(merged[field.Key] as JsonObject, child) : field.Value?.DeepClone();
        return merged;
    }

    [Microsoft.Extensions.Logging.LoggerMessage(EventId = 12, Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Proxy settings cleanup incomplete: {ExceptionType}")]
    private static partial void LogProxyCleanupFailed(Microsoft.Extensions.Logging.ILogger logger, string exceptionType);
}
