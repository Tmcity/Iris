using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.Core.Errors;
using Iris.Core.Languages;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Microsoft.Extensions.Logging;

namespace Iris.App.Translation;

internal sealed record LanguageOption(string Code, string DisplayName);

/// <summary>
/// 翻译窗口主 ViewModel。
/// 规则见 TRANSLATION_WINDOW.md 全文与 ARCH §14。
/// </summary>
internal sealed partial class TranslateWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly ITranslationOrchestrator _orchestrator;
    private readonly IClipboardService _clipboardService;
    private readonly ISettingsWindowPresenter _settingsPresenter;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly ILogger<TranslateWindowViewModel> _logger;
    private readonly ITextPreprocessor _textPreprocessor;
    private readonly TranslationDeltaBuffer _deltaBuffer = new();

    private readonly Lock _gate = new();
    private bool _disposed;
    private bool _programmaticTextUpdate;

    private string _inputText = string.Empty;
    private string _sourceLanguage = LanguageCodes.Auto;
    private string _targetLanguage = LanguageCodes.Auto;
    private string? _resolvedSourceLanguage;
    private string? _resolvedTargetLanguage;

    private string? _statusMessage;
    private ErrorInfo? _error;
    private bool _isTranslating;
    private bool _isPinned;

    private TranslateWindowOperation? _activeOperation;
    private string _sessionOriginalText = string.Empty;
    private TextOrigin _sessionOrigin = TextOrigin.Input;
    private AutoCopyMode _sessionAutoCopyMode = AutoCopyMode.None;
    private int _autoCopyClaimed;

    private Task? _activeMainTask;
    private Task? _activeRetryTask;
    private Task? _mainCoordinatorTask;
    private Task? _retryCoordinatorTask;
    private Task? _flushTask;
    private Task? _settingsTask;
    private Task? _settingsChangedTask;

    private readonly Lock _copyGate = new();
    private Task? _inFlightCopyTask;
    private CancellationTokenSource? _inFlightCopyCts;
    private QueuedCopyItem? _pendingCopy;
    private readonly List<Task> _copyCancelTasks = [];
    private readonly HashSet<CancellationTokenSource> _cancelingCtsSet = [];

    private QueuedMainRequest? _pendingMain;
    private QueuedRetryRequest? _pendingRetry;

    public TranslateWindowViewModel(
        ISettingsService settingsService,
        ITranslationOrchestrator orchestrator,
        IClipboardService clipboardService,
        ISettingsWindowPresenter settingsPresenter,
        IUiDispatcher uiDispatcher,
        ILogger<TranslateWindowViewModel> logger,
        ITextPreprocessor textPreprocessor)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _clipboardService = clipboardService ?? throw new ArgumentNullException(nameof(clipboardService));
        _settingsPresenter = settingsPresenter ?? throw new ArgumentNullException(nameof(settingsPresenter));
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _textPreprocessor = textPreprocessor ?? throw new ArgumentNullException(nameof(textPreprocessor));

        var trSettings = _settingsService.Current.Translation;
        _sourceLanguage = trSettings.DefaultSourceLanguage ?? LanguageCodes.Auto;
        _targetLanguage = trSettings.PrimaryTargetLanguage ?? LanguageCodes.Auto;

        InitializeLanguageLists();

        TranslateCommand = new RelayCommand(ExecuteTranslateFromUser, () => CanTranslate);
        StopCommand = new RelayCommand(ExecuteStop, () => IsTranslating);
        SwapLanguagesCommand = new RelayCommand(ExecuteSwapLanguages, () => CanSwapLanguages);
        PinCommand = new RelayCommand(() => IsPinned = !IsPinned);
        GoToSettingsCommand = new RelayCommand(ExecuteGoToSettings);

        _settingsService.Changed += OnSettingsChanged;
    }

    public ObservableCollection<LanguageOption> AvailableSourceLanguages { get; } = [];
    public ObservableCollection<LanguageOption> AvailableTargetLanguages { get; } = [];
    public ObservableCollection<ProviderResultCardViewModel> Cards { get; } = [];

    public string InputText
    {
        get => _inputText;
        set
        {
            if (SetField(ref _inputText, value))
            {
                OnPropertyChanged(nameof(InputLength));
                OnPropertyChanged(nameof(CanTranslate));

                if (!_programmaticTextUpdate)
                {
                    OnUserInputChanged();
                }
            }
        }
    }

    public int InputLength => _inputText?.Length ?? 0;

    public int MaxInputChars => _settingsService.Current.Translation.MaxInputChars;

    public string SourceLanguage
    {
        get => _sourceLanguage;
        set
        {
            if (SetField(ref _sourceLanguage, value))
            {
                OnPropertyChanged(nameof(CanSwapLanguages));
                if (!_programmaticTextUpdate)
                {
                    OnUserInputChanged();
                }
            }
        }
    }

    public string TargetLanguage
    {
        get => _targetLanguage;
        set
        {
            if (SetField(ref _targetLanguage, value))
            {
                OnPropertyChanged(nameof(CanSwapLanguages));
                if (!_programmaticTextUpdate)
                {
                    OnUserInputChanged();
                }
            }
        }
    }

    public bool CanSwapLanguages
    {
        get
        {
            string effSource = _sourceLanguage;
            string effTarget = _targetLanguage;

            if (effSource == LanguageCodes.Auto)
            {
                if (string.IsNullOrEmpty(_resolvedSourceLanguage) || _resolvedSourceLanguage == LanguageCodes.Auto)
                {
                    return false;
                }
                effSource = _resolvedSourceLanguage;
            }

            if (effTarget == LanguageCodes.Auto)
            {
                if (string.IsNullOrEmpty(_resolvedTargetLanguage) || _resolvedTargetLanguage == LanguageCodes.Auto)
                {
                    return false;
                }
                effTarget = _resolvedTargetLanguage;
            }

            return !string.Equals(effSource, effTarget, StringComparison.OrdinalIgnoreCase);
        }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (SetField(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    public ErrorInfo? Error
    {
        get => _error;
        set
        {
            if (SetField(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(ShowGoToSettingsForError));
            }
        }
    }

    public bool HasError => _error != null;

    public bool ShowGoToSettingsForError => _error?.Code is ErrorCode.NotConfigured or ErrorCode.AuthenticationFailed;

    public bool IsTranslating
    {
        get => _isTranslating;
        private set
        {
            if (SetField(ref _isTranslating, value))
            {
                OnPropertyChanged(nameof(CanTranslate));
                if (TranslateCommand is RelayCommand tc)
                {
                    tc.RaiseCanExecuteChanged();
                }
                if (StopCommand is RelayCommand sc)
                {
                    sc.RaiseCanExecuteChanged();
                }
                foreach (var card in Cards)
                {
                    card.RefreshCanRetry();
                }
            }
        }
    }

    internal static ErrorInfo SanitizeErrorInfo(ErrorInfo error)
    {
        string safeMessage = error.Code switch
        {
            ErrorCode.InputEmpty => UiText.TranslateEmptyPrompt,
            ErrorCode.InputTooLong => UiText.TranslateInputTooLong,
            ErrorCode.NotConfigured => UiText.TranslateNoConfiguredProviders,
            _ => ErrorMessages.GetDefault(error.Code)
        };

        return new ErrorInfo(
            error.Code,
            safeMessage,
            Detail: null,
            HttpStatus: error.HttpStatus,
            Retryable: error.Retryable,
            ProviderId: error.ProviderId);
    }

    public bool CanTranslate => !string.IsNullOrWhiteSpace(_inputText) && !_disposed;

    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (SetField(ref _isPinned, value))
            {
                OnPropertyChanged(nameof(Topmost));
            }
        }
    }

    public bool Topmost => _settingsService.Current.TranslateWindow.Topmost || _isPinned;

    public double WindowWidth => _settingsService.Current.TranslateWindow.Width;

    public double FontSize => _settingsService.Current.TranslateWindow.FontSize;

    public bool EnterToTranslate => _settingsService.Current.TranslateWindow.EnterToTranslate;

    public bool HideOnDeactivate => _settingsService.Current.TranslateWindow.HideOnDeactivate;

    public ICommand TranslateCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand SwapLanguagesCommand { get; }
    public ICommand PinCommand { get; }
    public ICommand GoToSettingsCommand { get; }

    internal TranslateWindowOperation? ActiveOperation => _activeOperation;

    private void InitializeLanguageLists()
    {
        var all = new (string Code, string Name)[]
        {
            (LanguageCodes.ChineseSimplified, "简体中文"),
            (LanguageCodes.ChineseTraditional, "繁体中文"),
            (LanguageCodes.English, "英语"),
            (LanguageCodes.Japanese, "日语"),
            (LanguageCodes.Korean, "韩语"),
            (LanguageCodes.French, "法语"),
            (LanguageCodes.German, "德语"),
            (LanguageCodes.Spanish, "西班牙语"),
            (LanguageCodes.Russian, "俄语"),
            (LanguageCodes.Portuguese, "葡萄牙语"),
            (LanguageCodes.Italian, "意大利语"),
            (LanguageCodes.Vietnamese, "越南语"),
            (LanguageCodes.Thai, "泰语"),
            (LanguageCodes.Arabic, "阿拉伯语"),
        };

        AvailableSourceLanguages.Add(new LanguageOption(LanguageCodes.Auto, "自动检测"));
        foreach (var (code, name) in all)
        {
            AvailableSourceLanguages.Add(new LanguageOption(code, name));
        }

        AvailableTargetLanguages.Add(new LanguageOption(LanguageCodes.Auto, "自动 (按设置)"));
        foreach (var (code, name) in all)
        {
            AvailableTargetLanguages.Add(new LanguageOption(code, name));
        }
    }

    internal void ApplyRequest(TranslateWindowRequest request, TranslateWindowOperation operation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(operation);

        _programmaticTextUpdate = true;
        try
        {
            if (request.Text != null)
            {
                _inputText = request.Text;
                OnPropertyChanged(nameof(InputText));
                OnPropertyChanged(nameof(InputLength));
                OnPropertyChanged(nameof(CanTranslate));
            }

            if (_activeOperation != null && !ReferenceEquals(_activeOperation, operation))
            {
                _activeOperation.Cancel();
                _activeOperation.DetachSession();
            }

            _activeOperation = operation;
            operation.AttachSession();

            StatusMessage = request.StatusMessage;
            Error = request.Error != null ? SanitizeErrorInfo(request.Error) : null;

            bool isNewMain = request.AutoTranslate
                && !string.IsNullOrWhiteSpace(request.Text)
                && request.Error == null
                && string.IsNullOrWhiteSpace(request.StatusMessage)
                && operation.TryClaimAutoTranslate();

            if (isNewMain)
            {
                _sessionOriginalText = _inputText;
                _sessionOrigin = request.Origin;
                _sessionAutoCopyMode = _settingsService.Current.Translation.AutoCopy;
                Interlocked.Exchange(ref _autoCopyClaimed, 0);

                SubmitMainTranslation(operation, _inputText, request.Origin);
            }
        }
        finally
        {
            _programmaticTextUpdate = false;
        }
    }

    internal void RevokeOperation(Guid operationId)
    {
        lock (_gate)
        {
            if (_activeOperation != null && _activeOperation.Id == operationId)
            {
                _pendingMain = null;
                _pendingRetry = null;
                _sessionAutoCopyMode = AutoCopyMode.None;
                Interlocked.Exchange(ref _autoCopyClaimed, 1);
                _activeOperation.Cancel();
                _activeOperation.DetachSession();
                _activeOperation = null;
                IsTranslating = false;
            }
        }
        CancelPendingAndActiveCopies();
    }

    internal void OnWindowHidden()
    {
        TranslateWindowOperation? opToCancel = null;
        Guid? currentOpId = null;
        lock (_gate)
        {
            _pendingMain = null;
            _pendingRetry = null;
            opToCancel = _activeOperation;
            currentOpId = _activeOperation?.Id;
            _activeOperation = null;
        }

        opToCancel?.Cancel();
        opToCancel?.DetachSession();
        CancelPendingAndActiveCopies();
        _deltaBuffer.Reset();

        IsTranslating = false;
        foreach (var card in Cards.Where(c => c.IsStreaming))
        {
            card.SetCanceled();
        }
    }

    private void OnUserInputChanged()
    {
        TranslateWindowOperation? opToCancel;
        lock (_gate)
        {
            opToCancel = _activeOperation;
            _pendingMain = null;
            _pendingRetry = null;
        }

        opToCancel?.Cancel();

        StatusMessage = null;
        Error = null;

        foreach (var card in Cards)
        {
            card.RefreshCanRetry();
        }
    }

    public void ExecuteTranslateFromUser()
    {
        string prepared = _textPreprocessor.Prepare(_inputText, TextOrigin.Input);
        if (string.IsNullOrWhiteSpace(prepared))
        {
            Error = new ErrorInfo(ErrorCode.InputEmpty, UiText.TranslateEmptyPrompt);
            return;
        }

        if (prepared.Length > MaxInputChars)
        {
            Error = new ErrorInfo(ErrorCode.InputTooLong, UiText.TranslateInputTooLong);
            return;
        }

        var opObj = _presenterRef?.TryBeginOperation();
        if (opObj == null)
        {
            StatusMessage = UiText.TranslateBusy;
            return;
        }

        var op = (TranslateWindowOperation)opObj;
        if (_activeOperation != null && !ReferenceEquals(_activeOperation, op))
        {
            _activeOperation.Cancel();
            _activeOperation.DetachSession();
        }

        _activeOperation = op;
        op.AttachSession();
        op.Dispose(); // Releases caller ownership so VM session owns it

        _sessionOriginalText = _inputText;
        _sessionOrigin = TextOrigin.Input;
        _sessionAutoCopyMode = _settingsService.Current.Translation.AutoCopy;
        Interlocked.Exchange(ref _autoCopyClaimed, 0);
        StatusMessage = null;
        Error = null;

        SubmitMainTranslation(op, _inputText, TextOrigin.Input);
    }

    private void ExecuteStop()
    {
        TranslateWindowOperation? opToCancel;
        lock (_gate)
        {
            opToCancel = _activeOperation;
            _pendingMain = null;
            _pendingRetry = null;
        }

        opToCancel?.Cancel();
        _deltaBuffer.Reset();
        StatusMessage = UiText.TranslateStopped;
        IsTranslating = false;

        foreach (var card in Cards)
        {
            if (card.IsStreaming)
            {
                card.SetCanceled();
            }
            card.RefreshCanRetry();
        }
    }

    private void ExecuteSwapLanguages()
    {
        string newTarget = _sourceLanguage == LanguageCodes.Auto ? (_resolvedSourceLanguage ?? LanguageCodes.English) : _sourceLanguage;
        string newSource = _targetLanguage == LanguageCodes.Auto ? (_resolvedTargetLanguage ?? LanguageCodes.Auto) : _targetLanguage;

        _programmaticTextUpdate = true;
        try
        {
            SourceLanguage = newSource;
            TargetLanguage = newTarget;
            _resolvedSourceLanguage = null;
            _resolvedTargetLanguage = null;
        }
        finally
        {
            _programmaticTextUpdate = false;
        }

        OnUserInputChanged();
    }

    private void SubmitMainTranslation(TranslateWindowOperation operation, string text, TextOrigin origin)
    {
        string prepared = _textPreprocessor.Prepare(text, origin);
        if (prepared.Length > MaxInputChars)
        {
            Error = new ErrorInfo(ErrorCode.InputTooLong, UiText.TranslateInputTooLong);
            return;
        }

        IsTranslating = true;
        Cards.Clear();
        StatusMessage = null;
        Error = null;

        var req = new TranslationRequest
        {
            RequestId = Guid.NewGuid(),
            Text = text,
            SourceLanguage = _sourceLanguage,
            TargetLanguage = _targetLanguage == LanguageCodes.Auto ? null : _targetLanguage,
            Origin = origin,
            BypassCache = false,
            RecordHistory = true,
        };

        lock (_gate)
        {
            _pendingMain = new QueuedMainRequest(operation, req);
            _pendingRetry = null;
            if (_mainCoordinatorTask == null || _mainCoordinatorTask.IsCompleted)
            {
                _mainCoordinatorTask = Task.Run(CoordinateMainQueueAsync, CancellationToken.None);
            }
        }
    }

    private async Task CoordinateMainQueueAsync()
    {
        while (true)
        {
            Task? waitMain;
            Task? waitRetry;
            lock (_gate)
            {
                waitMain = _activeMainTask;
                waitRetry = _activeRetryTask;
            }

            if (waitMain != null)
            {
                try { await waitMain.ConfigureAwait(false); } catch { }
            }

            if (waitRetry != null)
            {
                try { await waitRetry.ConfigureAwait(false); } catch { }
            }

            Task? currentTask = null;
            lock (_gate)
            {
                var current = _pendingMain;
                _pendingMain = null;

                if (current == null)
                {
                    _mainCoordinatorTask = null;
                    return;
                }

                if (current.Operation.IsRetired || current.Operation.CancellationToken.IsCancellationRequested)
                {
                    _ = _uiDispatcher.InvokeAsync(() =>
                    {
                        if (_activeOperation != null && _activeOperation.Id == current.Operation.Id)
                        {
                            IsTranslating = false;
                        }
                        return Task.CompletedTask;
                    }, CancellationToken.None);
                    continue;
                }

                currentTask = RunMainTranslationAsync(current.Operation, current.Request);
                _activeMainTask = currentTask;
            }

            if (currentTask != null)
            {
                try
                {
                    await currentTask.ConfigureAwait(false);
                }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_activeMainTask, currentTask))
                        {
                            _activeMainTask = null;
                        }
                    }
                }
            }
        }
    }

    private async Task RunMainTranslationAsync(TranslateWindowOperation operation, TranslationRequest request)
    {
        if (!operation.IncrementWork())
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (_activeOperation != null && _activeOperation.Id == operation.Id)
                {
                    IsTranslating = false;
                }
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        IAsyncEnumerator<TranslationEvent>? enumerator = null;
        try
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (_activeOperation == null || _activeOperation.Id != operation.Id || operation.CancellationToken.IsCancellationRequested)
                {
                    return Task.CompletedTask;
                }
                IsTranslating = true;
                Cards.Clear();
                StatusMessage = null;
                Error = null;
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);

            var token = operation.CancellationToken;
            try
            {
                var stream = _orchestrator.TranslateAsync(request, token);
                enumerator = stream.GetAsyncEnumerator(token);

                while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    if (token.IsCancellationRequested || _activeOperation == null || _activeOperation.Id != operation.Id)
                    {
                        break;
                    }
                    var evt = enumerator.Current;
                    await HandleTranslationEventAsync(operation, evt).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                await _uiDispatcher.InvokeAsync(() =>
                {
                    if (_activeOperation != null && _activeOperation.Id == operation.Id)
                    {
                        foreach (var card in Cards.Where(c => c.IsStreaming))
                        {
                            card.SetCanceled();
                        }
                    }
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (IrisException ex)
            {
                await _uiDispatcher.InvokeAsync(() =>
                {
                    if (_activeOperation != null && _activeOperation.Id == operation.Id && !operation.CancellationToken.IsCancellationRequested)
                    {
                        Error = SanitizeErrorInfo(ex.ToErrorInfo());
                    }
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogTranslationUnexpectedError(_logger, ex.GetType().Name);
                await _uiDispatcher.InvokeAsync(() =>
                {
                    if (_activeOperation != null && _activeOperation.Id == operation.Id && !operation.CancellationToken.IsCancellationRequested)
                    {
                        Error = new ErrorInfo(ErrorCode.Unknown, UiText.ErrorUnknown);
                    }
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            if (enumerator != null)
            {
                try
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogTranslationUnexpectedError(_logger, ex.GetType().Name);
                }
            }

            try
            {
                await _uiDispatcher.InvokeAsync(() =>
                {
                    FlushDeltaBufferToUi();
                    if (_activeOperation != null && _activeOperation.Id == operation.Id)
                    {
                        IsTranslating = false;
                    }
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogTranslationUnexpectedError(_logger, ex.GetType().Name);
            }
            finally
            {
                operation.DecrementWork();
            }
        }
    }

    private async Task HandleTranslationEventAsync(TranslateWindowOperation operation, TranslationEvent evt, bool isRetry = false)
    {
        if (_activeOperation == null || _activeOperation.Id != operation.Id || operation.CancellationToken.IsCancellationRequested)
        {
            return;
        }

        switch (evt)
        {
            case TranslationStarted started:
                _resolvedSourceLanguage = started.Languages.Source;
                _resolvedTargetLanguage = started.Languages.Target;

                await _uiDispatcher.InvokeAsync(() =>
                {
                    if (_activeOperation == null || _activeOperation.Id != operation.Id || operation.CancellationToken.IsCancellationRequested)
                    {
                        return Task.CompletedTask;
                    }

                    OnPropertyChanged(nameof(CanSwapLanguages));
                    if (!isRetry)
                    {
                        Cards.Clear();
                        foreach (var p in started.Providers)
                        {
                            var card = new ProviderResultCardViewModel(
                                operation.Id,
                                p.Id,
                                p.DisplayName,
                                p.Model,
                                started.RequestId,
                                OnCardRetry,
                                OnCardCopy,
                                OnCardGoToSettings,
                                _ => CanRetryCard(operation));
                            Cards.Add(card);
                        }
                    }
                    else
                    {
                        foreach (var p in started.Providers)
                        {
                            var card = Cards.FirstOrDefault(c => c.OperationId == operation.Id && c.ProviderId == p.Id);
                            if (card != null)
                            {
                                card.UpdateDescriptor(p.DisplayName, p.Model);
                            }
                        }
                    }
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
                break;

            case ProviderStarted ps:
                await _uiDispatcher.InvokeAsync(() =>
                {
                    if (_activeOperation == null || _activeOperation.Id != operation.Id || operation.CancellationToken.IsCancellationRequested)
                    {
                        return Task.CompletedTask;
                    }

                    var card = FindCard(operation.Id, ps.ProviderId, ps.RequestId);
                    if (card != null && !card.IsTerminal)
                    {
                        card.State = CardState.Streaming;
                    }
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
                break;

            case ProviderDelta pd:
                if (_activeOperation != null && _activeOperation.Id == operation.Id && !operation.CancellationToken.IsCancellationRequested)
                {
                    await _deltaBuffer.EnqueueAsync(new QueuedDelta(operation.Id, pd.ProviderId, pd.RequestId, pd.Delta), operation.CancellationToken).ConfigureAwait(false);
                    ScheduleUiFlush();
                }
                break;

            case ProviderCompleted pc:
                _deltaBuffer.DiscardForCard(operation.Id, pc.ProviderId, pc.RequestId);
                await _uiDispatcher.InvokeAsync(() =>
                {
                    if (_activeOperation == null || _activeOperation.Id != operation.Id || operation.CancellationToken.IsCancellationRequested)
                    {
                        return Task.CompletedTask;
                    }

                    var card = FindCard(operation.Id, pc.ProviderId, pc.RequestId);
                    if (card != null)
                    {
                        int currentTotal = Cards.Where(c => !ReferenceEquals(c, card)).Sum(c => c.Text.Length);
                        int remainingBudget = Math.Max(0, TextBudgetManager.MaxTotalChars - currentTotal);
                        int allowedForCard = Math.Min(TextBudgetManager.MaxCardChars, remainingBudget);

                        bool locallyTruncated = pc.Text.Length > allowedForCard;
                        string safeText = locallyTruncated
                            ? TextBudgetManager.TruncateSurrogateSafe(pc.Text, allowedForCard)
                            : pc.Text;

                        card.SetCompleted(safeText, pc.Elapsed, pc.FromCache, pc.Truncated, locallyTruncated);

                        // 自动复制：仅初始主请求首个有效完整（非截断）结果参与一次，重试不参与
                        if (!isRetry && !locallyTruncated && !pc.Truncated && _sessionAutoCopyMode == AutoCopyMode.FirstResult)
                        {
                            if (Interlocked.CompareExchange(ref _autoCopyClaimed, 1, 0) == 0)
                            {
                                TrackCopy(safeText, operation.CancellationToken);
                            }
                        }
                    }
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
                break;

            case ProviderFailed pf:
                _deltaBuffer.DiscardForCard(operation.Id, pf.ProviderId, pf.RequestId);
                await _uiDispatcher.InvokeAsync(() =>
                {
                    if (_activeOperation == null || _activeOperation.Id != operation.Id || operation.CancellationToken.IsCancellationRequested)
                    {
                        return Task.CompletedTask;
                    }

                    var card = FindCard(operation.Id, pf.ProviderId, pf.RequestId);
                    if (card != null)
                    {
                        bool showSettings = pf.Error.Code is ErrorCode.NotConfigured or ErrorCode.AuthenticationFailed;
                        string safeMsg = ErrorMessages.GetDefault(pf.Error.Code);
                        card.SetFailed(safeMsg, showSettings, pf.Elapsed);
                    }
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
                break;

            case TranslationFinished:
                break;
        }
    }

    private void ScheduleUiFlush()
    {
        lock (_gate)
        {
            if (_flushTask != null && !_flushTask.IsCompleted)
            {
                return;
            }

            _flushTask = Task.Run(async () =>
            {
                await Task.Delay(33).ConfigureAwait(false);
                await _uiDispatcher.InvokeAsync(() =>
                {
                    FlushDeltaBufferToUi();
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
            }, CancellationToken.None);
        }
    }

    private void FlushDeltaBufferToUi()
    {
        var deltas = _deltaBuffer.Drain();
        if (deltas.Count == 0)
        {
            return;
        }

        var groups = deltas.GroupBy(d => (d.OperationId, d.ProviderId, d.RequestId));
        foreach (var group in groups)
        {
            var card = FindCard(group.Key.OperationId, group.Key.ProviderId, group.Key.RequestId);
            if (card == null || card.IsTerminal || card.IsLocallyTruncated)
            {
                continue;
            }

            var sb = new StringBuilder();
            foreach (var d in group)
            {
                sb.Append(d.Delta);
            }
            string combinedDelta = sb.ToString();

            int currentTotal = Cards.Sum(c => c.Text.Length);
            int remainingTotal = Math.Max(0, TextBudgetManager.MaxTotalChars - currentTotal);
            int allowedForCard = Math.Min(TextBudgetManager.MaxCardChars - card.Text.Length, remainingTotal);

            if (allowedForCard <= 0)
            {
                card.IsLocallyTruncated = true;
                continue;
            }

            if (combinedDelta.Length > allowedForCard)
            {
                string safe = TextBudgetManager.TruncateSurrogateSafe(combinedDelta, allowedForCard);
                card.AppendText(safe);
                card.IsLocallyTruncated = true;
            }
            else
            {
                card.AppendText(combinedDelta);
            }
        }
    }

    private void OnCardRetry(ProviderResultCardViewModel card)
    {
        if (_activeOperation == null || _activeOperation.IsRetired || _activeOperation.CancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (card.OperationId != _activeOperation.Id || !card.IsTerminal)
        {
            return;
        }

        lock (_gate)
        {
            if (_pendingRetry?.Card.ProviderId == card.ProviderId)
            {
                return; // 同卡正在等待，忽略
            }

            if (_pendingRetry != null && _pendingRetry.Card.ProviderId != card.ProviderId)
            {
                _pendingRetry.Card.SetCanceled();
            }

            var newReqId = Guid.NewGuid();
            card.ResetForRetry(newReqId);

            var retryReq = new TranslationRequest
            {
                RequestId = newReqId,
                Text = _sessionOriginalText,
                SourceLanguage = _resolvedSourceLanguage ?? _sourceLanguage,
                TargetLanguage = _resolvedTargetLanguage ?? (_targetLanguage == LanguageCodes.Auto ? null : _targetLanguage),
                ProviderIds = [card.ProviderId],
                Origin = _sessionOrigin,
                BypassCache = true,
                RecordHistory = false,
            };

            _pendingRetry = new QueuedRetryRequest(_activeOperation, card, retryReq);
            if (_retryCoordinatorTask == null || _retryCoordinatorTask.IsCompleted)
            {
                _retryCoordinatorTask = Task.Run(CoordinateRetryQueueAsync, CancellationToken.None);
            }
        }
    }

    private async Task CoordinateRetryQueueAsync()
    {
        while (true)
        {
            Task? waitRetry;
            lock (_gate)
            {
                waitRetry = _activeRetryTask;
            }

            if (waitRetry != null)
            {
                try { await waitRetry.ConfigureAwait(false); } catch { }
            }

            Task? currentTask = null;
            lock (_gate)
            {
                var current = _pendingRetry;
                _pendingRetry = null;

                if (current == null)
                {
                    _retryCoordinatorTask = null;
                    return;
                }

                if (current.Operation.IsRetired || current.Operation.CancellationToken.IsCancellationRequested)
                {
                    continue;
                }

                currentTask = RunRetryTranslationAsync(current.Operation, current.Card, current.Request);
                _activeRetryTask = currentTask;
            }

            if (currentTask != null)
            {
                try
                {
                    await currentTask.ConfigureAwait(false);
                }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_activeRetryTask, currentTask))
                        {
                            _activeRetryTask = null;
                        }
                    }
                }
            }
        }
    }

    private async Task RunRetryTranslationAsync(TranslateWindowOperation operation, ProviderResultCardViewModel card, TranslationRequest request)
    {
        if (!operation.IncrementWork())
        {
            return;
        }

        IAsyncEnumerator<TranslationEvent>? enumerator = null;
        try
        {
            var token = operation.CancellationToken;
            try
            {
                var stream = _orchestrator.TranslateAsync(request, token);
                enumerator = stream.GetAsyncEnumerator(token);

                while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    if (token.IsCancellationRequested || _activeOperation == null || _activeOperation.Id != operation.Id)
                    {
                        break;
                    }
                    var evt = enumerator.Current;
                    await HandleTranslationEventAsync(operation, evt, isRetry: true).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                await _uiDispatcher.InvokeAsync(() =>
                {
                    if (card.RequestId == request.RequestId)
                    {
                        card.SetCanceled();
                    }
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (IrisException ex)
            {
                await _uiDispatcher.InvokeAsync(() =>
                {
                    if (card.RequestId == request.RequestId)
                    {
                        string safeMsg = ErrorMessages.GetDefault(ex.Code);
                        card.SetFailed(safeMsg, ex.Code is ErrorCode.NotConfigured or ErrorCode.AuthenticationFailed, TimeSpan.Zero);
                    }
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogRetryUnexpectedError(_logger, ex.GetType().Name);
                await _uiDispatcher.InvokeAsync(() =>
                {
                    if (card.RequestId == request.RequestId)
                    {
                        card.SetFailed(UiText.ErrorUnknown, false, TimeSpan.Zero);
                    }
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            if (enumerator != null)
            {
                try
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogRetryUnexpectedError(_logger, ex.GetType().Name);
                }
            }

            try
            {
                await _uiDispatcher.InvokeAsync(() =>
                {
                    FlushDeltaBufferToUi();
                    card.RefreshCanRetry();
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogRetryUnexpectedError(_logger, ex.GetType().Name);
            }
            finally
            {
                operation.DecrementWork();
            }
        }
    }

    private bool CanRetryCard(TranslateWindowOperation operation)
    {
        return _activeOperation != null
            && _activeOperation.Id == operation.Id
            && !operation.IsRetired
            && !operation.CancellationToken.IsCancellationRequested
            && !_disposed;
    }

    private void OnCardCopy(ProviderResultCardViewModel card)
    {
        if (!string.IsNullOrEmpty(card.Text))
        {
            TrackCopy(card.Text, CancellationToken.None);
        }
    }

    private void ExecuteGoToSettings()
    {
        OpenSettingsAsync("translation");
    }

    private void OnCardGoToSettings(ProviderResultCardViewModel card)
    {
        OpenSettingsAsync("translation");
    }

    private void OpenSettingsAsync(string? section = null)
    {
        lock (_gate)
        {
            _settingsTask = Task.Run(async () =>
            {
                try
                {
                    await _settingsPresenter.ShowAsync(section).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogGoToSettingsError(_logger, ex.GetType().Name);
                }
            }, CancellationToken.None);
        }
    }

    private sealed record QueuedCopyItem(string Text, CancellationTokenSource Cts);

    private void RegisterCopyCancelTask(CancellationTokenSource cts)
    {
        lock (_copyGate)
        {
            if (!_cancelingCtsSet.Add(cts))
            {
                return;
            }
        }

        Task cancelTask;
        try
        {
            cancelTask = cts.CancelAsync();
        }
        catch (Exception ex)
        {
            LogClipboardError(_logger, ex.GetType().Name);
            try { cts.Dispose(); } catch { }
            lock (_copyGate)
            {
                _cancelingCtsSet.Remove(cts);
            }
            return;
        }

        Task? cleanupTask = null;
        cleanupTask = AwaitCancelAndDisposeCtsAsync(cts, cancelTask, () =>
        {
            lock (_copyGate)
            {
                _cancelingCtsSet.Remove(cts);
                if (cleanupTask != null)
                {
                    _copyCancelTasks.Remove(cleanupTask);
                }
                _copyCancelTasks.RemoveAll(t => t.IsCompleted);
            }
        });

        lock (_copyGate)
        {
            _copyCancelTasks.RemoveAll(t => t.IsCompleted);
            _copyCancelTasks.Add(cleanupTask);
        }
    }

    private async Task AwaitCancelAndDisposeCtsAsync(CancellationTokenSource cts, Task cancelTask, Action onComplete)
    {
        try
        {
            await cancelTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogClipboardError(_logger, ex.GetType().Name);
        }
        finally
        {
            try
            {
                cts.Dispose();
            }
            catch
            {
            }
            onComplete();
        }
    }

    private void TrackCopy(string text, CancellationToken token)
    {
        CancellationTokenSource cts;
        if (token.CanBeCanceled)
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        }
        else
        {
            cts = new CancellationTokenSource();
        }

        CancellationTokenSource? oldCts = null;
        lock (_copyGate)
        {
            if (_pendingCopy != null)
            {
                oldCts = _pendingCopy.Cts;
            }

            _pendingCopy = new QueuedCopyItem(text, cts);

            if (_inFlightCopyTask == null || _inFlightCopyTask.IsCompleted)
            {
                _inFlightCopyTask = Task.Run(ProcessCopyQueueAsync, CancellationToken.None);
            }
        }

        if (oldCts != null)
        {
            RegisterCopyCancelTask(oldCts);
        }
    }

    private async Task ProcessCopyQueueAsync()
    {
        while (true)
        {
            QueuedCopyItem? current;
            lock (_copyGate)
            {
                current = _pendingCopy;
                _pendingCopy = null;
                if (current == null)
                {
                    _inFlightCopyTask = null;
                    return;
                }
                _inFlightCopyCts = current.Cts;
            }

            try
            {
                if (!current.Cts.IsCancellationRequested)
                {
                    await _clipboardService.SetTextAsync(current.Text, current.Cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LogClipboardError(_logger, ex.GetType().Name);
            }
            finally
            {
                bool wasTrackedForCancel;
                lock (_copyGate)
                {
                    if (ReferenceEquals(_inFlightCopyCts, current.Cts))
                    {
                        _inFlightCopyCts = null;
                    }
                    wasTrackedForCancel = _cancelingCtsSet.Contains(current.Cts);
                    if (!wasTrackedForCancel && current.Cts.IsCancellationRequested)
                    {
                        wasTrackedForCancel = true;
                    }
                }

                if (wasTrackedForCancel)
                {
                    RegisterCopyCancelTask(current.Cts);
                }
                else
                {
                    try { current.Cts.Dispose(); } catch { }
                }
            }
        }
    }

    private void CancelPendingAndActiveCopies()
    {
        CancellationTokenSource? toCancel1 = null;
        CancellationTokenSource? toCancel2 = null;

        lock (_copyGate)
        {
            if (_pendingCopy != null)
            {
                toCancel1 = _pendingCopy.Cts;
                _pendingCopy = null;
            }

            if (_inFlightCopyCts != null)
            {
                toCancel2 = _inFlightCopyCts;
            }
        }

        if (toCancel1 != null)
        {
            RegisterCopyCancelTask(toCancel1);
        }

        if (toCancel2 != null)
        {
            RegisterCopyCancelTask(toCancel2);
        }
    }

    private ProviderResultCardViewModel? FindCard(Guid opId, string providerId, Guid reqId)
    {
        return Cards.FirstOrDefault(c => c.OperationId == opId && c.ProviderId == providerId && c.RequestId == reqId);
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        lock (_gate)
        {
            _settingsChangedTask = _uiDispatcher.InvokeAsync(() =>
            {
                if (_disposed) return Task.CompletedTask;
                OnPropertyChanged(nameof(Topmost));
                OnPropertyChanged(nameof(WindowWidth));
                OnPropertyChanged(nameof(FontSize));
                OnPropertyChanged(nameof(EnterToTranslate));
                OnPropertyChanged(nameof(HideOnDeactivate));
                OnPropertyChanged(nameof(MaxInputChars));
                return Task.CompletedTask;
            }, CancellationToken.None);
        }
    }

    private TranslateWindowPresenter? _presenterRef;
    internal void AttachPresenter(TranslateWindowPresenter presenter) => _presenterRef = presenter;

    public async Task WaitForActiveWorkAsync()
    {
        while (true)
        {
            Task? mainCoord;
            Task? retryCoord;
            Task? main;
            Task? retry;
            Task? flush;
            Task? copy;
            Task? settings;
            Task? settingsChanged;

            lock (_gate)
            {
                mainCoord = _mainCoordinatorTask;
                retryCoord = _retryCoordinatorTask;
                main = _activeMainTask;
                retry = _activeRetryTask;
                flush = _flushTask;
                settings = _settingsTask;
                settingsChanged = _settingsChangedTask;
            }

            List<Task> copyCancels;
            lock (_copyGate)
            {
                copy = _inFlightCopyTask;
                copyCancels = _copyCancelTasks.ToList();
            }

            if (mainCoord != null)
            {
                try { await mainCoord.ConfigureAwait(false); } catch { }
            }

            if (retryCoord != null)
            {
                try { await retryCoord.ConfigureAwait(false); } catch { }
            }

            if (main != null)
            {
                try { await main.ConfigureAwait(false); } catch { }
            }

            if (retry != null)
            {
                try { await retry.ConfigureAwait(false); } catch { }
            }

            if (flush != null)
            {
                try { await flush.ConfigureAwait(false); } catch { }
            }

            if (copy != null)
            {
                try { await copy.ConfigureAwait(false); } catch { }
            }

            foreach (var ct in copyCancels)
            {
                try { await ct.ConfigureAwait(false); } catch { }
            }

            if (settings != null)
            {
                try { await settings.ConfigureAwait(false); } catch { }
            }

            if (settingsChanged != null)
            {
                try { await settingsChanged.ConfigureAwait(false); } catch { }
            }

            bool allCompleted;
            lock (_gate)
            {
                allCompleted = (_mainCoordinatorTask == null || _mainCoordinatorTask.IsCompleted) &&
                               (_retryCoordinatorTask == null || _retryCoordinatorTask.IsCompleted) &&
                               (_activeMainTask == null || _activeMainTask.IsCompleted) &&
                               (_activeRetryTask == null || _activeRetryTask.IsCompleted) &&
                               (_flushTask == null || _flushTask.IsCompleted) &&
                               (_settingsTask == null || _settingsTask.IsCompleted) &&
                               (_settingsChangedTask == null || _settingsChangedTask.IsCompleted);
            }

            lock (_copyGate)
            {
                _copyCancelTasks.RemoveAll(t => t.IsCompleted);
                allCompleted &= (_inFlightCopyTask == null || _inFlightCopyTask.IsCompleted)
                             && _pendingCopy == null
                             && _copyCancelTasks.Count == 0;
            }

            if (allCompleted)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        TranslateWindowOperation? opToCancel;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            opToCancel = _activeOperation;
            _activeOperation = null;
        }

        opToCancel?.Cancel();
        opToCancel?.DetachSession();
        CancelPendingAndActiveCopies();

        _settingsService.Changed -= OnSettingsChanged;
        _deltaBuffer.Dispose();
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

    private sealed record QueuedMainRequest(TranslateWindowOperation Operation, TranslationRequest Request);
    private sealed record QueuedRetryRequest(TranslateWindowOperation Operation, ProviderResultCardViewModel Card, TranslationRequest Request);

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "剪贴板复制失败：{ErrorType}")]
    private static partial void LogClipboardError(ILogger logger, string errorType);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "翻译过程发生未预期异常：{ExceptionType}")]
    private static partial void LogTranslationUnexpectedError(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "重试翻译过程发生未预期异常：{ExceptionType}")]
    private static partial void LogRetryUnexpectedError(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "打开设置界面失败：{ErrorType}")]
    private static partial void LogGoToSettingsError(ILogger logger, string errorType);
}
