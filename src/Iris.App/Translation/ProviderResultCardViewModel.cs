using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Iris.App.Settings;

namespace Iris.App.Translation;

internal enum CardState
{
    Idle,
    Streaming,
    Completed,
    Failed,
    Canceled,
}

/// <summary>
/// 单个翻译服务卡片的 ViewModel。
/// 绑定唯一键为 (OperationId, ProviderId, RequestId)，负责正文显示、截断标记与单卡重试。
/// 规则见 TRANSLATION_WINDOW.md §4 与 §5。
/// </summary>
internal sealed class ProviderResultCardViewModel : INotifyPropertyChanged
{
    private readonly Func<ProviderResultCardViewModel, bool> _canRetryPredicate;
    private Guid _requestId;
    private string _text = string.Empty;
    private CardState _state = CardState.Idle;
    private TimeSpan? _elapsed;
    private bool _fromCache;
    private bool _isModelTruncated;
    private bool _isLocallyTruncated;
    private string? _errorMessage;
    private bool _showGoToSettings;

    public ProviderResultCardViewModel(
        Guid operationId,
        string providerId,
        string displayName,
        string? model,
        Guid requestId,
        Action<ProviderResultCardViewModel> onRetry,
        Action<ProviderResultCardViewModel> onCopy,
        Action<ProviderResultCardViewModel> onGoToSettings,
        Func<ProviderResultCardViewModel, bool> canRetryPredicate)
    {
        OperationId = operationId;
        ProviderId = providerId ?? throw new ArgumentNullException(nameof(providerId));
        DisplayName = displayName ?? providerId;
        Model = model;
        _requestId = requestId;
        _canRetryPredicate = canRetryPredicate ?? throw new ArgumentNullException(nameof(canRetryPredicate));

        RetryCommand = new RelayCommand(() => onRetry(this), () => CanRetry);
        CopyCommand = new RelayCommand(() => onCopy(this), () => CanCopy);
        GoToSettingsCommand = new RelayCommand(() => onGoToSettings(this));
    }

    public Guid OperationId { get; }
    public string ProviderId { get; }
    public string DisplayName { get; }
    public string? Model { get; }

    public Guid RequestId
    {
        get => _requestId;
        set => SetField(ref _requestId, value);
    }

    public string Text
    {
        get => _text;
        set
        {
            if (SetField(ref _text, value))
            {
                OnPropertyChanged(nameof(CanCopy));
            }
        }
    }

    public CardState State
    {
        get => _state;
        set
        {
            if (SetField(ref _state, value))
            {
                OnPropertyChanged(nameof(IsTerminal));
                OnPropertyChanged(nameof(CanRetry));
                OnPropertyChanged(nameof(IsStreaming));
            }
        }
    }

    public bool IsStreaming => State == CardState.Streaming;

    public bool IsTerminal => State is CardState.Completed or CardState.Failed or CardState.Canceled;

    public TimeSpan? Elapsed
    {
        get => _elapsed;
        set
        {
            if (SetField(ref _elapsed, value))
            {
                OnPropertyChanged(nameof(ElapsedText));
            }
        }
    }

    public string? ElapsedText
    {
        get
        {
            if (!_elapsed.HasValue) return null;
            var ms = _elapsed.Value.TotalMilliseconds;
            return ms < 1000 ? $"{ms:F0}ms" : $"{_elapsed.Value.TotalSeconds:F1}s";
        }
    }

    public bool FromCache
    {
        get => _fromCache;
        set => SetField(ref _fromCache, value);
    }

    public bool IsModelTruncated
    {
        get => _isModelTruncated;
        set => SetField(ref _isModelTruncated, value);
    }

    public bool IsLocallyTruncated
    {
        get => _isLocallyTruncated;
        set
        {
            if (SetField(ref _isLocallyTruncated, value))
            {
                OnPropertyChanged(nameof(CopyTooltip));
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetField(ref _errorMessage, value);
    }

    public bool ShowGoToSettings
    {
        get => _showGoToSettings;
        set => SetField(ref _showGoToSettings, value);
    }

    public bool CanRetry => IsTerminal && _canRetryPredicate(this);

    public bool CanCopy => !string.IsNullOrEmpty(Text);

    public string CopyTooltip => IsLocallyTruncated ? UiText.TranslateCopyVisible : UiText.TranslateCopy;

    public ICommand RetryCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand GoToSettingsCommand { get; }

    public void AppendText(string delta)
    {
        Text += delta;
    }

    public void SetCompleted(string authoritativeText, TimeSpan elapsed, bool fromCache, bool isModelTruncated, bool isLocallyTruncated)
    {
        Text = authoritativeText;
        Elapsed = elapsed;
        FromCache = fromCache;
        IsModelTruncated = isModelTruncated;
        IsLocallyTruncated = isLocallyTruncated;
        ErrorMessage = null;
        ShowGoToSettings = false;
        State = CardState.Completed;
    }

    public void SetFailed(string safeMessage, bool showSettings, TimeSpan elapsed)
    {
        ErrorMessage = safeMessage;
        ShowGoToSettings = showSettings;
        Elapsed = elapsed;
        State = CardState.Failed;
    }

    public void SetCanceled()
    {
        State = CardState.Canceled;
    }

    public void ResetForRetry(Guid newRequestId)
    {
        RequestId = newRequestId;
        Text = string.Empty;
        Elapsed = null;
        FromCache = false;
        IsModelTruncated = false;
        IsLocallyTruncated = false;
        ErrorMessage = null;
        ShowGoToSettings = false;
        State = CardState.Streaming;
    }

    public void RefreshCanRetry()
    {
        OnPropertyChanged(nameof(CanRetry));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
