using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Iris.App.Hosting;
using Iris.Core.Actions;
using Iris.Core.Settings;

namespace Iris.App.Settings;

/// <summary>
/// 快捷键单项草稿 ViewModel。
/// 包含基线绑定、草稿绑定、录入状态机、草稿校验错误与运行时注册状态。
/// 规则见 HOTKEY_RECORDING_UI.md §4、§6、§7。
/// </summary>
internal sealed class HotkeyItemDraftViewModel : INotifyPropertyChanged
{
    private readonly IrisAction _action;
    private readonly string _displayName;
    private readonly string _description;
    private readonly SettingsViewModel _parent;

    private HotkeyBinding? _baseBinding;
    private HotkeyBinding? _targetBinding;
    private bool _enabled;
    private string? _draftError;
    private HotkeyRuntimeItemState? _runtimeState;

    private bool _isRecording;
    private bool _isPreparing;
    private string? _recordingPrompt;
    private string? _previewText;
    private bool _isFrozen;

    public event PropertyChangedEventHandler? PropertyChanged;

    public HotkeyItemDraftViewModel(
        IrisAction action,
        string displayName,
        string description,
        HotkeyBinding? baseBinding,
        SettingsViewModel parent)
    {
        _action = action;
        _displayName = displayName;
        _description = description;
        _baseBinding = baseBinding;
        _targetBinding = baseBinding;
        _enabled = baseBinding?.Enabled ?? false;
        _parent = parent ?? throw new ArgumentNullException(nameof(parent));

        StartRecordCommand = new AsyncRelayCommand(
            () => _parent.StartRecordingAsync(this),
            () => CanRecord && !_isFrozen);

        ClearCommand = new RelayCommand(
            () => Clear(),
            () => CanClear && !_isFrozen);

        CancelRecordCommand = new AsyncRelayCommand(
            () => _parent.CancelRecordingAsync(),
            () => _isRecording);
    }

    public IrisAction Action => _action;
    public string DisplayName => _displayName;
    public string Description => _description;

    public ICommand StartRecordCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand CancelRecordCommand { get; }

    public HotkeyBinding? BaseBinding => _baseBinding;

    public HotkeyBinding? TargetBinding
    {
        get => _targetBinding;
        private set
        {
            if (!AreBindingsEqual(_targetBinding, value))
            {
                _targetBinding = value;
                _enabled = value?.Enabled ?? false;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Enabled));
                OnPropertyChanged(nameof(KeyComboText));
                OnPropertyChanged(nameof(IsDirty));
                OnPropertyChanged(nameof(CanClear));
                _parent.OnHotkeyDraftChanged(this);
            }
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_isFrozen || _isRecording)
            {
                return;
            }

            if (_enabled != value)
            {
                _enabled = value;
                if (_targetBinding != null)
                {
                    _targetBinding = _targetBinding with { Enabled = value };
                }
                else if (value)
                {
                    // 若原本无任何按键设置，不能直接启用无按键的绑定
                    _enabled = false;
                    return;
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(KeyComboText));
                OnPropertyChanged(nameof(IsDirty));
                _parent.OnHotkeyDraftChanged(this);
            }
        }
    }

    public string KeyComboText
    {
        get
        {
            if (_isRecording && !string.IsNullOrEmpty(_previewText))
            {
                return _previewText;
            }
            return HotkeyCoordinator.FormatKeyCombo(_targetBinding);
        }
    }

    public bool IsDirty
    {
        get
        {
            if (_baseBinding == null && _targetBinding == null)
            {
                return false;
            }

            if (_baseBinding == null && _targetBinding != null)
            {
                // 如果目标值是禁用且无键（相当于未设置），且基线也是未设置，不视为 Dirty
                if (!_targetBinding.Enabled && string.IsNullOrWhiteSpace(_targetBinding.Key))
                {
                    return false;
                }
                return true;
            }

            if (_baseBinding != null && _targetBinding == null)
            {
                return true;
            }

            return !AreBindingsEqual(_baseBinding, _targetBinding);
        }
    }

    public string? DraftError
    {
        get => _draftError;
        internal set
        {
            if (_draftError != value)
            {
                _draftError = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasDraftError));
            }
        }
    }

    public bool HasDraftError => !string.IsNullOrEmpty(_draftError);

    public HotkeyRuntimeItemState? RuntimeState
    {
        get => _runtimeState;
        internal set
        {
            _runtimeState = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RuntimeStatusText));
            OnPropertyChanged(nameof(HasRuntimeStatus));
        }
    }

    public bool HasRuntimeStatus => !string.IsNullOrEmpty(RuntimeStatusText);

    public bool CanEdit => _parent.CanEdit;

    public string RuntimeStatusText
    {
        get
        {
            if (_runtimeState == null)
            {
                return string.Empty;
            }

            return _runtimeState.Status switch
            {
                HotkeyRuntimeActionStatus.Registered => UiText.SettingsHotkeyStatusRegistered,
                HotkeyRuntimeActionStatus.Paused => UiText.SettingsHotkeyStatusPaused,
                HotkeyRuntimeActionStatus.Conflict => UiText.SettingsHotkeyStatusConflict,
                HotkeyRuntimeActionStatus.Invalid => UiText.SettingsHotkeyStatusInvalid,
                HotkeyRuntimeActionStatus.Failed => _runtimeState.Message ?? UiText.SettingsHotkeyStatusFailed,
                HotkeyRuntimeActionStatus.Applying => UiText.SettingsHotkeyStatusApplying,
                _ => string.Empty
            };
        }
    }

    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (_isRecording != value)
            {
                _isRecording = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanRecord));
                OnPropertyChanged(nameof(KeyComboText));
                (StartRecordCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (CancelRecordCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsPreparing
    {
        get => _isPreparing;
        private set
        {
            if (_isPreparing != value)
            {
                _isPreparing = value;
                OnPropertyChanged();
            }
        }
    }

    public string? RecordingPrompt
    {
        get => _recordingPrompt;
        private set
        {
            if (_recordingPrompt != value)
            {
                _recordingPrompt = value;
                OnPropertyChanged();
            }
        }
    }

    public bool CanRecord => !_isRecording && !_parent.IsAnyRecording && !_isFrozen && _parent.CanEdit;

    public bool CanClear => !_isRecording && !_isFrozen && _parent.CanEdit &&
        _targetBinding != null && (!string.IsNullOrWhiteSpace(_targetBinding.Key) || _targetBinding.Enabled);

    public bool IsFrozen
    {
        get => _isFrozen;
        internal set
        {
            if (_isFrozen != value)
            {
                _isFrozen = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanRecord));
                OnPropertyChanged(nameof(CanClear));
                (StartRecordCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (ClearCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public void ApplyCapturedBinding(HotkeyBinding binding)
    {
        // 成功录入设置本行草稿 Enabled=true
        TargetBinding = binding with { Enabled = true };
    }

    public void Clear()
    {
        if (_isFrozen || _isRecording)
        {
            return;
        }

        // Backspace 清除为禁用空键
        TargetBinding = new HotkeyBinding(HotkeyModifiers.None, string.Empty, false);
    }

    public void RevertToBaseline()
    {
        TargetBinding = _baseBinding;
    }

    public void SyncBaseline(HotkeyBinding? newBase)
    {
        _baseBinding = newBase;
        _targetBinding = newBase;
        _enabled = newBase?.Enabled ?? false;
        DraftError = null;
        OnPropertyChanged(nameof(BaseBinding));
        OnPropertyChanged(nameof(TargetBinding));
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(KeyComboText));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanClear));
    }

    public void UpdateBaselineFromExternal(HotkeyBinding? newBase)
    {
        if (!IsDirty)
        {
            SyncBaseline(newBase);
        }
        else
        {
            _baseBinding = newBase;
            OnPropertyChanged(nameof(BaseBinding));
            OnPropertyChanged(nameof(IsDirty));
        }
    }

    public void SetRecordingState(bool isRecording, bool isPreparing, string? prompt, string? previewText)
    {
        IsRecording = isRecording;
        IsPreparing = isPreparing;
        RecordingPrompt = prompt;
        _previewText = previewText;
        OnPropertyChanged(nameof(KeyComboText));
    }

    public void RaiseCanRecordChanged()
    {
        OnPropertyChanged(nameof(CanRecord));
        OnPropertyChanged(nameof(CanClear));
        (StartRecordCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ClearCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static bool AreBindingsEqual(HotkeyBinding? a, HotkeyBinding? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return a.Enabled == b.Enabled
            && a.Modifiers == b.Modifiers
            && string.Equals(a.Key?.Trim(), b.Key?.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
