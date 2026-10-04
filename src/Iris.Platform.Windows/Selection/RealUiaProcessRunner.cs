using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Iris.Platform.Windows.Selection;

internal sealed class RealUiaProcessRunner : IUiaProcessRunner
{
    public static readonly RealUiaProcessRunner Instance = new();

    private readonly string _executablePath;

    public RealUiaProcessRunner(string? executablePath = null)
    {
        _executablePath = executablePath ?? Path.Combine(AppContext.BaseDirectory, "uia", "Iris.UiaHost.exe");
    }

    public Task<IUiaProcessSession> StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(_executablePath))
        {
            throw new FileNotFoundException($"Iris.UiaHost executable not found at '{_executablePath}'.", _executablePath);
        }

        var psi = new ProcessStartInfo
        {
            FileName = _executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start Iris.UiaHost process.");
        }

        try
        {
            // 持续在后台排空 stderr，上限至多 4KiB 诊断预算，不回显内容
            var stderrDrainTask = Task.Run(async () =>
            {
                try
                {
                    var buffer = new byte[512];
                    int totalRead = 0;
                    var stream = process.StandardError.BaseStream;
                    while (true)
                    {
                        int toRead = Math.Min(buffer.Length, UiaProtocolConstants.MaxStderrDrainBytes - totalRead);
                        if (toRead <= 0)
                        {
                            // 超过 4KiB 预算后继续排空但不保留
                            while (await stream.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false) > 0) { }
                            break;
                        }

                        int read = await stream.ReadAsync(buffer.AsMemory(0, toRead), CancellationToken.None).ConfigureAwait(false);
                        if (read <= 0) break;
                        totalRead += read;
                    }
                }
                catch (IOException ex) when (UiaProtocolFrameReader.IsPipeClosedException(ex))
                {
                    // 平台管道正常关闭
                }
                catch (ObjectDisposedException)
                {
                    // 流已由本进程主动关闭
                }
            }, CancellationToken.None);

            return Task.FromResult<IUiaProcessSession>(new RealUiaProcessSession(process, stderrDrainTask));
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(1000);
                }
            }
            catch { }
            try { process.Dispose(); } catch { }
            throw;
        }
    }
}

internal sealed class RealUiaProcessSession : IUiaProcessSession
{
    private readonly Process? _process;
    private readonly Task _stderrDrainTask;
    private readonly Stream _stdinStream;
    private readonly IDisposable _stdinDisposable;
    private readonly Stream _stdoutStream;
    private readonly IDisposable _stdoutDisposable;
    private readonly IDisposable? _processDisposable;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private bool _stdinDisposed;
    private bool _stdoutDisposed;
    private bool _stderrDrainObserved;
    private bool _processDisposed;
    private bool _isFullyDisposed;
    private bool _hasExited;
    private int _exitCode = -1;

    public RealUiaProcessSession(Process process, Task stderrDrainTask)
        : this(
            process ?? throw new ArgumentNullException(nameof(process)),
            stderrDrainTask,
            process.StandardInput.BaseStream,
            process.StandardInput,
            process.StandardOutput.BaseStream,
            process.StandardOutput,
            process)
    {
    }

    internal RealUiaProcessSession(
        Process? process,
        Task stderrDrainTask,
        Stream stdinStream,
        IDisposable stdinDisposable,
        Stream stdoutStream,
        IDisposable stdoutDisposable,
        IDisposable? processDisposable = null)
    {
        _process = process;
        _stderrDrainTask = stderrDrainTask ?? throw new ArgumentNullException(nameof(stderrDrainTask));
        _stdinStream = stdinStream ?? throw new ArgumentNullException(nameof(stdinStream));
        _stdinDisposable = stdinDisposable ?? throw new ArgumentNullException(nameof(stdinDisposable));
        _stdoutStream = stdoutStream ?? throw new ArgumentNullException(nameof(stdoutStream));
        _stdoutDisposable = stdoutDisposable ?? throw new ArgumentNullException(nameof(stdoutDisposable));
        _processDisposable = processDisposable;
    }

    public Stream StandardInput => _stdinStream;
    public Stream StandardOutput => _stdoutStream;

    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        if (_process != null)
        {
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            _hasExited = true;
            _exitCode = _process.ExitCode;
        }

        try
        {
            await _stderrDrainTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            _stderrDrainObserved = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            _stderrDrainObserved = true;
            throw;
        }
    }

    public bool HasExited
    {
        get
        {
            if (_hasExited) return true;
            if (_process == null) return false;
            try
            {
                if (_process.HasExited)
                {
                    _hasExited = true;
                    _exitCode = _process.ExitCode;
                    return true;
                }
            }
            catch
            {
                // ignore
            }
            return false;
        }
    }

    public int ExitCode
    {
        get
        {
            if (_hasExited) return _exitCode;
            if (_process == null) return _exitCode;
            try
            {
                return _process.ExitCode;
            }
            catch
            {
                return _exitCode;
            }
        }
    }

    public void Kill()
    {
        if (HasExited || _process == null) return;

        try
        {
            _process.Kill();
        }
        catch (InvalidOperationException)
        {
            // Process exited between check and kill
        }
    }

    public ValueTask DisposeAsync()
    {
        Task task;
        lock (_disposeGate)
        {
            if (_isFullyDisposed)
            {
                return ValueTask.CompletedTask;
            }

            if (_disposeTask != null && !_disposeTask.IsCompleted)
            {
                return new ValueTask(_disposeTask);
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = tcs.Task;
            _ = DoDisposeAsyncCore(tcs);
            task = tcs.Task;
        }
        return new ValueTask(task);
    }

    private async Task DoDisposeAsyncCore(TaskCompletionSource tcs)
    {
        List<Exception>? exceptions = null;

        if (!_stdinDisposed)
        {
            try
            {
                _stdinDisposable.Dispose();
                _stdinDisposed = true;
            }
            catch (ObjectDisposedException)
            {
                _stdinDisposed = true;
            }
            catch (Exception ex)
            {
                (exceptions ??= []).Add(ex);
            }
        }

        if (!_stdoutDisposed)
        {
            try
            {
                _stdoutDisposable.Dispose();
                _stdoutDisposed = true;
            }
            catch (ObjectDisposedException)
            {
                _stdoutDisposed = true;
            }
            catch (Exception ex)
            {
                (exceptions ??= []).Add(ex);
            }
        }

        if (!_stderrDrainObserved)
        {
            try
            {
                await _stderrDrainTask.ConfigureAwait(false);
                _stderrDrainObserved = true;
            }
            catch (Exception ex)
            {
                _stderrDrainObserved = true;
                (exceptions ??= []).Add(ex);
            }
        }

        if (!_processDisposed)
        {
            try
            {
                if (!_hasExited && _process != null && _process.HasExited)
                {
                    _hasExited = true;
                    _exitCode = _process.ExitCode;
                }
            }
            catch (Exception ex)
            {
                (exceptions ??= []).Add(ex);
            }

            try
            {
                _processDisposable?.Dispose();
                _processDisposed = true;
            }
            catch (ObjectDisposedException)
            {
                _processDisposed = true;
            }
            catch (Exception ex)
            {
                (exceptions ??= []).Add(ex);
            }
        }

        bool hasFailures = exceptions != null && exceptions.Count > 0;
        lock (_disposeGate)
        {
            if (hasFailures)
            {
                _disposeTask = null; // 允许重试未完成的释放阶段
            }
            else
            {
                _isFullyDisposed = true;
            }
        }

        if (hasFailures)
        {
            tcs.TrySetException(new AggregateException("Failed to dispose one or more process session resources.", exceptions!));
        }
        else
        {
            tcs.TrySetResult();
        }
    }
}
