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
                catch (IOException)
                {
                    // Pipe broken / closed
                }
                catch (ObjectDisposedException)
                {
                    // Stream disposed
                }
            }, CancellationToken.None);

            return Task.FromResult<IUiaProcessSession>(new RealUiaProcessSession(process, stderrDrainTask));
        }
        catch
        {
            try { process.Kill(); } catch { }
            try { process.Dispose(); } catch { }
            throw;
        }
    }
}

internal sealed class RealUiaProcessSession : IUiaProcessSession
{
    private readonly Process _process;
    private readonly Task _stderrDrainTask;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private bool _stdinDisposed;
    private bool _stdoutDisposed;
    private bool _stderrDrained;
    private bool _processDisposed;
    private bool _hasExited;
    private int _exitCode = -1;

    public RealUiaProcessSession(Process process, Task stderrDrainTask)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _stderrDrainTask = stderrDrainTask ?? throw new ArgumentNullException(nameof(stderrDrainTask));
    }

    public Stream StandardInput => _process.StandardInput.BaseStream;
    public Stream StandardOutput => _process.StandardOutput.BaseStream;

    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        _hasExited = true;
        _exitCode = _process.ExitCode;

        await _stderrDrainTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        _stderrDrained = true;
    }

    public bool HasExited
    {
        get
        {
            if (_hasExited) return true;
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
        if (HasExited) return;

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
            if (_disposeTask != null)
            {
                task = _disposeTask;
            }
            else
            {
                task = DoDisposeAsyncCore();
                _disposeTask = task;
            }
        }
        return new ValueTask(task);
    }

    private async Task DoDisposeAsyncCore()
    {
        List<Exception>? exceptions = null;

        if (!_stdinDisposed)
        {
            try
            {
                _process.StandardInput.Dispose();
                _stdinDisposed = true;
            }
            catch (ObjectDisposedException)
            {
                _stdinDisposed = true;
            }
            catch (Exception ex)
            {
                (exceptions ??= new()).Add(ex);
            }
        }

        if (!_stdoutDisposed)
        {
            try
            {
                _process.StandardOutput.Dispose();
                _stdoutDisposed = true;
            }
            catch (ObjectDisposedException)
            {
                _stdoutDisposed = true;
            }
            catch (Exception ex)
            {
                (exceptions ??= new()).Add(ex);
            }
        }

        if (!_stderrDrained)
        {
            try
            {
                await _stderrDrainTask.ConfigureAwait(false);
                _stderrDrained = true;
            }
            catch (Exception ex)
            {
                (exceptions ??= new()).Add(ex);
            }
        }

        if (!_processDisposed)
        {
            try
            {
                if (!_hasExited && _process.HasExited)
                {
                    _hasExited = true;
                    _exitCode = _process.ExitCode;
                }
            }
            catch (Exception ex)
            {
                (exceptions ??= new()).Add(ex);
            }

            try
            {
                _process.Dispose();
                _processDisposed = true;
            }
            catch (ObjectDisposedException)
            {
                _processDisposed = true;
            }
            catch (Exception ex)
            {
                (exceptions ??= new()).Add(ex);
            }
        }

        if (exceptions != null && exceptions.Count > 0)
        {
            lock (_disposeGate)
            {
                _disposeTask = null; // 允许重试未完成的释放阶段
            }
            throw new AggregateException("Failed to dispose one or more process session resources.", exceptions);
        }
    }
}
