using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Errors;

namespace Iris.Core.IO;

/// <summary>
/// 提供跨进程安全、防断电的原子文件写入工具。
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private const int SharingViolationHResult = unchecked((int)0x80070020);
    private const int LockViolationHResult = unchecked((int)0x80070021);
    private const int MaxRetries = 5;
    private const int RetryDelayMs = 50;

    /// <summary>原子写入：path + ".tmp"（FileOptions.WriteThrough，写完 Flush(true)）→ backupPath 非空且 path 已存在时复制为 backupPath → File.Move(tmp, path, overwrite: true)。</summary>
    public static async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> content, string? backupPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var tmpPath = path + ".tmp";

        try
        {
            // 阶段 1：写入 tmp 文件（响应取消）
            await ExecuteWithRetryAsync(async () =>
            {
                await using var fileStream = new FileStream(
                    tmpPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.WriteThrough | FileOptions.Asynchronous);

                await fileStream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                fileStream.Flush(flushToDisk: true);
            }, cancellationToken).ConfigureAwait(false);

            // 阶段 2 & 3：tmp 写完后不再响应取消，确保不留下中间状态
            if (!string.IsNullOrEmpty(backupPath) && File.Exists(path))
            {
                await ExecuteWithRetryAsync(() =>
                {
                    File.Copy(path, backupPath, overwrite: true);
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
            }

            await ExecuteWithRetryAsync(() =>
            {
                File.Move(tmpPath, path, overwrite: true);
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryDelete(tmpPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(tmpPath);
            throw new IrisException(ErrorCode.StorageFailure, ErrorMessages.GetDefault(ErrorCode.StorageFailure), ex, detail: path);
        }
    }

    /// <summary>同上，内容按 UTF-8（无 BOM）编码。</summary>
    public static Task WriteAllTextAsync(string path, string content, string? backupPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var bytes = Utf8WithoutBom.GetBytes(content);
        return WriteAllBytesAsync(path, bytes, backupPath, cancellationToken);
    }

    /// <summary>WriteAllBytesAsync 的同步版本，语义完全相同（tmp + WriteThrough + Flush(true) → 可选备份 → Move）。</summary>
    public static void WriteAllBytes(string path, ReadOnlyMemory<byte> content, string? backupPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var tmpPath = path + ".tmp";

        try
        {
            // 阶段 1：写入 tmp 文件
            ExecuteWithRetry(() =>
            {
                using var fileStream = new FileStream(
                    tmpPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.WriteThrough);

                fileStream.Write(content.Span);
                fileStream.Flush(flushToDisk: true);
            });

            // 阶段 2 & 3：tmp 写完后执行备份与替换
            if (!string.IsNullOrEmpty(backupPath) && File.Exists(path))
            {
                ExecuteWithRetry(() =>
                {
                    File.Copy(path, backupPath, overwrite: true);
                });
            }

            ExecuteWithRetry(() =>
            {
                File.Move(tmpPath, path, overwrite: true);
            });
        }
        catch (Exception ex)
        {
            TryDelete(tmpPath);
            throw new IrisException(ErrorCode.StorageFailure, ErrorMessages.GetDefault(ErrorCode.StorageFailure), ex, detail: path);
        }
    }

    /// <summary>带重试的复制（重试策略与 WriteAllBytesAsync 相同）。失败抛 IrisException(StorageFailure, detail: "源路径 → 目标路径")。</summary>
    public static async Task CopyAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        try
        {
            await ExecuteWithRetryAsync(() =>
            {
                File.Copy(sourcePath, destinationPath, overwrite);
                return Task.CompletedTask;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new IrisException(
                ErrorCode.StorageFailure,
                ErrorMessages.GetDefault(ErrorCode.StorageFailure),
                ex,
                detail: FormattableString.Invariant($"{sourcePath} → {destinationPath}"));
        }
    }

    /// <summary>带重试的移动，其余同上。</summary>
    public static async Task MoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        try
        {
            await ExecuteWithRetryAsync(() =>
            {
                File.Move(sourcePath, destinationPath, overwrite);
                return Task.CompletedTask;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new IrisException(
                ErrorCode.StorageFailure,
                ErrorMessages.GetDefault(ErrorCode.StorageFailure),
                ex,
                detail: FormattableString.Invariant($"{sourcePath} → {destinationPath}"));
        }
    }

    /// <summary>MoveAsync 的同步版本。</summary>
    public static void Move(string sourcePath, string destinationPath, bool overwrite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        try
        {
            ExecuteWithRetry(() =>
            {
                File.Move(sourcePath, destinationPath, overwrite);
            });
        }
        catch (Exception ex)
        {
            throw new IrisException(
                ErrorCode.StorageFailure,
                ErrorMessages.GetDefault(ErrorCode.StorageFailure),
                ex,
                detail: FormattableString.Invariant($"{sourcePath} → {destinationPath}"));
        }
    }

    /// <summary>带重试的全部字节读取（重试策略与 WriteAllBytes 相同）。失败抛 IrisException(StorageFailure, detail: path)。</summary>
    public static byte[] ReadAllBytes(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            byte[]? result = null;
            ExecuteWithRetry(() =>
            {
                result = File.ReadAllBytes(path);
            });
            return result!;
        }
        catch (Exception ex)
        {
            throw new IrisException(
                ErrorCode.StorageFailure,
                ErrorMessages.GetDefault(ErrorCode.StorageFailure),
                ex,
                detail: path);
        }
    }

    /// <summary>带重试的全部字节异步读取（重试策略与 WriteAllBytesAsync 相同）。失败抛 IrisException(StorageFailure, detail: path)。</summary>
    public static async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            byte[]? result = null;
            await ExecuteWithRetryAsync(async () =>
            {
                result = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            return result!;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new IrisException(
                ErrorCode.StorageFailure,
                ErrorMessages.GetDefault(ErrorCode.StorageFailure),
                ex,
                detail: path);
        }
    }

    private static void ExecuteWithRetry(Action action)
    {
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (attempt < MaxRetries && IsTransient(ex))
            {
                Thread.Sleep(RetryDelayMs);
            }
        }
    }

    private static async Task ExecuteWithRetryAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await action().ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < MaxRetries && IsTransient(ex))
            {
                await Task.Delay(RetryDelayMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(Exception ex)
    {
        if (ex is UnauthorizedAccessException)
        {
            return true;
        }

        if (ex is IOException io)
        {
            return io.HResult == SharingViolationHResult || io.HResult == LockViolationHResult;
        }

        return false;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 尽力删除，忽略失败
        }
    }
}
