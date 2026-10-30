using System;
using System.Runtime.InteropServices;
using System.Threading;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Platform.Windows.Clipboard;

namespace Iris.Clipboard.IsolatedHelper;

internal sealed class RealIsolatedStationEnvironment : IIsolatedStationEnvironment
{
    public nint GetProcessWindowStation() => Program.GetProcessWindowStation();
    public nint GetThreadDesktop(uint threadId) => Program.GetThreadDesktop(threadId);
    public uint GetCurrentThreadId() => Program.GetCurrentThreadId();

    public nint CreateWindowStation(string? name, uint flags, uint access, out int error)
    {
        var h = Program.CreateWindowStationW(name, flags, access, 0);
        error = Marshal.GetLastWin32Error();
        return h;
    }

    public bool SetProcessWindowStation(nint h, out int error)
    {
        var res = Program.SetProcessWindowStation(h);
        error = res ? 0 : Marshal.GetLastWin32Error();
        return res;
    }

    public nint CreateDesktop(string name, uint flags, uint access, out int error)
    {
        var h = Program.CreateDesktopW(name, 0, 0, flags, access, 0);
        error = Marshal.GetLastWin32Error();
        return h;
    }

    public bool SetThreadDesktop(nint h, out int error)
    {
        var res = Program.SetThreadDesktop(h);
        error = res ? 0 : Marshal.GetLastWin32Error();
        return res;
    }

    public bool CloseDesktop(nint h, out int error)
    {
        var res = Program.CloseDesktop(h);
        error = res ? 0 : Marshal.GetLastWin32Error();
        return res;
    }

    public bool CloseWindowStation(nint h, out int error)
    {
        var res = Program.CloseWindowStation(h);
        error = res ? 0 : Marshal.GetLastWin32Error();
        return res;
    }

    public uint GetClipboardSequenceNumber() => Program.GetClipboardSequenceNumber();

    public void WriteLine(string message) => Console.WriteLine(message);
}

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowStationW(string? name, uint flags, uint access, nint security);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetProcessWindowStation(nint h);

    [DllImport("user32.dll")]
    public static extern nint GetProcessWindowStation();

    [DllImport("user32.dll")]
    public static extern nint GetThreadDesktop(uint tid);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateDesktopW(string name, nint device, nint mode, uint flags, uint access, nint security);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetThreadDesktop(nint h);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseDesktop(nint h);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseWindowStation(nint h);

    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool OpenClipboard(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint GetClipboardData(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalUnlock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nuint GlobalSize(nint hMem);

    [MTAThread]
    public static int Main(string[] args)
    {
        // Dedicated MTA thread guarantees SetThreadDesktop and restoration occur on the exact same thread
        int exitCode = 1;
        var thread = new Thread(() =>
        {
            var env = new RealIsolatedStationEnvironment();
            bool snapshots = Array.IndexOf(args, "--snapshots") >= 0;
            bool diagnostics = Array.IndexOf(args, "--diagnostics") >= 0;
            exitCode = IsolatedStationCoordinator.Run(env, diagnostics ? ClipboardDiagnostics.Run : snapshots ? SnapshotValidation.Run : RunIsolatedBody);
            if (snapshots && SnapshotValidation.Skipped && exitCode == 0) exitCode = IsolatedStationCoordinator.ExitCodeSkip;
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        return exitCode;
    }

    public static (bool Success, string? ErrorMessage) RunIsolatedBody(IIsolatedStationEnvironment env)
    {
        uint initialIsolatedSeq = env.GetClipboardSequenceNumber();

        // Run actual Win32ClipboardService operations in the isolated station
        nint isolatedDesktop = env.GetThreadDesktop(env.GetCurrentThreadId());
        var service = new Win32ClipboardService(null, null, new DiagnosticClipboardAdapter(isolatedDesktop));

        // 1. Text roundtrip and owner window destruction check
        const string expectedText = "Iris isolated clipboard text roundtrip - 100% genuine Win32!";
        service.SetTextAsync(expectedText).GetAwaiter().GetResult();

        var readText = service.GetTextAsync().GetAwaiter().GetResult();
        if (readText != expectedText)
        {
            return (false, $"Text mismatch. Expected '{expectedText}', Got '{readText}'");
        }
        ClipboardDiagnostics.Log("coordinator", "text-roundtrip-verified", true);

        // 2. Synthetic 2x2 24bpp BMP image publish and CF_DIBV5 read-back with pixel comparison
        byte[] syntheticBmp = CreateSynthetic2x2Bmp();
        var imageData = new ImageData(syntheticBmp, ImageFormat.Bmp, 2, 2);
        service.SetImageAsync(imageData).GetAwaiter().GetResult();

        bool dibOpened = OpenClipboard(0);
        int dibOpenError = Marshal.GetLastWin32Error();
        ClipboardDiagnostics.Log("coordinator", "DIB-read-OpenClipboard", dibOpened, dibOpenError);
        if (!dibOpened)
        {
            return (false, $"Failed to open clipboard for DIBV5 read (error={dibOpenError})");
        }

        bool dibUnlockFailed = false;
        int dibUnlockError = 0;
        bool dibCloseFailed = false;
        int dibCloseError = 0;
        string? dibError = null;

        try
        {
            if (!IsClipboardFormatAvailable(DibV5Helper.CF_DIBV5))
            {
                dibError = "CF_DIBV5 format not available after SetImageAsync.";
            }
            else
            {
                nint hDib = GetClipboardData(DibV5Helper.CF_DIBV5);
                if (hDib == 0)
                {
                    int err = Marshal.GetLastWin32Error();
                    dibError = $"GetClipboardData(CF_DIBV5) returned null handle (error={err})";
                }
                else
                {
                    nuint dibSize = GlobalSize(hDib);
                    if (dibSize < BITMAPV5HEADER.HeaderSize + 16)
                    {
                        dibError = $"DIBV5 buffer size {dibSize} is too small.";
                    }
                    else
                    {
                        nint pDib = GlobalLock(hDib);
                        if (pDib == 0)
                        {
                            int err = Marshal.GetLastWin32Error();
                            dibError = $"GlobalLock failed on DIBV5 (error={err})";
                        }
                        else
                        {
                            try
                            {
                                byte[] dibBytes = new byte[(int)dibSize];
                                Marshal.Copy(pDib, dibBytes, 0, (int)dibSize);

                                if (!DibV5Helper.TryParseDibV5(dibBytes, out var header, out var pixels))
                                {
                                    dibError = "Failed to parse DIBV5 header.";
                                }
                                else if (header.bV5Width != 2 || header.bV5Height != -2 || pixels.Length < 16)
                                {
                                    dibError = $"DIB dimensions mismatch. Width={header.bV5Width}, Height={header.bV5Height}, PixelsLength={pixels.Length}";
                                }
                                else if (pixels[2] != 255 || pixels[3] != 255) // Top-left Red (R=255, A=255)
                                {
                                    dibError = $"Pixel (0,0) Red component mismatch: R={pixels[2]}, A={pixels[3]}";
                                }
                            }
                            finally
                            {
                                if (!GlobalUnlock(hDib))
                                {
                                    int err = Marshal.GetLastWin32Error();
                                    if (err != 0)
                                    {
                                        dibUnlockFailed = true;
                                        dibUnlockError = err;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            bool dibClosed = CloseClipboard();
            int closeError = Marshal.GetLastWin32Error();
            ClipboardDiagnostics.Log("coordinator", "DIB-read-CloseClipboard", dibClosed, closeError);
            if (!dibClosed)
            {
                dibCloseFailed = true;
                dibCloseError = closeError;
            }
        }

        if (dibError != null) return (false, dibError);
        if (dibUnlockFailed) return (false, $"GlobalUnlock failed on DIBV5 (error={dibUnlockError})");
        if (dibCloseFailed) return (false, $"CloseClipboard failed after DIBV5 read (error={dibCloseError})");
        ClipboardDiagnostics.Log("coordinator", "DIBV5-pixels-verified", true);

        // 3. Occupancy failure and cancellation test
        if (!ClipboardDiagnostics.WindowOccupancy(isolatedDesktop, service))
            return (false, "Expected cancellation/busy when occupied.");

        // 4. Verify sequence number changed in isolated window station
        uint isolatedSeq = service.GetSequenceNumber();
        if (isolatedSeq == initialIsolatedSeq)
        {
            return (false, "Expected sequence number to advance in isolated window station.");
        }
        ClipboardDiagnostics.Log("coordinator", "sequence-advanced-verified", new { Initial = initialIsolatedSeq, Final = isolatedSeq });

        return (true, null);
    }

    private static byte[] CreateSynthetic2x2Bmp()
    {
        // 54 bytes header + 2 rows of 8 bytes = 70 bytes
        byte[] bytes = new byte[70];
        bytes[0] = 0x42; // 'B'
        bytes[1] = 0x4D; // 'M'
        BitConverter.GetBytes(70).CopyTo(bytes, 2);
        BitConverter.GetBytes(54).CopyTo(bytes, 10);
        BitConverter.GetBytes(40).CopyTo(bytes, 14);
        BitConverter.GetBytes(2).CopyTo(bytes, 18);
        BitConverter.GetBytes(2).CopyTo(bytes, 22);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 26);
        BitConverter.GetBytes((short)24).CopyTo(bytes, 28);
        BitConverter.GetBytes(16).CopyTo(bytes, 34);

        // Row 0 (bottom row): Blue, Green, 2 pad bytes
        bytes[54] = 0xFF; bytes[55] = 0x00; bytes[56] = 0x00;
        bytes[57] = 0x00; bytes[58] = 0xFF; bytes[59] = 0x00;
        bytes[60] = 0x00; bytes[61] = 0x00;

        // Row 1 (top row): Red, White, 2 pad bytes
        bytes[62] = 0x00; bytes[63] = 0x00; bytes[64] = 0xFF;
        bytes[65] = 0xFF; bytes[66] = 0xFF; bytes[67] = 0xFF;
        bytes[68] = 0x00; bytes[69] = 0x00;

        return bytes;
    }
}
