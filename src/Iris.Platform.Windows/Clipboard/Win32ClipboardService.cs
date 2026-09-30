using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Microsoft.Extensions.Logging;

namespace Iris.Platform.Windows.Clipboard;

internal sealed class Win32ClipboardService : IClipboardService, IDisposable
{
    private const int MaxAttempts = 10;
    private const int RetryDelayMs = 30;
    private const int MaxTextCharacters = 4_194_304;
    private const ulong MaxTextBytes = 8_388_610UL;

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
            string? resultText = null;

            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (success, busy, text) = await Task.Run(() => ExecuteGetTextAttempt(), CancellationToken.None).ConfigureAwait(false);
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

            nuint byteSize = checked((nuint)((text.Length + 1) * sizeof(char)));
            nint hGlobal = _adapter.GlobalAlloc(DibV5Helper.GMEM_MOVEABLE, byteSize);
            if (hGlobal == 0)
            {
                int err = _adapter.GetLastError();
                throw new IrisException(ErrorCode.StorageFailure, "Failed to allocate global memory for clipboard text.", detail: $"GlobalAlloc error {err}");
            }

            bool hGlobalOwnedBySystem = false;
            try
            {
                nint ptr = _adapter.GlobalLock(hGlobal);
                if (ptr == 0)
                {
                    int err = _adapter.GetLastError();
                    throw new IrisException(ErrorCode.StorageFailure, "Failed to lock global memory for clipboard text.", detail: $"GlobalLock error {err}");
                }

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
                finally
                {
                    _adapter.GlobalUnlock(hGlobal);
                }

                await ExecuteWriteLoopAsync(DibV5Helper.CF_UNICODETEXT, hGlobal, () => hGlobalOwnedBySystem = true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (!hGlobalOwnedBySystem && hGlobal != 0)
                {
                    _adapter.GlobalFree(hGlobal);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetImageAsync(ImageData image, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Image validation and decoding occurs before acquiring the serial gate
        byte[] dibV5Bytes = await WindowsImageDecoder.DecodeToDibV5Async(image).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            nuint byteSize = checked((nuint)dibV5Bytes.Length);
            nint hGlobal = _adapter.GlobalAlloc(DibV5Helper.GMEM_MOVEABLE, byteSize);
            if (hGlobal == 0)
            {
                int err = _adapter.GetLastError();
                throw new IrisException(ErrorCode.StorageFailure, "Failed to allocate global memory for clipboard image.", detail: $"GlobalAlloc error {err}");
            }

            bool hGlobalOwnedBySystem = false;
            try
            {
                nint ptr = _adapter.GlobalLock(hGlobal);
                if (ptr == 0)
                {
                    int err = _adapter.GetLastError();
                    throw new IrisException(ErrorCode.StorageFailure, "Failed to lock global memory for clipboard image.", detail: $"GlobalLock error {err}");
                }

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
                finally
                {
                    _adapter.GlobalUnlock(hGlobal);
                }

                await ExecuteWriteLoopAsync(DibV5Helper.CF_DIBV5, hGlobal, () => hGlobalOwnedBySystem = true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (!hGlobalOwnedBySystem && hGlobal != 0)
                {
                    _adapter.GlobalFree(hGlobal);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    private async Task ExecuteWriteLoopAsync(uint format, nint hGlobal, Action onSystemOwned, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (success, busy) = await Task.Run(() => ExecuteWriteAttempt(format, hGlobal, onSystemOwned, cancellationToken), CancellationToken.None).ConfigureAwait(false);
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

        bool closeFailed = false;
        int closeError = 0;
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
                    result = (true, false, null);
                }
                else
                {
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
                    finally
                    {
                        _adapter.GlobalUnlock(hData);
                    }
                }
            }
        }
        finally
        {
            if (!_adapter.CloseClipboard())
            {
                closeFailed = true;
                closeError = _adapter.GetLastError();
            }
        }

        if (closeFailed)
        {
            throw new IrisException(ErrorCode.StorageFailure, "Failed to close clipboard.", detail: $"CloseClipboard error {closeError}");
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
        bool closeFailed = false;
        int closeError = 0;
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
        finally
        {
            if (opened)
            {
                if (!_adapter.CloseClipboard())
                {
                    closeFailed = true;
                    closeError = _adapter.GetLastError();
                }
            }

            _adapter.DestroyWindow(ownerHwnd);
        }

        if (closeFailed)
        {
            throw new IrisException(ErrorCode.StorageFailure, "Failed to close clipboard.", detail: $"CloseClipboard error {closeError}");
        }

        return result;
    }
}
