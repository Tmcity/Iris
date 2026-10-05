using System.Windows.Input;
using Iris.App.Selection;
using Iris.Core.Errors;

namespace Iris.App.Settings;

internal sealed partial class SettingsViewModel
{
    private SelectionIconController? _mouseController;
    private MouseSelectionRuntimeSnapshot? _mouseRuntime;
    public ICommand RetryMouseCommand { get; private set; } = null!;
    public bool CanRetryMouse => _mouseRuntime?.CanRetry == true;
    public bool HasMouseRuntimeStatus => _mouseRuntime != null;
    public string? MouseRuntimeStatus => _mouseRuntime switch
    {
        { IsApplying: true } => UiText.MouseApplying,
        { Error: ErrorCode.HookInstallFailed } => UiText.MouseApplyFailed,
        { Error: not null } => UiText.MouseReadFailed,
        { ActualMode: Iris.Core.Settings.MouseSelectionMode.Off } => UiText.MouseStopped,
        null => null,
        _ => UiText.MouseRunning,
    };
    private void InitializeMouse(SelectionIconController? controller)
    {
        _mouseController = controller;
        RetryMouseCommand = new AsyncRelayCommand(ExecuteRetryMouseAsync, () => CanEdit && CanRetryMouse, ex => HandleCommandException("RetryMouse", ex));
        if (controller == null) return;
        _mouseRuntime = controller.Runtime;
        controller.RuntimeChanged += OnMouseRuntimeChanged;
    }
    private void OnMouseRuntimeChanged(object? sender, MouseSelectionRuntimeSnapshot snapshot)
    {
        // Controller publishes runtime snapshots in its single owned UI worker.
        lock (_admissionLock)
        {
            if (_disposed || _isStopped || snapshot.Version < (_mouseRuntime?.Version ?? -1)) return;
            _mouseRuntime = snapshot;
        }
        OnPropertyChanged(nameof(MouseRuntimeStatus)); OnPropertyChanged(nameof(HasMouseRuntimeStatus)); OnPropertyChanged(nameof(CanRetryMouse));
        (RetryMouseCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
    public async Task ExecuteRetryMouseAsync()
    {
        TaskCompletionSource done;
        lock (_admissionLock)
        {
            if (!CanEdit || !CanRetryMouse || _mouseController == null) return;
            done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeWork.Add(done.Task); IsSaving = true; FreezeAllDrafts();
        }
        try { await _mouseController.RetryAsync().ConfigureAwait(false); }
        finally
        {
            try
            {
                await _uiDispatcher.InvokeAsync(() =>
                {
                    OnMouseRuntimeChanged(_mouseController, _mouseController.Runtime);
                    return CompleteSaveOrRetryOperationAsync();
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (_admissionLock) { IsSaving = false; UnfreezeAllDraftsIfAlive(); }
                LogCommandUnexpectedFailed(_logger, "RetryMouseCleanup", ex.GetType().Name);
            }
            finally
            {
                lock (_admissionLock) _activeWork.Remove(done.Task);
                done.TrySetResult();
            }
        }
    }
}
