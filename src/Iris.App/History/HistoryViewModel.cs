using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows.Input;
using Iris.App.Hosting;
using Iris.App.Settings;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Microsoft.Extensions.Logging;

namespace Iris.App.History;

internal sealed record HistoryRow(HistorySummary Summary)
{
    private static CompositeFormat MetadataFormat => CompositeFormat.Parse(UiText.HistoryMetadataFormat);
    public long Id => Summary.Id;
    public string Preview => Summary.SourcePreview;
    public string Metadata => string.Format(Iris.Core.Localization.UiLanguage.Culture, MetadataFormat,
        Summary.CreatedAt.ToLocalTime(), OriginLabel, Summary.SourceLanguage, Summary.TargetLanguage, Summary.ResultCount);
    public string Markers => string.Join(" · ", new[] { Summary.IsFavorite ? UiText.HistoryFavorite : null,
        Summary.HasTruncatedResults ? UiText.HistoryTruncated : null }.Where(s => s != null));
    private string OriginLabel => Summary.Origin switch
    {
        TextOrigin.Input => UiText.HistoryOriginInput, TextOrigin.Selection => UiText.HistoryOriginSelection,
        TextOrigin.Clipboard => UiText.HistoryOriginClipboard, TextOrigin.Screenshot => UiText.HistoryOriginScreenshot,
        TextOrigin.History => UiText.HistoryOriginHistory, TextOrigin.CommandLine => UiText.HistoryOriginCommandLine, _ => UiText.HistoryOriginOther
    };
}

internal sealed record HistoryConfirmation(long? Id);

/// <summary>One window owns three bounded latest-intent queues and one admitted write, including their UI completions.</summary>
internal sealed partial class HistoryViewModel : INotifyPropertyChanged, IDisposable
{
    private static CompositeFormat PageFormat => CompositeFormat.Parse(UiText.HistoryPageFormat);
    private readonly ITranslationHistoryRepository _repository;
    private readonly IClipboardService _clipboard;
    private readonly ITranslateWindowPresenter _translation;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly ILogger<HistoryViewModel> _logger;
    private readonly SettingsDraftOperationQueue _queries = new(), _details = new(), _actions = new();
    private readonly Lock _gate = new();
    private Task _writeTask = Task.CompletedTask;
    private bool _stopped, _disposed;
    private long _queryVersion, _detailVersion, _actionVersion;
    private string _keyword = "";
    private bool _favoritesOnly, _keepFavorites = true;
    private HistoryRow? _selected;
    private HistoryConfirmation? _confirmation;
    private readonly List<AsyncRelayCommand> _asyncCommands = [];
    private readonly List<RelayCommand> _commands = [];

    public HistoryViewModel(ITranslationHistoryRepository repository, IClipboardService clipboard,
        ITranslateWindowPresenter translation, IUiDispatcher ui, TimeProvider time, ILogger<HistoryViewModel> logger)
    {
        _repository = repository; _clipboard = clipboard; _translation = translation; _ui = ui; _time = time; _logger = logger;
        RefreshCommand = Async(() => RefreshAsync(), () => CanInteract);
        PreviousCommand = Async(() => RequestQueryAsync(PageIndex - 1), () => CanInteract && !IsLoading && ListError == null && PageIndex > 0);
        NextCommand = Async(() => RequestQueryAsync(PageIndex + 1), () => CanInteract && !IsLoading && ListError == null && (long)(PageIndex + 1) * 50 < TotalCount);
        FavoriteCommand = Async(ToggleFavoriteAsync, () => CanActOnRow);
        CopySourceCommand = Async(() => CopySourceAsync(), () => CanActOnRow);
        RetranslateCommand = Async(RetranslateAsync, () => CanActOnRow);
        var copyResult = new AsyncRelayCommand(p => CopyResultAsync(p as string), _ => CanActOnRow, LogFailure);
        _asyncCommands.Add(copyResult); CopyResultCommand = copyResult;
        DeleteCommand = Command(RequestDelete, () => CanActOnRow);
        ClearCommand = Command(RequestClear, () => CanInteract && !IsWriting);
        ConfirmCommand = Async(ConfirmAsync, () => CanInteract && !IsWriting && _confirmation != null);
        CancelConfirmationCommand = Command(CancelConfirmation, () => _confirmation != null && !IsWriting);
        CloseCommand = Command(() => RequestClose?.Invoke());
    }

    public string Keyword
    {
        get => _keyword;
        set { if (!CanInteract || _keyword == value) return; _keyword = value; _ = ObserveAsync(RequestQueryAsync(0, debounce: true)); }
    }
    public bool FavoritesOnly
    {
        get => _favoritesOnly;
        set { if (!CanInteract || _favoritesOnly == value) return; _favoritesOnly = value; _ = ObserveAsync(RequestQueryAsync(0)); }
    }
    public bool KeepFavorites
    {
        get => _keepFavorites;
        set { if (!CanInteract || IsWriting) return; _keepFavorites = value; Notify(); }
    }
    public IReadOnlyList<HistoryRow> Rows { get; private set; } = [];
    public HistoryRow? SelectedRow
    {
        get => _selected;
        set
        {
            if (!CanInteract || ReferenceEquals(_selected, value) || value != null && !Rows.Contains(value)) return;
            _selected = value; InvalidateDetailsAndActions(); Notify();
            if (value != null && CanInteract) _ = ObserveAsync(LoadDetailAsync(value.Id));
        }
    }
    public HistoryEntry? Detail { get; private set; }
    public bool HasDetail => Detail != null;
    public bool HasSelection => _selected != null;
    public bool IsLoading { get; private set; }
    public bool IsDetailLoading { get; private set; }
    public bool IsWriting { get; private set; }
    public bool CanInteract { get { lock (_gate) return !_stopped && !_disposed; } }
    private bool CanActOnRow => CanInteract && !IsWriting && _selected != null;
    public bool IsEmpty => !IsLoading && ListError == null && Rows.Count == 0;
    public int PageIndex { get; private set; }
    public int TotalCount { get; private set; }
    public string PageLabel => IsLoading || ListError != null ? "" : string.Format(Iris.Core.Localization.UiLanguage.Culture,
        PageFormat, PageIndex + 1, Math.Max(1, (TotalCount + 49L) / 50), TotalCount);
    public string FavoriteLabel => _selected?.Summary.IsFavorite == true ? UiText.HistoryUnfavorite : UiText.HistoryFavorite;
    public string? ListError { get; private set; }
    public string? DetailError { get; private set; }
    public string? Status { get; private set; }
    public bool HasConfirmation => _confirmation != null;
    public bool IsClearConfirmation => _confirmation is { Id: null };
    public string ConfirmationText => IsClearConfirmation ? UiText.HistoryClearConfirm : UiText.HistoryDeleteConfirm;
    public ICommand RefreshCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand FavoriteCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand ConfirmCommand { get; }
    public ICommand CancelConfirmationCommand { get; }
    public ICommand CopySourceCommand { get; }
    public ICommand CopyResultCommand { get; }
    public ICommand RetranslateCommand { get; }
    public ICommand CloseCommand { get; }
    public event Action? RequestClose;
    public event PropertyChangedEventHandler? PropertyChanged;

    public Task RefreshAsync() => RequestQueryAsync(PageIndex);
    internal Task QueryTask { get; private set; } = Task.CompletedTask;
    internal Task DetailTask { get; private set; } = Task.CompletedTask;

    internal Task RequestQueryAsync(int page, bool debounce = false)
    {
        if (!CanInteract) return Task.CompletedTask;
        long version = Interlocked.Increment(ref _queryVersion);
        long requestedAt = _time.GetTimestamp();
        var query = new HistoryQuery(_keyword, _favoritesOnly, Math.Max(0, page) * 50, 50);
        IsLoading = true; ListError = null; _selected = null; Rows = [];
        InvalidateDetailsAndActions(); Notify();
        return QueryTask = Enqueue(_queries, async ct =>
        {
            try
            {
                var remaining = TimeSpan.FromMilliseconds(300) - _time.GetElapsedTime(requestedAt);
                if (debounce && remaining > TimeSpan.Zero) await Task.Delay(remaining, _time, ct).ConfigureAwait(false);
                var result = await _repository.QueryAsync(query, ct).ConfigureAwait(false);
                int acceptedPage = query.Offset / 50;
                if (result.TotalCount > 0 && query.Offset >= result.TotalCount)
                {
                    acceptedPage = (result.TotalCount - 1) / 50;
                    ct.ThrowIfCancellationRequested();
                    result = await _repository.QueryAsync(query with { Offset = acceptedPage * 50 }, ct).ConfigureAwait(false);
                }
                if (result.TotalCount == 0) acceptedPage = 0;
                await PublishAsync(() => CurrentQuery(version, ct), () =>
                {
                    Rows = result.Items.Select(s => new HistoryRow(s)).ToArray();
                    PageIndex = acceptedPage; TotalCount = result.TotalCount; IsLoading = false; Notify();
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                LogFailure(ex);
                await PublishAsync(() => CurrentQuery(version, ct), () =>
                {
                    ListError = ex is ArgumentException ? UiText.HistoryKeywordError : UiText.HistoryStorageError;
                    IsLoading = false; Notify();
                }).ConfigureAwait(false);
            }
        });
    }

    private bool CurrentQuery(long version, CancellationToken ct) => CanInteract && !ct.IsCancellationRequested && version == Interlocked.Read(ref _queryVersion);
    private bool CurrentRow(long id, long version, bool action, CancellationToken ct) => CanInteract && !ct.IsCancellationRequested
        && _selected?.Id == id && version == Interlocked.Read(ref (action ? ref _actionVersion : ref _detailVersion));

    private void InvalidateDetailsAndActions()
    {
        Interlocked.Increment(ref _detailVersion); Interlocked.Increment(ref _actionVersion);
        _details.CancelInFlight(); _actions.CancelInFlight();
        Detail = null; DetailError = null; IsDetailLoading = false;
    }

    private Task LoadDetailAsync(long id)
    {
        long version = Interlocked.Read(ref _detailVersion);
        IsDetailLoading = true; Notify();
        return DetailTask = Enqueue(_details, async ct =>
        {
            try
            {
                var entry = await _repository.GetAsync(id, ct).ConfigureAwait(false);
                await PublishAsync(() => CurrentRow(id, version, false, ct), () =>
                {
                    Detail = entry; IsDetailLoading = false;
                    if (entry == null) { Status = UiText.HistoryMissing; _ = ObserveAsync(RefreshAsync()); }
                    Notify();
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                LogFailure(ex);
                await PublishAsync(() => CurrentRow(id, version, false, ct), () => { DetailError = UiText.HistoryStorageError; IsDetailLoading = false; Notify(); }).ConfigureAwait(false);
            }
        });
    }

    public void RequestDelete() { if (CanActOnRow) { _confirmation = new(_selected!.Id); Notify(); } }
    public void RequestClear() { if (CanInteract && !IsWriting) { _keepFavorites = true; _confirmation = new(null); Notify(); } }
    public void CancelConfirmation() { if (!IsWriting) { _confirmation = null; Notify(); } }
    public Task ConfirmAsync()
    {
        if (_confirmation is not { } intent || !CanInteract || IsWriting) return Task.CompletedTask;
        bool keep = _keepFavorites;
        return WriteAsync(() => intent.Id is { } id ? _repository.DeleteAsync(id, CancellationToken.None) : _repository.ClearAsync(keep, CancellationToken.None));
    }
    public Task ToggleFavoriteAsync()
    {
        if (!CanActOnRow) return Task.CompletedTask;
        var row = _selected!;
        return WriteAsync(() => _repository.SetFavoriteAsync(row.Id, !row.Summary.IsFavorite, CancellationToken.None));
    }
    private Task WriteAsync(Func<Task> write)
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_stopped || _disposed || !_writeTask.IsCompleted) return Task.CompletedTask;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _writeTask = completion.Task;
        }
        _ = CompleteWriteAsync(write, completion);
        return completion.Task;
    }
    private async Task CompleteWriteAsync(Func<Task> write, TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            IsWriting = true; _confirmation = null; Status = UiText.HistoryWriting; InvalidateDetailsAndActions(); Notify();
            try
            {
                await write().ConfigureAwait(false);
                await PublishAsync(() => CanInteract, () => { Status = UiText.HistoryWriteDone; _ = ObserveAsync(RefreshAsync()); }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogFailure(ex);
                await PublishAsync(() => CanInteract, () => { Status = UiText.HistoryStorageError; Notify(); }).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            try { await _ui.InvokeAsync(() => { IsWriting = false; Notify(); return Task.CompletedTask; }, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { failure = failure == null ? ex : new AggregateException(failure, ex); }
        }
        if (failure != null) completion.TrySetException(failure);
        else completion.TrySetResult();
    }

    public Task CopySourceAsync() => ActAsync(null, translate: false);
    public Task CopyResultAsync(string? providerId) => providerId == null ? Task.CompletedTask : ActAsync(providerId, translate: false);
    public Task RetranslateAsync() => ActAsync(null, translate: true);
    private Task ActAsync(string? providerId, bool translate)
    {
        if (!CanActOnRow) return Task.CompletedTask;
        long id = _selected!.Id, version = Interlocked.Increment(ref _actionVersion);
        return Enqueue(_actions, async ct =>
        {
            try
            {
                var entry = await _repository.GetAsync(id, ct).ConfigureAwait(false);
                await _ui.InvokeAsync(async () =>
                {
                    if (!CurrentRow(id, version, true, ct)) return;
                    if (entry == null) { Status = UiText.HistoryMissing; _ = ObserveAsync(RefreshAsync()); Notify(); return; }
                    if (translate)
                    {
                        using var operation = _translation.TryBeginOperation(ct);
                        if (operation == null) { Status = UiText.HistoryTranslateFailed; Notify(); return; }
                        bool accepted = await _translation.TryShowAsync(operation, new()
                        { Text = entry.SourceText, Origin = TextOrigin.History, AutoTranslate = true, FocusInput = true });
                        if (CurrentRow(id, version, true, ct)) Status = accepted ? UiText.HistoryTranslated : UiText.HistoryTranslateFailed;
                    }
                    else
                    {
                        var result = providerId == null ? null : entry.Results.FirstOrDefault(r => r.ProviderId == providerId);
                        if (providerId != null && result == null) { Status = UiText.HistoryMissing; Notify(); return; }
                        string text = result == null ? entry.SourceText : result.Text + (result.Truncated ? "\n" + UiText.HistoryTruncatedSuffix : "");
                        await _clipboard.SetTextAsync(text, ct);
                        if (CurrentRow(id, version, true, ct)) Status = UiText.HistoryCopied;
                    }
                    if (CurrentRow(id, version, true, ct)) Notify();
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                LogFailure(ex);
                await PublishAsync(() => CurrentRow(id, version, true, ct), () =>
                { Status = translate ? UiText.HistoryTranslateFailed : UiText.HistoryCopyFailed; Notify(); }).ConfigureAwait(false);
            }
        });
    }

    public void StopAcceptance()
    {
        lock (_gate) _stopped = true;
        Interlocked.Increment(ref _queryVersion); Interlocked.Increment(ref _detailVersion); Interlocked.Increment(ref _actionVersion);
        _queries.CancelInFlight(); _details.CancelInFlight(); _actions.CancelInFlight();
    }
    private Task Enqueue(SettingsDraftOperationQueue queue, Func<CancellationToken, Task> work)
    {
        // A binding notification can synchronously request close before the actual queue admission.
        if (!CanInteract) return Task.CompletedTask;
        try { return queue.EnqueueAsync(work); }
        catch (OperationCanceledException) when (!CanInteract) { return Task.CompletedTask; }
    }
    public async Task DrainAsync()
    {
        StopAcceptance();
        Task write;
        lock (_gate) write = _writeTask;
        var readers = Task.WhenAll(_queries.DrainAndCancelAllAsync(), _details.DrainAndCancelAllAsync(), _actions.DrainAndCancelAllAsync());
        // An ended operation failure is not a retained cleanup lease. Observe it without replaying the write.
        try { await write.ConfigureAwait(false); } catch (Exception ex) { LogFailure(ex); }
        await readers.ConfigureAwait(false);
        // Command adapters still own their final CanExecute notification after the work delegate returns.
        await Task.WhenAll(_asyncCommands.Select(c => c.ExecutionTask ?? Task.CompletedTask)).ConfigureAwait(false);
    }
    internal void ShowClosing(bool failed = false) { Status = failed ? UiText.HistoryCloseFailed : UiText.HistoryClosing; Notify(); }
    private Task PublishAsync(Func<bool> current, Action publish) => _ui.InvokeAsync(() => { if (current()) publish(); return Task.CompletedTask; }, CancellationToken.None);
    private AsyncRelayCommand Async(Func<Task> run, Func<bool> canRun)
    { var c = new AsyncRelayCommand(run, canRun, LogFailure); _asyncCommands.Add(c); return c; }
    private RelayCommand Command(Action run, Func<bool>? canRun = null)
    { var c = new RelayCommand(run, canRun); _commands.Add(c); return c; }
    private void Notify()
    {
        PropertyChanged?.Invoke(this, new(null));
        foreach (var c in _asyncCommands) c.RaiseCanExecuteChanged();
        foreach (var c in _commands) c.RaiseCanExecuteChanged();
    }
    private async Task ObserveAsync(Task task)
    { try { await task; } catch (OperationCanceledException) { } catch (Exception ex) { LogFailure(ex); } }
    internal void LogFailure(Exception ex) => LogOperationFailed(_logger, ex.GetType().Name);
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "历史窗口操作失败：{ExceptionType}")]
    private static partial void LogOperationFailed(ILogger logger, string exceptionType);
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!_stopped || !_writeTask.IsCompleted || _queries.HasActiveWork || _details.HasActiveWork || _actions.HasActiveWork)
                throw new InvalidOperationException("History work must finish before disposal.");
            _disposed = true;
        }
        _queries.Dispose(); _details.Dispose(); _actions.Dispose();
        Rows = []; Detail = null; _selected = null; _confirmation = null; _keyword = "";
        RequestClose = null; PropertyChanged = null;
    }
}
