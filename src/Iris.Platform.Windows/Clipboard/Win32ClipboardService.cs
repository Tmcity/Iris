using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Microsoft.Extensions.Logging;

namespace Iris.Platform.Windows.Clipboard;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "SemaphoreSlim does not allocate unmanaged resources unless AvailableWaitHandle is accessed; disposing it while async operations are in flight throws ObjectDisposedException.")]
internal sealed class Win32ClipboardService : IClipboardService
{
    private const int MaxAttempts = 10;
    private const int RetryDelayMs = 30;
    private const int MaxTextCharacters = 4_194_304;
    private const ulong MaxTextBytes = 8_388_610UL;

    private static readonly Action<ILogger, string, int, Exception?> s_logCleanupFailure =
        LoggerMessage.Define<string, int>(
            LogLevel.Warning,
            new EventId(1, nameof(Win32ClipboardService)),
            "Failed to {Action} during clipboard cleanup: error {ErrorCode}");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<Win32ClipboardService>? _logger;
    private readonly INativeClipboardAdapter _adapter;

    public Win32ClipboardService()
        : this(null, null, null)
    {
    }

    public Win32ClipboardService(TimeProvider? timeProvider, ILogger<Win32ClipboardService>? logger)
        : this(timeProvider, logger, null)
    {
    }

    internal Win32ClipboardService(TimeProvider? timeProvider, ILogger<Win32ClipboardService>? logger, INativeClipboardAdapter? adapter)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _adapter = adapter ?? NativeClipboardAdapter.Instance;
    }

    public uint GetSequenceNumber()
    {
        return _adapter.GetClipboardSequenceNumber();
    }

    public async Task<string?> GetTextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(() => CoreGetTextAsync(cancellationToken), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (text == null)
        {
            throw new IrisException(ErrorCode.InvalidArgument, "Text cannot be null.");
        }

        if (text.Contains('\0'))
        {
            throw new IrisException(ErrorCode.InvalidArgument, "Text cannot contain null characters.");
        }

        if (text.Length > MaxTextCharacters)
        {
            throw new IrisException(ErrorCode.InputTooLong, $"Text length exceeds the maximum allowed limit of {MaxTextCharacters} characters.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() => CoreSetTextAsync(text, cancellationToken), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetImageAsync(ImageData image, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (image == null || image.Bytes == null || image.Bytes.Length == 0)
        {
            throw new IrisException(ErrorCode.ImageInvalid, "Image data or bytes cannot be null or empty.");
        }

        if (!Enum.IsDefined(image.Format))
        {
            throw new IrisException(ErrorCode.ImageInvalid, "Unsupported or undefined image format.");
        }

        if (image.PixelWidth <= 0 || image.PixelHeight <= 0)
        {
            throw new IrisException(ErrorCode.ImageInvalid, "Image dimensions must be positive.");
        }

        // Preliminary budget check before acquiring the gate
        if (image.Bytes.Length > WindowsImageDecoder.MaxEncodedBytes)
        {
            throw new IrisException(ErrorCode.ImageTooLarge, "Image encoded bytes exceed maximum limit.");
        }

        if (image.PixelWidth > WindowsImageDecoder.MaxDimension || image.PixelHeight > WindowsImageDecoder.MaxDimension)
        {
            throw new IrisException(ErrorCode.ImageTooLarge, "Image dimensions exceed maximum limit.");
        }

        long requestedPixels = checked((long)image.PixelWidth * image.PixelHeight);
        if (requestedPixels > WindowsImageDecoder.MaxPixels)
        {
            throw new IrisException(ErrorCode.ImageTooLarge, "Total image pixels exceed maximum limit.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() => CoreSetImageAsync(image, cancellationToken), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> CoreGetTextAsync(CancellationToken cancellationToken)
    {
        string? resultText = null;

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (success, busy, text) = ExecuteGetTextAttempt();
            if (success)
            {
                resultText = text;
                break;
            }

            if (busy)
            {
                if (attempt < MaxAttempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(RetryDelayMs), _timeProvider, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new IrisException(ErrorCode.ClipboardBusy, "Clipboard is busy and could not be opened.");
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return resultText;
    }

    private async Task CoreSetTextAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        nuint byteSize = checked((nuint)((text.Length + 1) * sizeof(char)));
        nint hGlobal = _adapter.GlobalAlloc(DibV5Helper.GMEM_MOVEABLE, byteSize);
        if (hGlobal == 0)
        {
            int err = _adapter.GetLastError();
            throw new IrisException(ErrorCode.StorageFailure, "Failed to allocate global memory for clipboard text.", detail: $"GlobalAlloc error {err}");
        }

        bool hGlobalOwnedBySystem = false;
        bool primaryFailed = false;
        bool cleanupFailed = false;
        int cleanupError = 0;
        try
        {
            nint ptr = _adapter.GlobalLock(hGlobal);
            if (ptr == 0)
            {
                int err = _adapter.GetLastError();
                throw new IrisException(ErrorCode.StorageFailure, "Failed to lock global memory for clipboard text.", detail: $"GlobalLock error {err}");
            }

            bool unlockFailed = false;
            int unlockError = 0;
            bool copyFailed = false;
            try
            {
                unsafe
                {
                    fixed (char* pText = text)
                    {
                        Buffer.MemoryCopy(pText, (void*)ptr, byteSize, (nuint)(text.Length * sizeof(char)));
                    }
                    ((char*)ptr)[text.Length] = '\0';
                }
            }
            catch
            {
                copyFailed = true;
                throw;
            }
            finally
            {
                if (!_adapter.GlobalUnlock(hGlobal))
                {
                    int unlockErr = _adapter.GetLastError();
                    if (unlockErr != 0)
                    {
                        if (copyFailed)
                        {
                            if (_logger != null)
                            {
                                s_logCleanupFailure(_logger, "GlobalUnlock", unlockErr, null);
                            }
                        }
                        else
                        {
                            unlockFailed = true;
                            unlockError = unlockErr;
                        }
                    }
                }
            }

            if (unlockFailed)
            {
                throw new IrisException(ErrorCode.StorageFailure, "Failed to unlock global memory for clipboard text.", detail: $"GlobalUnlock error {unlockError}");
            }

            await ExecuteWriteLoopAsync(DibV5Helper.CF_UNICODETEXT, hGlobal, () => hGlobalOwnedBySystem = true, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            primaryFailed = true;
            throw;
        }
        finally
        {
            if (!hGlobalOwnedBySystem && hGlobal != 0)
            {
                if (_adapter.GlobalFree(hGlobal) != 0)
                {
                    cleanupFailed = true;
                    cleanupError = _adapter.GetLastError();
                    if (primaryFailed && _logger != null)
                    {
                        s_logCleanupFailure(_logger, "GlobalFree", cleanupError, null);
                    }
                }
            }
        }

        if (cleanupFailed && !primaryFailed)
        {
            throw new IrisException(ErrorCode.StorageFailure, "Failed to free global memory.", detail: $"GlobalFree error {cleanupError}");
        }
    }

    private async Task CoreSetImageAsync(ImageData image, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Image decoding occurs strictly on background thread under the serial gate
        byte[] dibV5Bytes = await WindowsImageDecoder.DecodeToDibV5Async(image, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        nuint byteSize = checked((nuint)dibV5Bytes.Length);
        nint hGlobal = _adapter.GlobalAlloc(DibV5Helper.GMEM_MOVEABLE, byteSize);
        if (hGlobal == 0)
        {
            int err = _adapter.GetLastError();
            throw new IrisException(ErrorCode.StorageFailure, "Failed to allocate global memory for clipboard image.", detail: $"GlobalAlloc error {err}");
        }

        bool hGlobalOwnedBySystem = false;
        bool primaryFailed = false;
        bool cleanupFailed = false;
        int cleanupError = 0;
        try
        {
            nint ptr = _adapter.GlobalLock(hGlobal);
            if (ptr == 0)
            {
                int err = _adapter.GetLastError();
                throw new IrisException(ErrorCode.StorageFailure, "Failed to lock global memory for clipboard image.", detail: $"GlobalLock error {err}");
            }

            bool unlockFailed = false;
            int unlockError = 0;
            bool copyFailed = false;
            try
            {
                unsafe
                {
                    fixed (byte* pDib = dibV5Bytes)
                    {
                        Buffer.MemoryCopy(pDib, (void*)ptr, byteSize, byteSize);
                    }
                }
            }
            catch
            {
                copyFailed = true;
                throw;
            }
            finally
            {
                if (!_adapter.GlobalUnlock(hGlobal))
                {
                    int unlockErr = _adapter.GetLastError();
                    if (unlockErr != 0)
                    {
                        if (copyFailed)
                        {
                            if (_logger != null)
                            {
                                s_logCleanupFailure(_logger, "GlobalUnlock", unlockErr, null);
                            }
                        }
                        else
                        {
                            unlockFailed = true;
                            unlockError = unlockErr;
                        }
                    }
                }
            }

            if (unlockFailed)
            {
                throw new IrisException(ErrorCode.StorageFailure, "Failed to unlock global memory for clipboard image.", detail: $"GlobalUnlock error {unlockError}");
            }

            await ExecuteWriteLoopAsync(DibV5Helper.CF_DIBV5, hGlobal, () => hGlobalOwnedBySystem = true, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            primaryFailed = true;
            throw;
        }
        finally
        {
            if (!hGlobalOwnedBySystem && hGlobal != 0)
            {
                if (_adapter.GlobalFree(hGlobal) != 0)
                {
                    cleanupFailed = true;
                    cleanupError = _adapter.GetLastError();
                    if (primaryFailed && _logger != null)
                    {
                        s_logCleanupFailure(_logger, "GlobalFree", cleanupError, null);
                    }
                }
            }
        }

        if (cleanupFailed && !primaryFailed)
        {
            throw new IrisException(ErrorCode.StorageFailure, "Failed to free global memory.", detail: $"GlobalFree error {cleanupError}");
        }
    }

    private async Task ExecuteWriteLoopAsync(uint format, nint hGlobal, Action onSystemOwned, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (success, busy) = ExecuteWriteAttempt(format, hGlobal, onSystemOwned, cancellationToken);
            if (success)
            {
                return;
            }

            if (busy)
            {
                if (attempt < MaxAttempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(RetryDelayMs), _timeProvider, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new IrisException(ErrorCode.ClipboardBusy, "Clipboard is busy and could not be opened.");
                }
            }
        }
    }

    private (bool Success, bool Busy, string? Text) ExecuteGetTextAttempt()
    {
        if (!_adapter.OpenClipboard(0))
        {
            return (false, true, null);
        }

        bool primaryFailed = false;
        bool cleanupFailed = false;
        int cleanupError = 0;
        (bool Success, bool Busy, string? Text) result = default;

        try
        {
            if (!_adapter.IsClipboardFormatAvailable(DibV5Helper.CF_UNICODETEXT))
            {
                result = (true, false, null);
            }
            else
            {
                nint hData = _adapter.GetClipboardData(DibV5Helper.CF_UNICODETEXT);
                if (hData == 0)
                {
                    int err = _adapter.GetLastError();
                    throw new IrisException(ErrorCode.StorageFailure, "Failed to get clipboard data.", detail: $"GetClipboardData error {err}");
                }

                nuint size = _adapter.GlobalSize(hData);
                if (size < 2 || (size % 2 != 0))
                {
                    throw new IrisException(ErrorCode.InvalidResponse, "Clipboard text data block is corrupt or has invalid byte length.");
                }

                if ((ulong)size > MaxTextBytes)
                {
                    throw new IrisException(ErrorCode.InputTooLong, $"Clipboard text size {size} exceeds maximum allowable {MaxTextBytes} bytes.");
                }

                nint ptr = _adapter.GlobalLock(hData);
                if (ptr == 0)
                {
                    int err = _adapter.GetLastError();
                    throw new IrisException(ErrorCode.StorageFailure, "Failed to lock clipboard data handle.", detail: $"GlobalLock error {err}");
                }

                bool unlockFailed = false;
                int unlockError = 0;
                bool readFailed = false;
                try
                {
                    int maxChars = (int)(size / 2);
                    int nullIndex = -1;
                    unsafe
                    {
                        char* pChar = (char*)ptr;
                        for (int i = 0; i < maxChars; i++)
                        {
                            if (pChar[i] == '\0')
                            {
                                nullIndex = i;
                                break;
                            }
                        }
                    }

                    if (nullIndex == -1)
                    {
                        throw new IrisException(ErrorCode.InvalidResponse, "Clipboard text data block does not contain a null terminator within bounds.");
                    }

                    if (nullIndex > MaxTextCharacters)
                    {
                        throw new IrisException(ErrorCode.InputTooLong, $"Clipboard text length exceeds maximum allowable limit of {MaxTextCharacters} characters.");
                    }

                    string text = nullIndex == 0 ? string.Empty : Marshal.PtrToStringUni(ptr, nullIndex)!;
                    result = (true, false, text);
                }
                catch
                {
                    readFailed = true;
                    throw;
                }
                finally
                {
                    if (!_adapter.GlobalUnlock(hData))
                    {
                        int unlockErr = _adapter.GetLastError();
                        if (unlockErr != 0)
                        {
                            if (readFailed)
                            {
                                if (_logger != null)
                                {
                                    s_logCleanupFailure(_logger, "GlobalUnlock", unlockErr, null);
                                }
                            }
                            else
                            {
                                unlockFailed = true;
                                unlockError = unlockErr;
                            }
                        }
                    }
                }

                if (unlockFailed)
                {
                    throw new IrisException(ErrorCode.StorageFailure, "Failed to unlock clipboard data handle.", detail: $"GlobalUnlock error {unlockError}");
                }
            }
        }
        catch
        {
            primaryFailed = true;
            throw;
        }
        finally
        {
            if (!_adapter.CloseClipboard())
            {
                cleanupFailed = true;
                cleanupError = _adapter.GetLastError();
                if (primaryFailed && _logger != null)
                {
                    s_logCleanupFailure(_logger, "CloseClipboard", cleanupError, null);
                }
            }
        }

        if (cleanupFailed && !primaryFailed)
        {
            throw new IrisException(ErrorCode.StorageFailure, "Failed to close clipboard.", detail: $"CloseClipboard error {cleanupError}");
        }

        return result;
    }

    private (bool Success, bool Busy) ExecuteWriteAttempt(uint format, nint hGlobal, Action onSystemOwned, CancellationToken cancellationToken)
    {
        nint ownerHwnd = _adapter.CreateOwnerWindow();
        if (ownerHwnd == 0)
        {
            int err = _adapter.GetLastError();
            throw new IrisException(ErrorCode.StorageFailure, "Failed to create clipboard owner window.", detail: $"CreateWindowEx error {err}");
        }

        bool opened = false;
        bool primaryFailed = false;
        bool closeFailed = false;
        int closeError = 0;
        bool destroyFailed = false;
        int destroyError = 0;
        (bool Success, bool Busy) result = default;

        try
        {
            if (!_adapter.OpenClipboard(ownerHwnd))
            {
                result = (false, true);
            }
            else
            {
                opened = true;

                // Commit point: check cancellation after Open, immediately before EmptyClipboard
                cancellationToken.ThrowIfCancellationRequested();

                if (!_adapter.EmptyClipboard())
                {
                    int err = _adapter.GetLastError();
                    throw new IrisException(ErrorCode.StorageFailure, "Failed to empty clipboard.", detail: $"EmptyClipboard error {err}");
                }

                // Empty succeeded: Commit phase entered! Cannot skip Set due to late cancellation.
                nint setHandle = _adapter.SetClipboardData(format, hGlobal);
                if (setHandle == 0)
                {
                    int err = _adapter.GetLastError();
                    throw new IrisException(ErrorCode.StorageFailure, "Failed to set clipboard data.", detail: $"SetClipboardData error {err}");
                }

                onSystemOwned();
                result = (true, false);
            }
        }
        catch
        {
            primaryFailed = true;
            throw;
        }
        finally
        {
            if (opened)
            {
                if (!_adapter.CloseClipboard())
                {
                    closeFailed = true;
                    closeError = _adapter.GetLastError();
                    if (primaryFailed && _logger != null)
                    {
                        s_logCleanupFailure(_logger, "CloseClipboard", closeError, null);
                    }
                }
            }

            if (!_adapter.DestroyWindow(ownerHwnd))
            {
                destroyFailed = true;
                destroyError = _adapter.GetLastError();
                if ((primaryFailed || closeFailed) && _logger != null)
                {
                    s_logCleanupFailure(_logger, "DestroyWindow", destroyError, null);
                }
            }
        }

        if (closeFailed && !primaryFailed)
        {
            throw new IrisException(ErrorCode.StorageFailure, "Failed to close clipboard.", detail: $"CloseClipboard error {closeError}");
        }

        if (destroyFailed && !primaryFailed && !closeFailed)
        {
            throw new IrisException(ErrorCode.StorageFailure, "Failed to destroy clipboard owner window.", detail: $"DestroyWindow error {destroyError}");
        }

        return result;
    }
}
