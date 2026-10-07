using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Iris.Core.Errors;
using Iris.Core.IO;
using Iris.Core.Settings;

// Provisional prototype DTOs, deliberately outside Iris production assemblies/contracts.
internal sealed record ProxyConfig(ProxyMode ProxyMode, string? CustomProxyUrl, bool BypassProxyOnLocal, string? ProxyCredentialSetId = null)
{
    public override string ToString() => "ProxyConfig";
}
internal sealed record ProxySecret(string? UserName, string? Password)
{
    public override string ToString() => "ProxySecret";
}
internal sealed record SecretEnvelope(int Version, string? ProxyUri, string? UserName, string? Password)
{
    public override string ToString() => "SecretEnvelope";
}
internal sealed record CredentialChange(string? BaseProxyUrl, string? BaseCredentialSetId, CredentialEditKind Kind, ProxySecret? Replacement = null)
{
    public override string ToString() => "CredentialChange";
}
internal sealed record NetworkPatch(SettingsFieldEdit<ProxyMode>? Mode = null, SettingsFieldEdit<string?>? Url = null,
    SettingsFieldEdit<bool>? Bypass = null, CredentialChange? Credentials = null)
{
    public override string ToString() => "NetworkPatch";
}
internal static class ProxyFormat
{
    internal const string LegacyKey = "network/proxy-credentials";
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    internal static string Key(string id) => "network/proxy-credentials/" + id;
    internal static string Id() => "pc-" + Guid.NewGuid().ToString("N");
    internal static bool ValidId(string? id) => id is { Length: 35 } && id.StartsWith("pc-", StringComparison.Ordinal) && id.AsSpan(3).ContainsAnyExcept("0123456789abcdef".AsSpan()) == false;
    internal static string? Clean(string? value, bool allowUserinfo = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "socks5")
            || string.IsNullOrEmpty(uri.Host) || uri.AbsolutePath is not ("" or "/") || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !allowUserinfo && uri.UserInfo.Length != 0) throw new ArgumentException("Invalid proxy endpoint.");
        var clean = new UriBuilder(uri) { UserName = "", Password = "", Path = "", Query = "", Fragment = "" };
        return clean.Uri.AbsoluteUri;
    }
    internal static string Serialize(string? uri, ProxySecret? secret) => JsonSerializer.Serialize(new SecretEnvelope(1, uri, secret?.UserName, secret?.Password), Json);
    internal static ProxySecret? Resolve(ProxyConfig config, MemorySecrets secrets)
    {
        if (config.ProxyCredentialSetId == null) return null; // Legacy is imported once during load, never a runtime fallback.
        if (!ValidId(config.ProxyCredentialSetId)) throw new IOException("Invalid credential reference.");
        var raw = secrets.Get(Key(config.ProxyCredentialSetId)) ?? throw new IOException("Unavailable proxy credentials.");
        var envelope = JsonSerializer.Deserialize<SecretEnvelope>(raw, Json) ?? throw new IOException("Invalid proxy credentials.");
        if (envelope.Version != 1 || envelope.ProxyUri != Clean(config.CustomProxyUrl) || (envelope.UserName == null) != (envelope.Password == null)) throw new IOException("Credential endpoint mismatch.");
        return envelope.UserName == null ? null : new(envelope.UserName, envelope.Password);
    }
}
internal sealed class ProposalStore(string path)
{
    internal string PathName => path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    internal ProxyConfig Current { get; private set; } = new(ProxyMode.None, null, true);
    internal bool IsReadOnly = false;
    internal bool FailWrite;
    internal Action? BeforeMutate;
    internal int Writes;
    internal async Task InitializeAsync(ProxyConfig value)
    {
        Current = value; await WriteAsync(value, null);
    }
    internal static JsonObject Document(ProxyConfig value) => new()
    { ["schemaVersion"] = AppSettings.CurrentSchemaVersion, ["general"] = new JsonObject { ["untouched"] = "preserved" }, ["network"] = JsonSerializer.SerializeToNode(value, ProxyFormat.Json) };
    internal async Task UpdateAsync(Func<ProxyConfig, ProxyConfig> mutate)
    {
        await _gate.WaitAsync();
        try
        {
            if (IsReadOnly) throw new IOException("Future configuration is read-only.");
            BeforeMutate?.Invoke(); var next = mutate(Current);
            if (next == Current) return;
            if (FailWrite) throw new IOException("Synthetic settings failure.");
            await WriteAsync(next, path + ".bak"); Current = next;
        }
        finally { _gate.Release(); }
    }
    internal void External(ProxyConfig value) => Current = value;
    private async Task WriteAsync(ProxyConfig value, string? backup)
    { await AtomicFile.WriteAllTextAsync(path, Document(value).ToJsonString(ProxyFormat.Json), backup, CancellationToken.None); Writes++; }
}
internal sealed class PrototypeObserver
{
    internal bool Fail;
    internal int Applies;
    internal ProxyConfig? Published;
    internal Task ApplyAsync(ProxyConfig current)
    { Applies++; if (Fail) throw new IOException("Synthetic apply failure."); Published = current; return Task.CompletedTask; }
    internal void RequireCurrent(ProxyConfig current)
    { if (current != Published) throw new IOException("Proxy configuration has not been applied; no direct fallback."); }
}
internal sealed class ProposedEditor(ProposalStore store, MemorySecrets secrets, PrototypeObserver observer)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Guid _owner = Guid.NewGuid();
    internal Func<Task>? AfterStage;
    private sealed class Receipt(Guid owner, string? candidate) : ISettingsApplyReceipt
    {
        public Guid CommitId { get; } = Guid.NewGuid();
        internal Guid Owner => owner;
        internal string? Candidate = candidate;
        internal bool Complete;
        public override string ToString() => "ProxyApplyReceipt";
    }
    private static bool Conflict<T>(SettingsFieldEdit<T>? edit, T current) => edit != null && !EqualityComparer<T>.Default.Equals(edit.Base, current) && !EqualityComparer<T>.Default.Equals(edit.Target, current);
    private static ProxyConfig Merge(NetworkPatch patch, ProxyConfig current, string? replacementRef, bool staged)
    {
        if (Conflict(patch.Mode, current.ProxyMode) || Conflict(patch.Url, current.CustomProxyUrl) || Conflict(patch.Bypass, current.BypassProxyOnLocal)) throw new ConflictException();
        if (patch.Credentials is { } edit && (edit.BaseProxyUrl != current.CustomProxyUrl || edit.BaseCredentialSetId != current.ProxyCredentialSetId)) throw new ConflictException();
        var next = current with { ProxyMode = patch.Mode?.Target ?? current.ProxyMode, CustomProxyUrl = patch.Url == null ? current.CustomProxyUrl : ProxyFormat.Clean(patch.Url.Target), BypassProxyOnLocal = patch.Bypass?.Target ?? current.BypassProxyOnLocal };
        if (staged) next = next with { ProxyCredentialSetId = replacementRef };
        return next;
    }
    internal async Task<SettingsEditResult> CommitAsync(NetworkPatch patch)
    {
        await _gate.WaitAsync(); string? staged = null;
        try
        {
            var current = store.Current; ProxyConfig next;
            try
            {
                next = Merge(patch, current, null, false);
                if (!Enum.IsDefined(next.ProxyMode) || next.ProxyMode == ProxyMode.Custom && next.CustomProxyUrl == null) throw new ArgumentException();
                var intent = patch.Credentials;
                if (intent != null && (!Enum.IsDefined(intent.Kind) || (intent.Kind == CredentialEditKind.Replace) != (intent.Replacement != null))) throw new ArgumentException();
                if (next.CustomProxyUrl != current.CustomProxyUrl && current.ProxyCredentialSetId != null && (intent == null || intent.Kind == CredentialEditKind.Keep)) throw new ArgumentException();
                if (intent?.Kind == CredentialEditKind.Replace && (intent.Replacement!.UserName == null || intent.Replacement.Password == null)) throw new ArgumentException();
            }
            catch (ConflictException) { return new(SettingsEditStatus.Uncommitted, true); }
            catch (ArgumentException) { return new(SettingsEditStatus.Uncommitted, Error: new(ErrorCode.InvalidArgument, "Invalid network draft.")); }
            try
            {
                if (patch.Credentials is { Kind: CredentialEditKind.Replace or CredentialEditKind.Clear } edit)
                {
                    staged = ProxyFormat.Id(); await Task.Run(() => secrets.Set(ProxyFormat.Key(staged), ProxyFormat.Serialize(next.CustomProxyUrl, edit.Kind == CredentialEditKind.Clear ? null : edit.Replacement)));
                }
                if (AfterStage != null) await AfterStage();
                await store.UpdateAsync(latest => Merge(patch, latest, staged, staged != null));
            }
            catch (Exception ex)
            {
                if (staged != null && staged != store.Current.ProxyCredentialSetId) try { await Task.Run(() => secrets.Remove(ProxyFormat.Key(staged))); } catch (IOException) { }
                return new(SettingsEditStatus.Uncommitted, ex is ConflictException, new(ErrorCode.StorageFailure, "Network settings not committed."));
            }
            var receipt = new Receipt(_owner, current.ProxyCredentialSetId != store.Current.ProxyCredentialSetId ? current.ProxyCredentialSetId : null);
            bool completed = await ApplyAsync(receipt);
            return completed ? new(SettingsEditStatus.Applied) : new(SettingsEditStatus.ApplyPending, PendingApply: receipt);
        }
        finally { _gate.Release(); }
    }
    private async Task<bool> ApplyAsync(Receipt receipt)
    {
        try
        {
            await observer.ApplyAsync(store.Current);
            if (ProxyFormat.ValidId(receipt.Candidate) && receipt.Candidate != store.Current.ProxyCredentialSetId)
                await Task.Run(() => secrets.Remove(ProxyFormat.Key(receipt.Candidate!)));
            receipt.Candidate = null; receipt.Complete = true; return true;
        }
        catch (IOException) { return false; }
    }
    internal async Task<bool> RetryAsync(ISettingsApplyReceipt input)
    {
        if (input is not Receipt receipt || receipt.Owner != _owner) throw new ArgumentException("Foreign receipt.");
        await _gate.WaitAsync(); try { return receipt.Complete || await ApplyAsync(receipt); } finally { _gate.Release(); }
    }
    private sealed class ConflictException : Exception;
}
