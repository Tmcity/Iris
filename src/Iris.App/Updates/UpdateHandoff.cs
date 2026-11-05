using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using Iris.Core.Errors;
using Iris.Platform.Windows.Maintenance;

namespace Iris.App.Updates;

/// <summary>Transfers only a validated candidate to the out-of-process maintenance owner.</summary>
internal sealed class UpdateHandoff : IAsyncDisposable, IDisposable
{
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly Lock _cancelGate = new();
    private CancellationTokenSource? _preparing;
    private Task? _cancelRequest;
    private bool _revoking;
    private int _revokeWaiters;
    private readonly UpdateHandoffEnvironment _environment;
    private Process? _process;
    private NamedPipeServerStream? _pipe;
    private string? _token;
    private string? _directory;
    internal bool IsPrepared => _process != null && _pipe != null;
    public UpdateHandoff(UpdateHandoffEnvironment? environment = null) => _environment = environment ?? new();
    internal async Task PrepareAsync(UpdateCandidate candidate, CancellationToken token)
    {
        await _mutex.WaitAsync(token).ConfigureAwait(false);
        using var watchCancellation = new CancellationTokenSource();
        Task watch = Task.CompletedTask;
        Exception? primary = null;
        var cleanup = new List<Exception>();
        bool ownsAttempt = false;
        try
        {
            CancellationToken attemptToken;
            lock (_cancelGate)
            {
                if (_revoking)
                    throw token.IsCancellationRequested ? new OperationCanceledException(token) : new IrisException(ErrorCode.Unknown, ErrorMessages.GetDefault(ErrorCode.Unknown), retryable: true);
                if (_process != null || _pipe != null || _directory != null) throw new InvalidOperationException("Maintenance preparation or cleanup is already owned.");
                _preparing = CancellationTokenSource.CreateLinkedTokenSource(token);
                _preparing.CancelAfter(TimeSpan.FromSeconds(30));
                attemptToken = _preparing.Token;
                ownsAttempt = true;
            }
            _environment.ValidateInstallation();
            await _environment.ValidateCandidateAsync(candidate, attemptToken).ConfigureAwait(false);
            attemptToken.ThrowIfCancellationRequested();
            string original = _environment.HelperPath;
            MaintenancePaths.RequireNoLinks(original);
            _directory = MaintenanceWorkDirectory.Create(_environment.StateRoot);
            string executable = Path.Combine(_directory, "Iris.Maintenance.exe");
            await using (var input = File.OpenRead(original))
            await using (var output = new FileStream(executable, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                await input.CopyToAsync(output, attemptToken).ConfigureAwait(false);
            string name = "Iris.Maintenance." + Guid.NewGuid().ToString("N"); _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
            using var parent = Process.GetCurrentProcess();
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _directory };
            foreach (var argument in new[] { "--handoff", name, _token, parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), parent.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(argument);
            _process = _environment.StartHelper(start);
            watch = WatchExitAsync(_process, watchCancellation.Token);
            await _pipe.WaitForConnectionAsync(attemptToken).ConfigureAwait(false); MaintenanceProtocol.VerifyPeer(_pipe, true, _process.Id);
            await MaintenanceProtocol.WriteAsync(_pipe, new("prepare", _token, Path.GetFileName(candidate.Path), candidate.Sha256, candidate.Size, candidate.Version), attemptToken).ConfigureAwait(false);
            var ready = await MaintenanceProtocol.ReadAsync(_pipe, attemptToken).ConfigureAwait(false);
            if (ready.Kind != "ready" || ready.Token != _token) throw ReleaseIdentity.Invalid();
        }
        catch (Exception ex)
        {
            Exception unwrap = ex is AggregateException ae && ae.InnerExceptions.Count == 1 ? ae.InnerExceptions[0] : ex;
            if (unwrap is OperationCanceledException && !token.IsCancellationRequested)
            {
                if (_process?.HasExited == true)
                    primary = new IrisException(ErrorCode.Unknown, ErrorMessages.GetDefault(ErrorCode.Unknown), ex, retryable: true);
                else if (_revoking)
                    primary = new IrisException(ErrorCode.Unknown, ErrorMessages.GetDefault(ErrorCode.Unknown), ex, retryable: true);
                else
                    primary = new IrisException(ErrorCode.Timeout, ErrorMessages.GetDefault(ErrorCode.Timeout), ex, retryable: true);
            }
            else
            {
                primary = ex;
            }
            try { if (ownsAttempt) await RevokeCoreAsync().ConfigureAwait(false); } catch (Exception failure) { cleanup.Add(failure); }
        }
        finally
        {
            try { await watchCancellation.CancelAsync().ConfigureAwait(false); } catch (Exception ex) { cleanup.Add(ex); }
            try { await watch.ConfigureAwait(false); } catch (Exception ex) { cleanup.Add(ex); }
            Task? cancel; lock (_cancelGate) cancel = ownsAttempt ? _cancelRequest : null;
            try { if (cancel != null) await cancel.ConfigureAwait(false); } catch (Exception ex) { cleanup.Add(ex); }
            finally
            {
                lock (_cancelGate)
                {
                    if (ownsAttempt) { _preparing?.Dispose(); _preparing = null; _cancelRequest = null; if (_process == null) _revoking = _revokeWaiters != 0; }
                }
                _mutex.Release();
            }
        }
        if (primary != null && cleanup.Count == 0) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) cleanup.Insert(0, primary);
        if (cleanup.Count != 0) throw new AggregateException(cleanup);
    }
    internal async Task CompleteMainAsync(bool cleanExit)
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!cleanExit) { await RevokeCoreAsync().ConfigureAwait(false); return; }
            if (_pipe == null) return;
            using var confirm = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await MaintenanceProtocol.WriteAsync(_pipe, new("commit", _token!), confirm.Token).ConfigureAwait(false);
            var receipt = await MaintenanceProtocol.ReadAsync(_pipe, confirm.Token).ConfigureAwait(false);
            if (receipt.Kind != "committed" || receipt.Token != _token) throw ReleaseIdentity.Invalid();
            await _pipe.DisposeAsync().ConfigureAwait(false); _pipe = null;
            _process?.Dispose(); _process = null; // Ownership transfers to helper; it still waits for actual parent exit.
            _directory = null; // Helper process runs inside this directory; main app relinquishes ownership.
        }
        catch (Exception primary)
        {
            try { await RevokeCoreAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException(primary, cleanup); }
            throw;
        }
        finally { _mutex.Release(); }
    }
    internal async Task RevokeAsync()
    {
        lock (_cancelGate) { _revokeWaiters++; RequestRevoke(); }
        await _mutex.WaitAsync().ConfigureAwait(false);
        try { await RevokeCoreAsync().ConfigureAwait(false); }
        finally { lock (_cancelGate) { _revokeWaiters--; _revoking = _revokeWaiters != 0; } _mutex.Release(); }
    }
    private void RequestRevoke()
    {
        lock (_cancelGate) { _revoking = true; if (_preparing != null) _cancelRequest ??= _preparing.CancelAsync(); }
    }
    private async Task WatchExitAsync(Process process, CancellationToken token)
    {
        try { await process.WaitForExitAsync(token).ConfigureAwait(false); RequestRevoke(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private async Task RevokeCoreAsync()
    {
        // Closing the control stream is the cancellation signal. No updater was started before commit.
        if (_pipe != null)
        {
            if (_pipe.IsConnected)
                try
                {
                    using var cancelWrite = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await MaintenanceProtocol.WriteAsync(_pipe, new("cancel", _token!), cancelWrite.Token).ConfigureAwait(false);
                }
                catch (IOException) { /* A closed peer is already revoked; still join the actual process below. */ }
                catch (OperationCanceledException) { /* Closing the stream also revokes; still join the peer below. */ }
            await _pipe.DisposeAsync().ConfigureAwait(false); _pipe = null;
        }
        if (_process != null) { await _process.WaitForExitAsync().ConfigureAwait(false); _process.Dispose(); _process = null; }
        if (_directory != null && Directory.Exists(_directory))
        { _environment.DeleteWorkDirectory(_directory); _directory = null; }
        lock (_cancelGate) _revoking = _revokeWaiters != 0;
    }
    public async ValueTask DisposeAsync() { await RevokeAsync().ConfigureAwait(false); _mutex.Dispose(); }
    public void Dispose()
    {
        if (_process != null || _pipe != null) throw new InvalidOperationException("Maintenance ownership must be completed asynchronously.");
        _mutex.Dispose();
    }
}
