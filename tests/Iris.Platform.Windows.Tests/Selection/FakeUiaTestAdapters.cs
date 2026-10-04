using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Iris.Platform.Windows.Selection;

namespace Iris.Platform.Windows.Tests.Selection;

internal sealed class FakeNativeUiaAdapter : INativeUiaAdapter
{
    public nint ForegroundWindow { get; set; } = 0x1000;
    public uint WindowProcessId { get; set; } = 4242;
    public uint CurrentProcessId { get; set; } = 1000;
    public Func<nint, bool>? IsWindowValidFunc { get; set; }
    public Func<nint, nint, bool>? IsWindowOrAncestorFunc { get; set; }
    public FakeUiaAutomationSession Session { get; set; } = new();
    public Exception? ThrowOnCreateSession { get; set; }
    public int TotalCreateSessionCalls { get; private set; }
    public int CreateSessionConnectionTimeout { get; private set; }
    public int CreateSessionTransactionTimeout { get; private set; }

    public nint GetForegroundWindow() => ForegroundWindow;

    public uint GetWindowProcessId(nint hwnd) => hwnd == ForegroundWindow ? WindowProcessId : 0;

    public bool IsWindowValid(nint hwnd) => IsWindowValidFunc?.Invoke(hwnd) ?? (hwnd != 0);

    public bool IsWindowOrAncestor(nint childHwnd, nint expectedAncestorHwnd) =>
        IsWindowOrAncestorFunc?.Invoke(childHwnd, expectedAncestorHwnd) ?? (childHwnd == expectedAncestorHwnd);

    public uint GetCurrentProcessId() => CurrentProcessId;

    public IUiaAutomationSession CreateSession(int connectionTimeoutMs, int transactionTimeoutMs)
    {
        TotalCreateSessionCalls++;
        CreateSessionConnectionTimeout = connectionTimeoutMs;
        CreateSessionTransactionTimeout = transactionTimeoutMs;

        if (ThrowOnCreateSession != null)
        {
            throw ThrowOnCreateSession;
        }

        return Session;
    }
}

internal sealed class FakeUiaAutomationSession : IUiaAutomationSession
{
    public FakeUiaElement? FocusedElement { get; set; }
    public Exception? ThrowOnGetFocused { get; set; }
    public Exception? ThrowOnDisposeException { get; set; }
    public bool IsDisposed { get; private set; }

    public IUiaElement? GetFocusedElement()
    {
        if (ThrowOnGetFocused != null) throw ThrowOnGetFocused;
        return FocusedElement;
    }

    public void Dispose()
    {
        IsDisposed = true;
        if (ThrowOnDisposeException != null) throw ThrowOnDisposeException;
    }
}

internal sealed class FakeUiaElement : IUiaElement
{
    public uint ProcessId { get; set; } = 4242;
    public nint NativeWindowHandle { get; set; } = 0x1000;
    public bool IsPassword { get; set; }
    public FakeUiaElement? Parent { get; set; }
    public FakeUiaTextPattern? TextPattern { get; set; } = new();
    public Exception? ThrowOnDisposeException { get; set; }
    public bool IsDisposed { get; private set; }

    public IUiaElement? GetParent(int maxDepth) => Parent;

    public IUiaTextPattern? GetTextPattern() => TextPattern;

    public void Dispose()
    {
        IsDisposed = true;
        if (ThrowOnDisposeException != null) throw ThrowOnDisposeException;
    }
}

internal sealed class FakeUiaTextPattern : IUiaTextPattern
{
    public bool SupportsSelection { get; set; } = true;
    public List<FakeUiaTextRange> Ranges { get; set; } = [];
    public Exception? ThrowOnDisposeException { get; set; }
    public bool IsDisposed { get; private set; }

    public IReadOnlyList<IUiaTextRange> GetSelection() => Ranges;

    public void Dispose()
    {
        IsDisposed = true;
        if (ThrowOnDisposeException != null) throw ThrowOnDisposeException;
    }
}

internal sealed class FakeUiaTextRange : IUiaTextRange
{
    public string? Text { get; set; }
    public Func<int, string?>? GetTextCallback { get; set; }
    public Exception? ThrowOnDisposeException { get; set; }
    public bool IsDisposed { get; private set; }
    public int LastRequestedMaxLength { get; private set; }

    public FakeUiaTextRange(string? text = null)
    {
        Text = text;
    }

    public string? GetText(int maxLength)
    {
        LastRequestedMaxLength = maxLength;
        return GetTextCallback?.Invoke(maxLength) ?? Text;
    }

    public void Dispose()
    {
        IsDisposed = true;
        if (ThrowOnDisposeException != null) throw ThrowOnDisposeException;
    }
}

internal sealed class FakeTargetWindowValidator : ITargetWindowValidator
{
    public bool DefaultIsValid { get; set; } = true;
    public Queue<bool> Responses { get; } = new();
    public int TotalCalls { get; private set; }
    public Action<nint, uint>? OnCall { get; set; }

    public bool IsTargetValidAndForeground(nint expectedWindow, uint expectedProcessId)
    {
        TotalCalls++;
        OnCall?.Invoke(expectedWindow, expectedProcessId);
        if (Responses.Count > 0)
        {
            return Responses.Dequeue();
        }
        return DefaultIsValid;
    }
}

internal sealed class FakeUiaProcessRunner : IUiaProcessRunner
{
    public FakeUiaProcessSession Session { get; set; } = new();
    public Exception? ThrowOnStart { get; set; }
    public Func<CancellationToken, Task<IUiaProcessSession>>? StartAsyncFunc { get; set; }
    public int TotalStartCalls { get; private set; }

    public Task<IUiaProcessSession> StartAsync(CancellationToken cancellationToken)
    {
        TotalStartCalls++;
        cancellationToken.ThrowIfCancellationRequested();
        if (StartAsyncFunc != null)
        {
            return StartAsyncFunc(cancellationToken);
        }

        if (ThrowOnStart != null)
        {
            throw ThrowOnStart;
        }

        return Task.FromResult<IUiaProcessSession>(Session);
    }
}

internal sealed class FakeUiaProcessSession : IUiaProcessSession
{
    private readonly System.IO.Pipelines.Pipe _stdinPipe = new();
    private readonly System.IO.Pipelines.Pipe _stdoutPipe = new();
    private readonly SemaphoreSlim _exitGate = new(0);
    private Stream _standardOutput;

    public FakeUiaProcessSession()
    {
        _standardOutput = new ObservableReadStream(_stdoutPipe.Reader.AsStream(), () => StdoutReadStarted.TrySetResult());
    }

    public void AppendTrailingStreamFault(Exception faultException)
    {
        _standardOutput = new FaultingAfterBytesStream(_standardOutput, faultException);
    }

    public TaskCompletionSource StdoutReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource WaitForExitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Stream StandardInput => _stdinPipe.Writer.AsStream();
    public Stream StandardOutput => _standardOutput;

    public Stream TestStdinReader => _stdinPipe.Reader.AsStream();
    public Stream TestStdoutWriter => _stdoutPipe.Writer.AsStream();

    public bool HasExited { get; set; }
    public int ExitCode { get; set; }
    public bool KillCalled { get; private set; }
    public Exception? ThrowOnKillException { get; set; }
    public bool Disposed { get; private set; }
    public bool RefuseToExit { get; set; }
    public TimeSpan ExitDelay { get; set; } = TimeSpan.Zero;

    public void EnqueueLine(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        TestStdoutWriter.Write(bytes);
        TestStdoutWriter.Flush();
    }

    public void EnqueueReady()
    {
        EnqueueLine(UiaProtocolCodec.SerializeReady());
    }

    public void EnqueueReply(UiaReplyFrame reply)
    {
        EnqueueLine(UiaProtocolCodec.SerializeReply(reply));
    }

    public void SignalExit(Exception? stdoutException = null)
    {
        HasExited = true;
        _stdoutPipe.Writer.Complete(stdoutException);
        _stdinPipe.Writer.Complete();
        if (_exitGate.CurrentCount == 0)
        {
            _exitGate.Release();
        }
    }

    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        WaitForExitStarted.TrySetResult();

        if (RefuseToExit)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (HasExited)
        {
            return;
        }

        if (ExitDelay > TimeSpan.Zero)
        {
            await Task.Delay(ExitDelay, cancellationToken).ConfigureAwait(false);
            HasExited = true;
            _stdoutPipe.Writer.Complete();
            return;
        }

        if (WaitForExitFunc != null)
        {
            await WaitForExitFunc(cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await _exitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Gate already disposed
        }
        HasExited = true;
        _stdoutPipe.Writer.Complete();
    }

    public Func<CancellationToken, Task>? WaitForExitFunc { get; set; }
    public Action? OnKill { get; set; }
    public Action? OnDispose { get; set; }

    public void Kill()
    {
        KillCalled = true;
        ExitCode = -1; // Terminated by kill gives non-zero exit code -1 like real Windows Process.Kill
        OnKill?.Invoke();
        if (ThrowOnKillException != null)
        {
            throw ThrowOnKillException;
        }
        if (!RefuseToExit)
        {
            HasExited = true;
            _stdoutPipe.Writer.Complete();
            _stdinPipe.Writer.Complete();
            try
            {
                if (_exitGate.CurrentCount == 0)
                {
                    _exitGate.Release();
                }
            }
            catch (ObjectDisposedException)
            {
                // Gate already disposed
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        OnDispose?.Invoke();
        _stdoutPipe.Writer.Complete();
        _stdinPipe.Writer.Complete();
        _stdoutPipe.Reader.Complete();
        _stdinPipe.Reader.Complete();
        try
        {
            _exitGate.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Gate already disposed
        }
        return ValueTask.CompletedTask;
    }

    private sealed class ObservableReadStream(Stream inner, Action onRead) : Stream
    {
        private int _readStarted;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Interlocked.Exchange(ref _readStarted, 1) == 0) onRead();
            return inner.Read(buffer, offset, count);
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _readStarted, 1) == 0) onRead();
            return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _readStarted, 1) == 0) onRead();
            return await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

internal sealed class FaultingAfterBytesStream(Stream inner, Exception faultException) : Stream
{
        private bool _faulted;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            if (read == 0 && !_faulted)
            {
                _faulted = true;
                throw faultException;
            }
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0 && !_faulted)
            {
                _faulted = true;
                throw faultException;
            }
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

internal sealed class TrackedDisposable : IDisposable
{
    public int DisposeCallCount { get; private set; }
    public Exception? ThrowOnDispose { get; set; }

    public void Dispose()
    {
        DisposeCallCount++;
        if (ThrowOnDispose != null) throw ThrowOnDispose;
    }
}

internal sealed class FakeProcessAdapter : IProcessAdapter
{
    private readonly System.IO.Pipelines.Pipe _stdinPipe = new();
    private readonly System.IO.Pipelines.Pipe _stdoutPipe = new();
    private readonly System.IO.Pipelines.Pipe _stderrPipe = new();
    private readonly SemaphoreSlim _exitGate = new(0);

    public bool StartResult { get; set; } = true;
    public Exception? ThrowOnStart { get; set; }
    public bool HasExited { get; set; }
    public int ExitCode { get; set; }
    public bool WaitForExitResult { get; set; } = true;
    public Exception? ThrowOnWaitForExit { get; set; }
    public Exception? ThrowOnKill { get; set; }
    public Exception? ThrowOnDispose { get; set; }

    public int StartCallCount { get; private set; }
    public int KillCallCount { get; private set; }
    public int WaitForExitCallCount { get; private set; }
    public int DisposeCallCount { get; private set; }

    public Stream StandardInput => _stdinPipe.Writer.AsStream();
    public Stream StandardOutput => _stdoutPipe.Reader.AsStream();
    public Stream StandardError => _stderrPipe.Reader.AsStream();

    public Stream TestStdinReader => _stdinPipe.Reader.AsStream();
    public Stream TestStdoutWriter => _stdoutPipe.Writer.AsStream();
    public Stream TestStderrWriter => _stderrPipe.Writer.AsStream();

    public IDisposable StandardInputDisposable { get; set; }
    public IDisposable StandardOutputDisposable { get; set; }

    public FakeProcessAdapter()
    {
        StandardInputDisposable = new TrackedDisposable();
        StandardOutputDisposable = new TrackedDisposable();
    }

    public void EnqueueLine(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        TestStdoutWriter.Write(bytes);
        TestStdoutWriter.Flush();
    }

    public void EnqueueReady()
    {
        EnqueueLine(UiaProtocolCodec.SerializeReady());
    }

    public void EnqueueReply(UiaReplyFrame reply)
    {
        EnqueueLine(UiaProtocolCodec.SerializeReply(reply));
    }

    public void SignalExit()
    {
        HasExited = true;
        _stdoutPipe.Writer.Complete();
        _stdinPipe.Writer.Complete();
        _stderrPipe.Writer.Complete();
        if (_exitGate.CurrentCount == 0)
        {
            _exitGate.Release();
        }
    }

    public bool Start()
    {
        StartCallCount++;
        if (ThrowOnStart != null) throw ThrowOnStart;
        return StartResult;
    }

    public void Kill()
    {
        KillCallCount++;
        if (ThrowOnKill != null) throw ThrowOnKill;
        HasExited = true;
        ExitCode = -1;
        _stdoutPipe.Writer.Complete();
        _stdinPipe.Writer.Complete();
        _stderrPipe.Writer.Complete();
        if (_exitGate.CurrentCount == 0)
        {
            _exitGate.Release();
        }
    }

    public bool WaitForExit(int milliseconds)
    {
        WaitForExitCallCount++;
        if (ThrowOnWaitForExit != null) throw ThrowOnWaitForExit;
        if (WaitForExitResult) HasExited = true;
        return WaitForExitResult;
    }

    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        WaitForExitCallCount++;
        if (ThrowOnWaitForExit != null) throw ThrowOnWaitForExit;
        if (!WaitForExitResult)
        {
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            throw new OperationCanceledException("Process exit wait timed out.", cancellationToken);
        }
        if (HasExited) return;
        try
        {
            await _exitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }
        HasExited = true;
    }

    public void Dispose()
    {
        DisposeCallCount++;
        _stdoutPipe.Writer.Complete();
        _stdinPipe.Writer.Complete();
        _stderrPipe.Writer.Complete();
        _stdoutPipe.Reader.Complete();
        _stdinPipe.Reader.Complete();
        _stderrPipe.Reader.Complete();
        try { _exitGate.Dispose(); } catch { }
        if (ThrowOnDispose != null) throw ThrowOnDispose;
    }
}

