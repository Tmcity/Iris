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
        _ = Task.Run(async () =>
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
            catch
            {
                // 静默忽略 stderr 读取异常
            }
        }, CancellationToken.None);

        return Task.FromResult<IUiaProcessSession>(new RealUiaProcessSession(process));
    }
}

internal sealed class RealUiaProcessSession : IUiaProcessSession
{
    private readonly Process _process;

    public RealUiaProcessSession(Process process)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
    }

    public Stream StandardInput => _process.StandardInput.BaseStream;
    public Stream StandardOutput => _process.StandardOutput.BaseStream;

    public Task WaitForExitAsync(CancellationToken cancellationToken) =>
        _process.WaitForExitAsync(cancellationToken);

    public bool HasExited => _process.HasExited;
    public int ExitCode => _process.ExitCode;

    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // 进程已提前退出
        }
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            _process.Dispose();
        }
        catch
        {
            // ignore
        }
        return ValueTask.CompletedTask;
    }
}
