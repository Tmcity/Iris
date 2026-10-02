using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
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
    private readonly TranslationDeltaBuffer _deltaBuffer = new();

    private readonly Lock _gate = new();
    private bool _disposed;
    private bool _isStopped;
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
    private int _autoCopyClaimed;

    private Task? _activeMainTask;
    private Task? _activeRetryTask;
    private Task? _processMainQueueTask;
    private Task? _processRetryQueueTask;
    private Task? _flushTask;
    private readonly List<Task> _copyTasks = [];

    private QueuedMainRequest? _pendingMain;
    private QueuedRetryRequest? _pendingRetry;

    public TranslateWindowViewModel(
        ISettingsService settingsService,
        ITranslationOrchestrator orchestrator,
        IClipboardService clipboardService,
        ISettingsWindowPresenter settingsPresenter,
        IUiDispatcher uiDispatcher,
        ILogger<TranslateWindowViewModel> logger)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _clipboardService = clipboardService ?? throw new ArgumentNullException(nameof(clipboardService));
        _settingsPresenter = settingsPresenter ?? throw new ArgumentNullException(nameof(settingsPresenter));
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var trSettings = _settingsService.Current.Translation;
        _sourceLanguage = trSettings.DefaultSourceLanguage ?? LanguageCodes.Auto;
        _targetLanguage = trSettings.PrimaryTargetLanguage ?? LanguageCodes.Auto;

        InitializeLanguageLists();

        TranslateCommand = new RelayCommand(ExecuteTranslateFromUser, () => CanTranslate);
        StopCommand = new RelayCommand(ExecuteStop, () => IsTranslating);
        SwapLanguagesCommand = new RelayCommand(ExecuteSwapLanguages, () => CanSwapLanguages);
        PinCommand = new RelayCommand(() => IsPinned = !IsPinned);
        GoToSettingsCommand = new RelayCommand(() => _settingsPresenter.ShowAsync("translation"));

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
                foreach (var card in Cards)
                {
                    card.RefreshCanRetry();
                }
            }
        }
    }

    public bool CanTranslate => !string.IsNullOrWhiteSpace(_inputText) && !IsTranslating && !_disposed && !_isStopped;

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

            _activeOperation = operation;
            _sessionOriginalText = _inputText;
            _sessionOrigin = request.Origin;
            _autoCopyClaimed = 0;

            StatusMessage = request.StatusMessage;
            Error = request.Error;

            if (request.AutoTranslate && !string.IsNullOrWhiteSpace(request.Text) && request.Error == null && string.IsNullOrWhiteSpace(request.StatusMessage))
            {
                SubmitMainTranslation(operation, _inputText, request.Origin);
            }
        }
        finally
        {
            _programmaticTextUpdate = false;
        }
    }

    private void OnUserInputChanged()
    {
        // 用户手动修改撤销旧操作资格与正在进行的请求
        lock (_gate)
        {
            _activeOperation?.Cancel();
            _pendingMain = null;
            _pendingRetry = null;
        }

        StatusMessage = null;
        Error = null;

        foreach (var card in Cards)
        {
            card.RefreshCanRetry();
        }
    }

    public void ExecuteTranslateFromUser()
    {
        if (string.IsNullOrWhiteSpace(_inputText))
        {
            Error = new ErrorInfo(ErrorCode.InputEmpty, UiText.TranslateEmptyPrompt);
            return;
        }

        if (_inputText.Length > MaxInputChars)
        {
            Error = new ErrorInfo(ErrorCode.InputTooLong, UiText.TranslateInputTooLong);
            return;
        }

        var op = _presenterRef?.TryBeginOperation();
        if (op == null)
        {
            StatusMessage = UiText.TranslateBusy;
            return;
        }

        _activeOperation = (TranslateWindowOperation)op;
        _sessionOriginalText = _inputText;
        _sessionOrigin = TextOrigin.Input;
        _autoCopyClaimed = 0;
        StatusMessage = null;
        Error = null;

        SubmitMainTranslation((TranslateWindowOperation)op, _inputText, TextOrigin.Input);
    }

    private void ExecuteStop()
    {
        lock (_gate)
        {
            _activeOperation?.Cancel();
            _pendingMain = null;
            _pendingRetry = null;
        }

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
        if (text.Length > MaxInputChars)
        {
            Error = new ErrorInfo(ErrorCode.InputTooLong, UiText.TranslateInputTooLong);
            return;
        }

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
            if (_activeOperation != null && !ReferenceEquals(_activeOperation, operation))
            {
                _activeOperation.Cancel();
            }
            _activeOperation = operation;
            _pendingMain = new QueuedMainRequest(operation, req);
            _pendingRetry = null;
            _processMainQueueTask = ProcessMainQueueAsync();
        }
    }

    private async Task ProcessMainQueueAsync()
    {
        Task? oldMain;
        Task? oldRetry;
        lock (_gate)
        {
            oldMain = _activeMainTask;
            oldRetry = _activeRetryTask;
        }

        if (oldMain != null)
        {
            try { await oldMain.ConfigureAwait(false); } catch { }
        }

        if (oldRetry != null)
        {
            try { await oldRetry.ConfigureAwait(false); } catch { }
        }

        QueuedMainRequest? current;
        lock (_gate)
        {
            current = _pendingMain;
            _pendingMain = null;
        }

        if (current == null || current.Operation.IsRetired || current.Operation.CancellationToken.IsCancellationRequested)
        {
            return;
        }

        Task mainTask;
        lock (_gate)
        {
            mainTask = RunMainTranslationAsync(current.Operation, current.Request);
            _activeMainTask = mainTask;
        }

        try
        {
            await mainTask.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeMainTask, mainTask))
                {
                    _activeMainTask = null;
                }
            }
        }
    }

    private async Task RunMainTranslationAsync(TranslateWindowOperation operation, TranslationRequest request)
    {
        await _uiDispatcher.InvokeAsync(() =>
        {
            IsTranslating = true;
            Cards.Clear();
            StatusMessage = null;
            Error = null;
            return Task.CompletedTask;
        }, CancellationToken.None).ConfigureAwait(false);

        var token = operation.CancellationToken;
        IAsyncEnumerator<TranslationEvent>? enumerator = null;

        try
        {
            var stream = _orchestrator.TranslateAsync(request, token);
            enumerator = stream.GetAsyncEnumerator(token);

            while (await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                var evt = enumerator.Current;
                await HandleTranslationEventAsync(operation, evt).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                foreach (var card in Cards.Where(c => c.IsStreaming))
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
                Error = ex.ToErrorInfo();
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                Error = new ErrorInfo(ErrorCode.Unknown, ex.Message);
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (enumerator != null)
            {
                try
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                }
            }

            await _uiDispatcher.InvokeAsync(() =>
            {
                FlushDeltaBufferToUi();
                IsTranslating = false;
                operation.EndWorkOwnership();
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task HandleTranslationEventAsync(TranslateWindowOperation operation, TranslationEvent evt, bool isRetry = false)
    {
        switch (evt)
        {
            case TranslationStarted started:
                _resolvedSourceLanguage = started.Languages.Source;
                _resolvedTargetLanguage = started.Languages.Target;

                await _uiDispatcher.InvokeAsync(() =>
                {
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
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
                break;

            case ProviderStarted ps:
                await _uiDispatcher.InvokeAsync(() =>
                {
                    var card = FindCard(operation.Id, ps.ProviderId, ps.RequestId);
                    if (card != null && !card.IsTerminal)
                    {
                        card.State = CardState.Streaming;
                    }
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
                break;

            case ProviderDelta pd:
                await _deltaBuffer.EnqueueAsync(new QueuedDelta(operation.Id, pd.ProviderId, pd.RequestId, pd.Delta), operation.CancellationToken).ConfigureAwait(false);
                ScheduleUiFlush();
                break;

            case ProviderCompleted pc:
                _deltaBuffer.DiscardForCard(operation.Id, pc.ProviderId, pc.RequestId);
                await _uiDispatcher.InvokeAsync(() =>
                {
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
                        if (!isRetry && !locallyTruncated && !pc.Truncated && _settingsService.Current.Translation.AutoCopy == AutoCopyMode.FirstResult)
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
                    var card = FindCard(operation.Id, pf.ProviderId, pf.RequestId);
                    if (card != null)
                    {
                        bool showSettings = pf.Error.Code is ErrorCode.NotConfigured or ErrorCode.AuthenticationFailed;
                        card.SetFailed(pf.Error.Message, showSettings, pf.Elapsed);
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

        foreach (var d in deltas)
        {
            var card = FindCard(d.OperationId, d.ProviderId, d.RequestId);
            if (card == null || card.IsTerminal || card.IsLocallyTruncated)
            {
                continue;
            }

            int currentTotal = Cards.Sum(c => c.Text.Length);
            int remainingTotal = Math.Max(0, TextBudgetManager.MaxTotalChars - currentTotal);
            int allowedForCard = Math.Min(TextBudgetManager.MaxCardChars - card.Text.Length, remainingTotal);

            if (allowedForCard <= 0)
            {
                card.IsLocallyTruncated = true;
                continue;
            }

            if (d.Delta.Length > allowedForCard)
            {
                string safe = TextBudgetManager.TruncateSurrogateSafe(d.Delta, allowedForCard);
                card.AppendText(safe);
                card.IsLocallyTruncated = true;
            }
            else
            {
                card.AppendText(d.Delta);
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
            _processRetryQueueTask = ProcessRetryQueueAsync();
        }
    }

    private async Task ProcessRetryQueueAsync()
    {
        Task? oldRetry;
        lock (_gate)
        {
            oldRetry = _activeRetryTask;
        }

        if (oldRetry != null)
        {
            try { await oldRetry.ConfigureAwait(false); } catch { }
        }

        QueuedRetryRequest? current;
        lock (_gate)
        {
            current = _pendingRetry;
            _pendingRetry = null;
        }

        if (current == null || current.Operation.IsRetired || current.Operation.CancellationToken.IsCancellationRequested)
        {
            return;
        }

        Task retryTask;
        lock (_gate)
        {
            retryTask = RunRetryTranslationAsync(current.Operation, current.Card, current.Request);
            _activeRetryTask = retryTask;
        }

        try
        {
            await retryTask.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeRetryTask, retryTask))
                {
                    _activeRetryTask = null;
                }
            }
        }
    }

    private async Task RunRetryTranslationAsync(TranslateWindowOperation operation, ProviderResultCardViewModel card, TranslationRequest request)
    {
        var token = operation.CancellationToken;
        IAsyncEnumerator<TranslationEvent>? enumerator = null;

        try
        {
            var stream = _orchestrator.TranslateAsync(request, token);
            enumerator = stream.GetAsyncEnumerator(token);

            while (await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
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
                    card.SetFailed(ex.Message, ex.Code is ErrorCode.NotConfigured or ErrorCode.AuthenticationFailed, TimeSpan.Zero);
                }
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (card.RequestId == request.RequestId)
                {
                    card.SetFailed(ex.Message, false, TimeSpan.Zero);
                }
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (enumerator != null)
            {
                try { await enumerator.DisposeAsync().ConfigureAwait(false); } catch { }
            }

            await _uiDispatcher.InvokeAsync(() =>
            {
                FlushDeltaBufferToUi();
                card.RefreshCanRetry();
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private bool CanRetryCard(TranslateWindowOperation operation)
    {
        return !IsTranslating && _activeOperation != null && _activeOperation.Id == operation.Id && !operation.IsRetired && !operation.CancellationToken.IsCancellationRequested;
    }

    private void OnCardCopy(ProviderResultCardViewModel card)
    {
        if (!string.IsNullOrEmpty(card.Text))
        {
            TrackCopy(card.Text, CancellationToken.None);
        }
    }

    private void OnCardGoToSettings(ProviderResultCardViewModel card)
    {
        _settingsPresenter.ShowAsync("translation");
    }

    private void TrackCopy(string text, CancellationToken token)
    {
        var task = Task.Run(async () =>
        {
            try
            {
                await _clipboardService.SetTextAsync(text, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogClipboardError(_logger, ex.GetType().Name);
            }
        }, token);

        lock (_gate)
        {
            _copyTasks.Add(task);
        }

        task.ContinueWith(t =>
        {
            lock (_gate)
            {
                _copyTasks.Remove(t);
            }
        }, TaskScheduler.Default);
    }

    private ProviderResultCardViewModel? FindCard(Guid opId, string providerId, Guid reqId)
    {
        return Cards.FirstOrDefault(c => c.OperationId == opId && c.ProviderId == providerId && c.RequestId == reqId);
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Topmost));
        OnPropertyChanged(nameof(WindowWidth));
        OnPropertyChanged(nameof(FontSize));
        OnPropertyChanged(nameof(EnterToTranslate));
        OnPropertyChanged(nameof(HideOnDeactivate));
        OnPropertyChanged(nameof(MaxInputChars));
    }

    private TranslateWindowPresenter? _presenterRef;
    internal void AttachPresenter(TranslateWindowPresenter presenter) => _presenterRef = presenter;

    public async Task WaitForActiveWorkAsync()
    {
        while (true)
        {
            Task? queueTask;
            Task? retryQueueTask;
            Task? main;
            Task? retry;
            Task[] copies;

            lock (_gate)
            {
                queueTask = _processMainQueueTask;
                retryQueueTask = _processRetryQueueTask;
                main = _activeMainTask;
                retry = _activeRetryTask;
                copies = _copyTasks.ToArray();
            }

            if (queueTask != null)
            {
                try { await queueTask.ConfigureAwait(false); } catch { }
            }

            if (retryQueueTask != null)
            {
                try { await retryQueueTask.ConfigureAwait(false); } catch { }
            }

            if (main != null)
            {
                try { await main.ConfigureAwait(false); } catch { }
            }

            if (retry != null)
            {
                try { await retry.ConfigureAwait(false); } catch { }
            }

            if (copies.Length > 0)
            {
                try { await Task.WhenAll(copies).ConfigureAwait(false); } catch { }
            }

            lock (_gate)
            {
                if ((_processMainQueueTask == null || _processMainQueueTask.IsCompleted) &&
                    (_processRetryQueueTask == null || _processRetryQueueTask.IsCompleted) &&
                    (_activeMainTask == null || _activeMainTask.IsCompleted) &&
                    (_activeRetryTask == null || _activeRetryTask.IsCompleted) &&
                    _copyTasks.All(c => c.IsCompleted))
                {
                    break;
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _isStopped = true;
            _activeOperation?.Cancel();
        }

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
}
