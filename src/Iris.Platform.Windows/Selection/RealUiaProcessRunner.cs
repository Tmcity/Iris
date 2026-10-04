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
}

internal sealed class RealUiaProcessSession : IUiaProcessSession
{
    private readonly Process _process;
    private readonly Task _stderrDrainTask;
    private int _disposed;
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

        try
        {
            await _stderrDrainTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // ignore stderr task errors during normal exit
        }
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (!_hasExited && _process.HasExited)
            {
                _hasExited = true;
                _exitCode = _process.ExitCode;
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            _process.StandardInput.Dispose();
            _process.StandardOutput.Dispose();
        }
        catch
        {
            // ignore
        }

        try
        {
            await _stderrDrainTask.ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }

        try
        {
            _process.Dispose();
        }
        catch
        {
            // ignore
        }
    }
}
