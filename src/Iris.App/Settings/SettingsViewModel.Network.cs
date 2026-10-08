using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Iris.Core.Errors;
using Iris.Core.Network;
using Iris.Core.Settings;

namespace Iris.App.Settings;

internal sealed partial class SettingsViewModel
{
    private readonly IProxyConnectivityTester? _networkTester;
    private readonly TimeProvider _networkTime;
    private readonly SettingsDraftOperationQueue _networkQueue = new();
    private readonly HashSet<Task> _networkWork = [];
    private NetworkTestOwner? _networkTest;
    private NetworkEndpointChoice? _selectedNetworkEndpoint;
    private NetworkSettings? _networkExternal;
    private long _networkSequence, _networkRequestedRevision, _networkRequestedSequence;
    private long _networkCancelSequence = -1;
    private bool _networkRequestedPending;
    private bool _mergingNetwork;
    private NetworkEndpointChoice? _networkRequestedEndpoint;
    public NetworkSettingsDraftViewModel Network { get; private set; } = null!;
    public ObservableCollection<NetworkEndpointChoice> NetworkEndpoints { get; } = [];
    public ICommand TestNetworkCommand { get; private set; } = null!;
    public ICommand CancelNetworkCommand { get; private set; } = null!;
    public ICommand RetryNetworkCleanupCommand { get; private set; } = null!;
    public string? NetworkTestStatus { get; private set; }
    public string? NetworkResultText { get; private set; }
    public bool HasNetworkStatus => !string.IsNullOrEmpty(NetworkTestStatus);
    public bool HasNetworkResult => !string.IsNullOrEmpty(NetworkResultText);
    public bool HasFailedNetworkCleanup => _networkTest?.Failed == true;
    public bool HasNetworkTestOwnership { get { lock (_admissionLock) return _networkTest != null || _networkQueue.HasActiveWork || _networkWork.Count != 0; } }
    public string NetworkEndpointHint => NetworkEndpoints.Count == 0 ? UiText.NetworkNoEndpoints
        : SelectedNetworkEndpoint?.EndpointText ?? UiText.NetworkChooseEndpoint;
    private bool CanTestNetwork => CanEdit && _networkTester != null && SelectedNetworkEndpoint != null && !HasFailedNetworkCleanup
        && !(_networkRequestedPending && _networkRequestedRevision == Network.Revision && _networkRequestedEndpoint?.SameEndpoint(SelectedNetworkEndpoint) == true);
    public NetworkEndpointChoice? SelectedNetworkEndpoint
    {
        get => _selectedNetworkEndpoint;
        set
        {
            if (_mergingNetwork || !CanEdit || Equals(value, _selectedNetworkEndpoint) || value != null && !NetworkEndpoints.Contains(value)) return;
            _selectedNetworkEndpoint = value; InvalidateNetworkTest(); RaiseNetworkCommands();
        }
    }
    private void InitializeNetwork()
    {
        Network = new(_settingsService.Current.Network, () => CanEdit, OnNetworkDraftChanged);
        Network.PropertyChanged += OnNetworkDraftPropertyChanged;
        // This command can replace the one pending intent after a field change. Work owns its own full barrier.
        TestNetworkCommand = new RelayCommand(() => ObserveNetworkCommand(ExecuteNetworkTestAsync()), () => CanTestNetwork);
        CancelNetworkCommand = new RelayCommand(CancelNetworkTest, () => !_disposed && (_networkTest != null || _networkRequestedPending) && !HasFailedNetworkCleanup);
        RetryNetworkCleanupCommand = new AsyncRelayCommand(RetryNetworkCleanupAsync,
            () => !_disposed && !IsSaving && HasFailedNetworkCleanup, ex => HandleCommandException("NetworkCleanup", ex));
        MergeNetworkSettings(reset: true);
    }
    private void ObserveNetworkCommand(Task task) => _ = task.ContinueWith(done =>
        LogTestDraftException(_logger, done.Exception!.GetType().Name), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    private void OnNetworkDraftPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(NetworkSettingsDraftViewModel.ReplacementPassword)) OnPropertyChanged(nameof(Network));
    }
    private void OnNetworkDraftChanged()
    { InvalidateNetworkTest(); OnPropertyChanged(nameof(IsDirty)); RaiseAllCommandsCanExecute(); }

    private void InvalidateNetworkTest(bool userCanceled = false)
    {
        Interlocked.Increment(ref _networkSequence); _networkQueue.CancelInFlight();
        _networkCancelSequence = userCanceled ? _networkSequence : -1;
        NetworkResultText = null;
        if (_networkTest != null && !HasFailedNetworkCleanup) NetworkTestStatus = UiText.NetworkEnding;
        RaiseNetworkCommands();
    }
    public void CancelNetworkTest() => InvalidateNetworkTest(userCanceled: true);

    private void MergeNetworkSettings(bool reset)
    {
        var settings = _settingsService.Current;
        var choices = NetworkEndpointChoice.FromSettings(settings).Where(choice => choice.IsValid).ToArray();
        var previous = _selectedNetworkEndpoint;
        var selected = choices.FirstOrDefault(c => c.Selection == previous?.Selection);
        bool changed = _networkExternal != settings.Network || previous != null && !previous.SameEndpoint(selected);
        if (changed || reset) InvalidateNetworkTest();
        _networkExternal = settings.Network;
        Network.Merge(settings.Network, reset);
        _mergingNetwork = true;
        try
        {
            NetworkEndpoints.Clear(); foreach (var choice in choices) NetworkEndpoints.Add(choice);
            _selectedNetworkEndpoint = selected; // Deletion leaves no selection; never switch to another server.
        }
        finally { _mergingNetwork = false; }
        RaiseNetworkCommands();
    }
    private void RaiseNetworkCommands()
    {
        (TestNetworkCommand as RelayCommand)?.RaiseCanExecuteChanged(); (CancelNetworkCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RetryNetworkCleanupCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        foreach (var name in new[] { nameof(SelectedNetworkEndpoint), nameof(NetworkEndpointHint), nameof(NetworkTestStatus), nameof(NetworkResultText),
            nameof(HasNetworkStatus), nameof(HasNetworkResult), nameof(HasNetworkTestOwnership), nameof(HasFailedNetworkCleanup), nameof(CanSave) }) OnPropertyChanged(name);
        (SaveCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private sealed class NetworkTestOwner(NetworkEndpointChoice endpoint, NetworkSettings savedNetwork, long revision, long sequence, IProxyConnectivityTestSession session)
    {
        internal readonly NetworkEndpointChoice Endpoint = endpoint;
        internal readonly NetworkSettings SavedNetwork = savedNetwork;
        internal readonly long Revision = revision, Sequence = sequence;
        internal readonly IProxyConnectivityTestSession Session = session;
        internal ProxyConnectivityResult? Result;
        internal Task<bool>? Cleanup;
        internal bool Canceled, ResourceDisposed;
        internal volatile bool Failed;
    }
    private bool NetworkResultIsCurrent(NetworkTestOwner owner) => !_disposed && !_isStopped && owner.Revision == Network.Revision
        && owner.Sequence == _networkSequence && owner.Endpoint.SameEndpoint(SelectedNetworkEndpoint)
        && owner.SavedNetwork == _settingsService.Current.Network
        && owner.Endpoint.SameEndpoint(NetworkEndpointChoice.FromSettings(_settingsService.Current).FirstOrDefault(c => c.Selection == owner.Endpoint.Selection));

    public async Task ExecuteNetworkTestAsync()
    {
        TaskCompletionSource? work = null;
        long admittedSequence = 0;
        try
        {
            if (!CanTestNetwork) return;
            BeforeSave?.Invoke();
            if (!Network.TryCapture(out _, forTest: true)) return;
            var endpoint = SelectedNetworkEndpoint!;
            if (!endpoint.IsValid) { NetworkResultText = UiText.NetworkInvalidEndpoint; RaiseNetworkCommands(); return; }
            if (!endpoint.SameEndpoint(NetworkEndpointChoice.FromSettings(_settingsService.Current).FirstOrDefault(c => c.Selection == endpoint.Selection)))
            { MergeNetworkSettings(false); NetworkResultText = UiText.NetworkEndpointChanged; RaiseNetworkCommands(); return; }
            var draft = Network.CaptureDraft(); var saved = _settingsService.Current.Network;
            long revision = Network.Revision, sequence = Interlocked.Increment(ref _networkSequence);
            _networkRequestedRevision = revision; _networkRequestedEndpoint = endpoint;
            _networkRequestedPending = true; _networkRequestedSequence = admittedSequence = sequence;
            work = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_admissionLock) { _activeWork.Add(work.Task); _networkWork.Add(work.Task); }
            RaiseNetworkCommands();
            await _networkQueue.EnqueueAsync(async token =>
            {
                NetworkTestOwner? owner = null;
                try
                {
                    await _uiDispatcher.InvokeAsync(() =>
                    {
                        if (!CanEdit || token.IsCancellationRequested || _networkTest != null || sequence != _networkSequence || revision != Network.Revision
                            || !endpoint.SameEndpoint(SelectedNetworkEndpoint) || saved != _settingsService.Current.Network) return Task.CompletedTask;
                        owner = new(endpoint, saved, revision, sequence, _networkTester!.Create(draft, endpoint.Selection));
                        _networkTest = owner; NetworkTestStatus = UiText.NetworkTesting; NetworkResultText = null; RaiseNetworkCommands();
                        return Task.CompletedTask;
                    }, CancellationToken.None).ConfigureAwait(false);
                    if (owner == null) return;
                    using var noticeCancel = new CancellationTokenSource();
                    var notice = ShowNetworkEndingAsync(owner, noticeCancel.Token);
                    try
                    {
                        owner.Result = await owner.Session.RunAsync(token).ConfigureAwait(false);
                        owner.Canceled = token.IsCancellationRequested;
                    }
                    catch (OperationCanceledException) { owner.Canceled = true; }
                    catch (Exception ex) { LogTestDraftException(_logger, ex.GetType().Name); }
                    finally { await noticeCancel.CancelAsync().ConfigureAwait(false); await notice.ConfigureAwait(false); }
                    await _uiDispatcher.InvokeAsync(() => { NetworkTestStatus = UiText.NetworkEnding; RaiseNetworkCommands(); return Task.CompletedTask; }, CancellationToken.None).ConfigureAwait(false);
                }
                finally { if (owner != null) await CleanupNetworkOwnerAsync(owner).ConfigureAwait(false); }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await _uiDispatcher.InvokeAsync(() => { HandleCommandException("NetworkTest", ex); return Task.CompletedTask; }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (work != null)
            {
                try
                {
                    await _uiDispatcher.InvokeAsync(() =>
                    {
                        if (_networkRequestedSequence == admittedSequence) _networkRequestedPending = false;
                        RaiseNetworkCommands(); return Task.CompletedTask;
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                finally { lock (_admissionLock) { _activeWork.Remove(work.Task); _networkWork.Remove(work.Task); } work.TrySetResult(); }
            }
        }
    }
    private async Task ShowNetworkEndingAsync(NetworkTestOwner owner, CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), _networkTime, token).ConfigureAwait(false);
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_networkTest, owner) && !owner.Failed) { NetworkTestStatus = UiText.NetworkEnding; RaiseNetworkCommands(); }
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private Task<bool> CleanupNetworkOwnerAsync(NetworkTestOwner owner)
    {
        lock (_admissionLock)
        {
            if (owner.Cleanup is { IsCompleted: false }) return owner.Cleanup;
            return owner.Cleanup = CleanupAsync();
        }
        async Task<bool> CleanupAsync()
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            try
            {
                if (!owner.ResourceDisposed) { await owner.Session.DisposeAsync().ConfigureAwait(false); owner.ResourceDisposed = true; }
                await _uiDispatcher.InvokeAsync(() =>
                {
                    if (!ReferenceEquals(_networkTest, owner)) return Task.CompletedTask;
                    try
                    {
                        if (!_isStopped && !_disposed && _networkCancelSequence == _networkSequence) NetworkResultText = UiText.NetworkCanceled;
                        else if (NetworkResultIsCurrent(owner)) NetworkResultText = owner.Canceled ? UiText.NetworkCanceled : FormatNetworkResult(owner.Result);
                        owner.Failed = false; _networkTest = null; NetworkTestStatus = null; RaiseNetworkCommands();
                    }
                    catch { _networkTest = owner; owner.Failed = true; throw; }
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                owner.Failed = true; LogTestDraftException(_logger, ex.GetType().Name);
                await _uiDispatcher.InvokeAsync(() => { NetworkTestStatus = UiText.NetworkCleanupFailed; RaiseNetworkCommands(); return Task.CompletedTask; }, CancellationToken.None).ConfigureAwait(false);
                return false;
            }
        }
    }
    private static string FormatNetworkResult(ProxyConnectivityResult? result)
    {
        if (result == null) return UiText.NetworkTransportFailed;
        if (result.HttpStatusCode is { } status && result.Status is ProxyConnectivityStatus.EndpointResponded or ProxyConnectivityStatus.RedirectNotFollowed or ProxyConnectivityStatus.ProxyAuthenticationRequired)
        {
            string outcome = result.Status == ProxyConnectivityStatus.EndpointResponded ? UiText.NetworkResponded
                : result.Status == ProxyConnectivityStatus.RedirectNotFollowed ? UiText.NetworkRedirect : UiText.NetworkProxyAuthentication;
            string route = result.RouteKind switch
            {
                ProxyRouteKind.DirectByMode => UiText.NetworkDirect, ProxyRouteKind.DirectByLocalBypass => UiText.NetworkLocalDirect,
                ProxyRouteKind.DirectBySystemBypass => UiText.NetworkSystemDirect, ProxyRouteKind.Proxy => UiText.NetworkViaProxy, _ => UiText.NetworkRouteUnknown
            };
            return $"{outcome} · HTTP {status} · {route} · {result.Elapsed.TotalMilliseconds:F0} ms\n{UiText.NetworkResponseDisclaimer}";
        }
        // RouteKind has no 'unknown' contract value. Never infer a confirmed route from its default on failures.
        return result.Status switch { ProxyConnectivityStatus.TimedOut => UiText.NetworkTimedOut,
            ProxyConnectivityStatus.InvalidConfiguration => UiText.NetworkInvalidConfiguration, _ => UiText.NetworkTransportFailed };
    }
    public async Task RetryNetworkCleanupAsync()
    {
        NetworkTestOwner owner;
        var work = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_admissionLock)
        {
            if (_disposed || IsSaving || _networkTest is not { Failed: true } pending) return;
            owner = pending; _activeWork.Add(work.Task); _networkWork.Add(work.Task);
        }
        try
        {
            if (await CleanupNetworkOwnerAsync(owner).ConfigureAwait(false) && _isStopped)
                await _uiDispatcher.InvokeAsync(() => { RequestClose?.Invoke(); return Task.CompletedTask; }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { lock (_admissionLock) { _activeWork.Remove(work.Task); _networkWork.Remove(work.Task); } work.TrySetResult(); }
    }
    private async Task<bool> DrainNetworkTestsAsync(bool closing)
    {
        Task[] work; NetworkTestOwner? failed;
        lock (_admissionLock) { work = _networkWork.ToArray(); failed = _networkTest is { Failed: true } value ? value : null; }
        await _uiDispatcher.InvokeAsync(() =>
        {
            if (_networkTest != null && !HasFailedNetworkCleanup) { NetworkTestStatus = UiText.NetworkEnding; RaiseNetworkCommands(); }
            return Task.CompletedTask;
        }, CancellationToken.None).ConfigureAwait(false);
        if (closing) await _networkQueue.DrainAndCancelAllAsync().ConfigureAwait(false);
        else await _networkQueue.DrainAndCancelInFlightAsync().ConfigureAwait(false);
        await Task.WhenAll(work).ConfigureAwait(false);
        if (closing && work.Length == 0 && failed != null) await CleanupNetworkOwnerAsync(failed).ConfigureAwait(false);
        return _networkTest == null;
    }
    private void DisposeNetworkDraft()
    {
        if (HasNetworkTestOwnership) throw new InvalidOperationException(UiText.NetworkCleanupFailed);
        Network.ClearReplacement(); Network.PropertyChanged -= OnNetworkDraftPropertyChanged; _networkQueue.Dispose();
    }
}
