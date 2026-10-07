using Iris.Core.Errors;
using Iris.Core.Network;
using Iris.Core.Security;

namespace Iris.Core.Settings;

internal sealed partial class SettingsEditCoordinator : INetworkProxySnapshotSource
{
    private readonly IEnumerable<INetworkConfigurationObserver> _networkObservers;
    private NetworkSettings? _publishedNetwork;
    private Guid _networkGeneration;
    private bool _networkApplied;
    private string? _watchedNetworkKey;
    private long _networkSecretRevision;
    private long _publishedSecretRevision;
    private bool _hasCapturedNetworkValue;
    private string? _capturedNetworkValue;
    private bool _networkSubscribed;

    private void EnsureNetworkSubscription()
    {
        if (_networkSubscribed) return;
        _secretStore.Changed += OnNetworkSecretChanged;
        _networkSubscribed = true;
    }

    private void OnNetworkSecretChanged(object? sender, string key)
    {
        if (string.Equals(key, Volatile.Read(ref _watchedNetworkKey), StringComparison.Ordinal))
            Interlocked.Increment(ref _networkSecretRevision);
    }

    private static bool HasNetworkEdits(NetworkSettingsPatch? patch) => patch != null &&
        (patch.ProxyMode != null || patch.CustomProxyUrl != null || patch.BypassProxyOnLocal != null || patch.Credentials != null);

    private static ErrorInfo? ValidateNetwork(NetworkSettingsPatch? patch, NetworkSettings current)
    {
        if (!HasNetworkEdits(patch)) return null;
        try
        {
            var mode = patch!.ProxyMode?.Target ?? current.ProxyMode;
            if (!Enum.IsDefined(mode) || patch.ProxyMode is { } modeEdit && !Enum.IsDefined(modeEdit.Base)) throw NetworkProxyCodec.Invalid();
            var edit = patch.Credentials;
            if (edit != null && (!Enum.IsDefined(edit.Kind) || (edit.Kind == CredentialEditKind.Replace) != (edit.Replacement != null)))
                throw NetworkProxyCodec.Invalid();
            if (patch.CustomProxyUrl is { } url && !string.Equals(url.Base, url.Target, StringComparison.Ordinal)
                && edit?.Kind is not CredentialEditKind.Replace and not CredentialEditKind.Clear) throw NetworkProxyCodec.Invalid();
            if (patch.CustomProxyUrl != null || edit != null || mode == ProxyMode.Custom)
            {
                var uri = NetworkProxyCodec.ParseUri(patch.CustomProxyUrl is { } field ? field.Target : current.CustomProxyUrl);
                NetworkProxyCodec.ValidateCredentials(edit?.Replacement, uri);
            }
            return null;
        }
        catch (IrisException ex) { return ex.ToErrorInfo(); }
    }

    private static bool HasNetworkConflict(NetworkSettingsPatch? patch, NetworkSettings current) => patch != null &&
        (SelectionConflict(patch.ProxyMode, current.ProxyMode, static (a, b) => a == b)
         || SelectionConflict(patch.CustomProxyUrl, current.CustomProxyUrl, static (a, b) => string.Equals(a, b, StringComparison.Ordinal))
         || SelectionConflict(patch.BypassProxyOnLocal, current.BypassProxyOnLocal, static (a, b) => a == b)
         || patch.Credentials is { } edit && (edit.BaseProxyUrl != current.CustomProxyUrl || edit.BaseCredentialSetId != current.ProxyCredentialSetId));

    private async Task<ErrorInfo?> ValidateKeptNetworkCredentialsAsync(NetworkSettingsPatch? patch, NetworkSettings current, CancellationToken cancellationToken)
    {
        if (!HasNetworkEdits(patch) || (patch!.ProxyMode?.Target ?? current.ProxyMode) != ProxyMode.Custom
            || patch.Credentials?.Kind is CredentialEditKind.Replace or CredentialEditKind.Clear) return null;
        try
        {
            var raw = await ReadNetworkValueAsync(current, cancellationToken).ConfigureAwait(false);
            if (current.ProxyCredentialSetId != null) NetworkProxyCodec.Deserialize(raw, NetworkProxyCodec.ParseUri(current.CustomProxyUrl));
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (NetworkCaptureChangedException) { return NetworkProxyCodec.Unavailable().ToErrorInfo(); }
        catch (IrisException error) { return error.ToErrorInfo(); }
    }

    private static StagingPlan PrepareNetworkStaging(SettingsEditPlan plan, AppSettings current, StagingPlan staging)
    {
        if (staging.ConflictError != null) return staging;
        if (HasNetworkConflict(plan.Network, current.Network)) return StagingPlan.Conflict(new(ErrorCode.Unknown, ConflictMessage));
        if (plan.Network?.Credentials is { Kind: CredentialEditKind.Replace or CredentialEditKind.Clear } edit)
        {
            var uri = NetworkProxyCodec.ParseUri(plan.Network.CustomProxyUrl is { } url ? url.Target : current.Network.CustomProxyUrl);
            staging.NetworkSetId = "pc-" + Guid.NewGuid().ToString("N");
            staging.NetworkSetValue = NetworkProxyCodec.Serialize(uri, edit.Replacement);
            if (NetworkProxyCodec.IsCredentialSetId(current.Network.ProxyCredentialSetId))
                staging.NetworkCandidates.Add(current.Network.ProxyCredentialSetId!);
        }
        return staging;
    }

    private static NetworkSettings ApplyNetwork(NetworkSettingsPatch? patch, NetworkSettings current, string? stagedId)
    {
        if (!HasNetworkEdits(patch)) return current;
        if (HasNetworkConflict(patch, current)) throw new StagingConflictException(new(ErrorCode.Unknown, ConflictMessage));
        if (ValidateNetwork(patch, current) is { } error) throw new StagingConflictException(error);
        var target = current with
        {
            ProxyMode = patch!.ProxyMode?.Target ?? current.ProxyMode,
            CustomProxyUrl = patch.CustomProxyUrl is { } url ? NetworkProxyCodec.ParseUri(url.Target).AbsoluteUri : current.CustomProxyUrl,
            BypassProxyOnLocal = patch.BypassProxyOnLocal?.Target ?? current.BypassProxyOnLocal,
            ProxyCredentialSetId = stagedId ?? current.ProxyCredentialSetId
        };
        return target == current ? current : target;
    }

    private async Task<bool> CleanupNetworkCandidatesAsync(List<string> candidates)
    {
        bool complete = true;
        foreach (var id in candidates.ToArray())
        {
            if (!NetworkProxyCodec.IsCredentialSetId(id) || _settingsService.Current.Network.ProxyCredentialSetId == id)
            { candidates.Remove(id); continue; }
            try
            {
                await Task.Run(() => _secretStore.Remove(SecretKeys.ProxyCredentialSet(id)), CancellationToken.None).ConfigureAwait(false);
                candidates.Remove(id);
            }
            catch (Exception ex) { LogCandidateCleanupFailed(_logger, "ProxyCredentialSet", ex.GetType().Name); complete = false; }
        }
        return complete;
    }

    private async Task<bool> RefreshReceiptObserversAsync(SettingsApplyReceipt receipt)
    {
        bool applied = !receipt.RequiresProviders || await RefreshObserversAsync().ConfigureAwait(false);
        receipt.NetworkFailed = false;
        if (receipt.RequiresNetwork)
        {
            try { await PublishNetworkAsync(_settingsService.Current.Network, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { LogObserverRefreshFailed(_logger, "Network", ex.GetType().Name); receipt.NetworkFailed = true; applied = false; }
        }
        return applied;
    }

    // Called only under _gate. Observer publication establishes order before a snapshot can escape.
    // Arrival order at the transport manager therefore never determines which generation is current.
    private async Task<Guid> PublishNetworkAsync(NetworkSettings network, CancellationToken cancellationToken)
    {
        EnsureNetworkSubscription();
        string? key = NetworkProxyCodec.IsCredentialSetId(network.ProxyCredentialSetId) ? SecretKeys.ProxyCredentialSet(network.ProxyCredentialSetId!) : null;
        Volatile.Write(ref _watchedNetworkKey, key);
        long revision = Volatile.Read(ref _networkSecretRevision);
        if (_publishedNetwork != network || _publishedSecretRevision != revision)
        {
            _publishedNetwork = network;
            _publishedSecretRevision = revision;
            _networkGeneration = Guid.NewGuid();
            _networkApplied = false;
            _hasCapturedNetworkValue = false;
            _capturedNetworkValue = null;
        }
        if (!_networkApplied)
        {
            foreach (var observer in _networkObservers)
                await observer.ApplyAsync(network, _networkGeneration, cancellationToken).ConfigureAwait(false);
            _networkApplied = true;
        }
        return _networkGeneration;
    }

    public async Task<NetworkProxySnapshot> CaptureCurrentAsync(Uri destination, CancellationToken cancellationToken = default)
    {
        ValidateDestination(destination);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_settingsService.IsReadOnly) throw NetworkProxyCodec.StorageFailure();
            EnsureNetworkSubscription();
            for (int attempt = 0; attempt < 4; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var network = _settingsService.Current.Network;
                Volatile.Write(ref _watchedNetworkKey, NetworkProxyCodec.IsCredentialSetId(network.ProxyCredentialSetId)
                    ? SecretKeys.ProxyCredentialSet(network.ProxyCredentialSetId!) : null);
                long revision = Volatile.Read(ref _networkSecretRevision);
                bool needsSecret = NeedsCustomProxy(network, destination);
                string? raw;
                try { raw = needsSecret ? await ReadNetworkValueAsync(network, cancellationToken).ConfigureAwait(false) : null; }
                catch (NetworkCaptureChangedException) { continue; }
                if (network != _settingsService.Current.Network || revision != Volatile.Read(ref _networkSecretRevision)) continue;
                if (needsSecret && _publishedNetwork == network && _hasCapturedNetworkValue && raw != _capturedNetworkValue)
                    Interlocked.Increment(ref _networkSecretRevision);
                var generation = await PublishNetworkAsync(network, cancellationToken).ConfigureAwait(false);
                if (needsSecret) { _capturedNetworkValue = raw; _hasCapturedNetworkValue = true; }
                // Pure validation and value construction; never sends or owns transport resources.
                var snapshot = CreateNetworkSnapshot(network, destination, generation, raw, null, false);
                if (needsSecret)
                {
                    try { if (raw != await ReadNetworkValueAsync(network, cancellationToken).ConfigureAwait(false)) continue; }
                    catch (NetworkCaptureChangedException) { continue; }
                }
                if (network == _settingsService.Current.Network && _publishedSecretRevision == Volatile.Read(ref _networkSecretRevision)) return snapshot;
            }
            throw NetworkProxyCodec.Unavailable();
        }
        finally { _gate.Release(); }
    }

    public async Task<NetworkProxySnapshot> CaptureDraftAsync(ProxyDraft draft, Uri destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateDestination(destination);
        // All public values are immutable. Capture before the first wait.
        var baseline = draft.BaseSettings ?? throw NetworkProxyCodec.Invalid();
        var target = draft.TargetSettings ?? throw NetworkProxyCodec.Invalid();
        var edit = draft.Credentials;
        if (target.ProxyCredentialSetId != baseline.ProxyCredentialSetId) throw NetworkProxyCodec.Invalid();
        var patch = new NetworkSettingsPatch(new(baseline.ProxyMode, target.ProxyMode),
            new(baseline.CustomProxyUrl, target.CustomProxyUrl), new(baseline.BypassProxyOnLocal, target.BypassProxyOnLocal), edit);
        // Dormant null URLs need no validation unless edited or used.
        if (target.CustomProxyUrl == baseline.CustomProxyUrl) patch = patch with { CustomProxyUrl = null };
        if (ValidateNetwork(patch, baseline) is { } error) throw new IrisException(error.Code, error.Message);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_settingsService.IsReadOnly) throw NetworkProxyCodec.StorageFailure();
            EnsureNetworkSubscription();
            bool keep = edit == null || edit.Kind == CredentialEditKind.Keep;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                var current = _settingsService.Current.Network;
                if (edit != null && (edit.BaseProxyUrl != baseline.CustomProxyUrl || edit.BaseCredentialSetId != baseline.ProxyCredentialSetId)) throw NetworkProxyCodec.Invalid();
                if (keep && (current.CustomProxyUrl != baseline.CustomProxyUrl || current.ProxyCredentialSetId != baseline.ProxyCredentialSetId))
                    throw new IrisException(ErrorCode.InvalidArgument, ConflictMessage);
                if (keep) Volatile.Write(ref _watchedNetworkKey, NetworkProxyCodec.IsCredentialSetId(current.ProxyCredentialSetId)
                    ? SecretKeys.ProxyCredentialSet(current.ProxyCredentialSetId!) : null);
                long revision = Volatile.Read(ref _networkSecretRevision);
                string? raw;
                try { raw = keep && NeedsCustomProxy(target, destination) ? await ReadNetworkValueAsync(target, cancellationToken).ConfigureAwait(false) : null; }
                catch (NetworkCaptureChangedException) { continue; }
                if (keep && (current != _settingsService.Current.Network || revision != Volatile.Read(ref _networkSecretRevision))) continue;
                return CreateNetworkSnapshot(target, destination, Guid.NewGuid(), raw, edit?.Replacement, !keep);
            }
            throw NetworkProxyCodec.Unavailable();
        }
        finally { _gate.Release(); }
    }

    private async Task<string?> ReadNetworkValueAsync(NetworkSettings network, CancellationToken cancellationToken)
    {
        NetworkProxyCodec.ParseUri(network.CustomProxyUrl);
        if (network.ProxyCredentialSetId == null) return null;
        if (!NetworkProxyCodec.IsCredentialSetId(network.ProxyCredentialSetId)) throw NetworkProxyCodec.SecretUnavailable();
        try
        {
            string key = SecretKeys.ProxyCredentialSet(network.ProxyCredentialSetId);
            // Join synchronous SecretStore work even if cancellation arrives after it starts.
            var value = await Task.Run(() =>
            {
                var first = _secretStore.Get(key);
                if (first != _secretStore.Get(key)) throw new NetworkCaptureChangedException();
                return first;
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return value ?? throw NetworkProxyCodec.SecretUnavailable();
        }
        catch (OperationCanceledException) { throw; }
        catch (NetworkCaptureChangedException) { throw; }
        catch (IrisException ex) when (ex.Code == ErrorCode.NetworkUnavailable) { throw NetworkProxyCodec.Unavailable(); }
        catch (IrisException) { throw NetworkProxyCodec.SecretUnavailable(); }
        catch { throw NetworkProxyCodec.SecretUnavailable(); }
    }

    private sealed class NetworkCaptureChangedException : Exception;

    private static bool NeedsCustomProxy(NetworkSettings settings, Uri destination) => settings.ProxyMode == ProxyMode.Custom
        && !(settings.BypassProxyOnLocal && NetworkAddressClassifier.IsLocalAddress(destination));

    private static void ValidateDestination(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.IsAbsoluteUri || destination.Scheme is not "http" and not "https") throw NetworkProxyCodec.Invalid();
    }

    private static NetworkProxySnapshot CreateNetworkSnapshot(NetworkSettings settings, Uri destination, Guid generation,
        string? raw, ProxyCredentials? replacement, bool replace)
    {
        if (!Enum.IsDefined(settings.ProxyMode)) throw NetworkProxyCodec.Invalid();
        if (settings.ProxyMode == ProxyMode.None) return new(generation, destination, ProxyMode.None, null, null, ProxyRouteKind.DirectByMode);
        if (settings.BypassProxyOnLocal && NetworkAddressClassifier.IsLocalAddress(destination))
            return new(generation, destination, ProxyMode.None, null, null, ProxyRouteKind.DirectByLocalBypass);
        if (settings.ProxyMode == ProxyMode.System) return new(generation, destination, ProxyMode.System, null, null, ProxyRouteKind.Proxy);
        var uri = NetworkProxyCodec.ParseUri(settings.CustomProxyUrl);
        var credentials = replace ? replacement : settings.ProxyCredentialSetId == null ? null : NetworkProxyCodec.Deserialize(raw, uri);
        NetworkProxyCodec.ValidateCredentials(credentials, uri);
        return new(generation, destination, ProxyMode.Custom, uri, credentials, ProxyRouteKind.Proxy);
    }
}
