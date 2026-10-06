using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Ocr;
using Iris.Core.Translation;
using Microsoft.Extensions.Logging;

namespace Iris.Core.Settings;

internal sealed partial class SettingsEditCoordinator
{
    public IOcrDraftTestSession CreateOcrDraftTest(OcrProviderDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Profile == null || draft.Credentials == null
            || draft.BaseProfile == null && draft.Profile.CredentialSetId != null
            || draft.BaseProfile is { } b && (b.Id != draft.Profile.Id || b.Kind != draft.Profile.Kind
                || b.CredentialSetId != draft.Profile.CredentialSetId)) throw InvalidOcrDraft();
        var captured = draft with
        {
            Profile = CaptureOcrProfile(draft.Profile, true),
            BaseProfile = draft.BaseProfile is { } baseline ? CaptureOcrProfile(baseline, false) : null,
            Credentials = draft.Credentials with { Replacement = draft.Credentials.Replacement is { } c ? c with { } : null }
        };
        if (ValidateOcrProfile(captured.Profile) != null || ValidateOcrCredentials(captured.Profile, captured.Credentials) != null)
            throw InvalidOcrDraft();
        return new OcrDraftTestSession(this, captured);
    }

    private static IrisException InvalidOcrDraft() => new(ErrorCode.InvalidArgument, ErrorMessages.GetDefault(ErrorCode.InvalidArgument));

    private async Task<DraftCredentialsResult> ResolveOcrDraftCredentialsAsync(OcrProviderDraft draft, CancellationToken caller, CancellationToken cancellationToken)
    {
        if (draft.Credentials.Kind == CredentialEditKind.Replace) return new(draft.Credentials.Replacement);
        if (draft.Credentials.Kind == CredentialEditKind.Clear || draft.BaseProfile == null) return new(ProviderCredentials.None);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            caller.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            var current = _settingsService.Current.Ocr.Providers.FirstOrDefault(p => p.Id == draft.BaseProfile.Id);
            if (current == null || current != draft.BaseProfile)
                return new(null, new(ErrorCode.Unknown, ConflictMessage, Detail: "BaseProfileConflict"));
            var captured = CaptureOcrProfile(current, false);
            var resolved = await Task.Run(() =>
            {
                caller.ThrowIfCancellationRequested();
                cancellationToken.ThrowIfCancellationRequested();
                return ProviderCredentialResolver.Resolve(captured, _secretStore, _logger);
            }, CancellationToken.None).ConfigureAwait(false);
            caller.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            return resolved.IsExplicitUnconfigured || resolved.Credentials == null
                ? new(null, new(ErrorCode.NotConfigured, ErrorMessages.GetDefault(ErrorCode.NotConfigured)))
                : new(resolved.Credentials);
        }
        finally { _gate.Release(); }
    }

    private static ErrorInfo SafeOcrDraftError(ErrorInfo error) => new(
        Enum.IsDefined(error.Code) ? error.Code : ErrorCode.Unknown,
        ErrorMessages.GetDefault(Enum.IsDefined(error.Code) ? error.Code : ErrorCode.Unknown),
        HttpStatus: error.HttpStatus, Retryable: error.Retryable);

    private sealed class OcrDraftTestSession(SettingsEditCoordinator owner, OcrProviderDraft draft) : IOcrDraftTestSession
    {
        private readonly Lock _lock = new();
        private readonly CancellationTokenSource _lifetime = new();
        private SettingsEditCoordinator? _owner = owner;
        private OcrProviderDraft? _draft = draft;
        private IOcrProviderSnapshot? _snapshot;
        private CancellationToken _caller;
        private CancellationTokenRegistration _callerRegistration;
        private Task<ProviderTestResult>? _run;
        private Task? _cancellation, _dispose;
        private bool _started, _closing, _disposed;

        public Task<ProviderTestResult> RunAsync(CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                if (_started || _closing) throw new InvalidOperationException("OCR 草稿测试会话只允许运行一次，关闭后不可运行。");
                _started = true; _caller = cancellationToken;
                _callerRegistration = cancellationToken.Register(Cancel);
                return _run = Task.Run(RunCoreAsync, CancellationToken.None);
            }
        }

        private void Cancel()
        {
            lock (_lock)
            {
                if (!_disposed) _cancellation ??= _lifetime.CancelAsync();
            }
        }

        private void CheckCancellation()
        {
            _caller.ThrowIfCancellationRequested();
            _lifetime.Token.ThrowIfCancellationRequested();
        }

        private async Task<ProviderTestResult> RunCoreAsync()
        {
            SettingsEditCoordinator? coordinator = _owner;
            OcrProviderDraft? captured = _draft;
            ProviderCredentials? credentials = null;
            IOcrProvider? provider = null;
            try
            {
                CheckCancellation();
                var resolved = await coordinator!.ResolveOcrDraftCredentialsAsync(captured!, _caller, _lifetime.Token).ConfigureAwait(false);
                CheckCancellation();
                if (resolved.Error is { } error) return new(false, TimeSpan.Zero, Error: SafeOcrDraftError(error));
                credentials = resolved.Credentials ?? ProviderCredentials.None;
                var registry = coordinator._ocrProviderRegistry ?? throw new IrisException(ErrorCode.NotConfigured, ErrorMessages.GetDefault(ErrorCode.NotConfigured));
                CheckCancellation();
                // Store ownership before checking cancellation again: late cancellation
                // never leaves a successfully returned snapshot without a disposer.
                _snapshot = await registry.CreateTransientAsync(captured!.Profile, credentials, _lifetime.Token).ConfigureAwait(false);
                CheckCancellation();
                provider = _snapshot.Providers.Single().Provider;
                CheckCancellation();
                var result = await provider.TestAsync(_lifetime.Token).ConfigureAwait(false);
                CheckCancellation();
                return new(result.Success, result.Latency, Error: result.Error is { } failure ? SafeOcrDraftError(failure) : null);
            }
            catch (OperationCanceledException) when (_caller.IsCancellationRequested || _lifetime.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                LogOcrDraftFailure(coordinator!._logger, "RUN", ex.GetType().Name);
                // A genuine provider/registry cleanup failure remains observable even
                // after cancellation; only cancellation itself is reported as OCE.
                var code = ex is IrisException known && Enum.IsDefined(known.Code) ? known.Code : ErrorCode.Unknown;
                return new(false, TimeSpan.Zero, Error: new(code, ErrorMessages.GetDefault(code)));
            }
            finally { coordinator = null; captured = null; credentials = null; provider = null; }
        }

        public ValueTask DisposeAsync()
        {
            lock (_lock)
            {
                if (_disposed) return ValueTask.CompletedTask;
                if (_dispose is { IsCompleted: false }) return new(_dispose);
                _closing = true;
                Cancel();
                return new(_dispose = Task.Run(DisposeCoreAsync, CancellationToken.None));
            }
        }

        private async Task DisposeCoreAsync()
        {
            // Run reports its own result/error to its caller. Closing still owns and
            // awaits that task, even when the provider ignores the cancellation token.
            if (_run != null)
                try { await _run.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            await _callerRegistration.DisposeAsync().ConfigureAwait(false);
            var failures = new List<string>();
            try { await _cancellation!.ConfigureAwait(false); }
            catch (Exception ex)
            {
                LogOcrDraftFailure(_owner!._logger, "CANCEL", ex.GetType().Name);
                failures.Add("CANCEL");
                // Observed callbacks have exited; a retry must not invoke them again.
                lock (_lock) _cancellation = Task.CompletedTask;
            }
            if (_snapshot != null)
            {
                try { await _snapshot.DisposeAsync().ConfigureAwait(false); _snapshot = null; }
                catch (Exception ex)
                {
                    LogOcrDraftFailure(_owner!._logger, "DISPOSE", ex.GetType().Name);
                    failures.Add("DISPOSE");
                }
            }
            if (failures.Count != 0)
                throw new IrisException(ErrorCode.Unknown, ErrorMessages.GetDefault(ErrorCode.Unknown), detail: string.Join(';', failures));
            lock (_lock)
            {
                _owner = null; _draft = null; _run = null; _cancellation = null;
                _callerRegistration = default; _caller = default;
                _lifetime.Dispose(); _disposed = true;
            }
        }
    }

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "OCR草稿测试未完成: Stage={Stage}, ExceptionType={ExceptionType}")]
    private static partial void LogOcrDraftFailure(ILogger logger, string stage, string exceptionType);
}
