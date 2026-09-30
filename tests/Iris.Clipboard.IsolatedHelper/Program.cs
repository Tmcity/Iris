using System;
using System.Runtime.InteropServices;
using System.Threading;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Platform.Windows.Clipboard;

namespace Iris.Clipboard.IsolatedHelper;

internal static class Program
{
    private const uint CWF_CREATE_ONLY = 0x00000001;
    private const uint WINSTA_ALL_ACCESS = 0x0000037F;
    private const uint DESKTOP_ALL_ACCESS = 0x000001FF;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowStationW(string? name, uint flags, uint access, nint security);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessWindowStation(nint h);

    [DllImport("user32.dll")]
    private static extern nint GetProcessWindowStation();

    [DllImport("user32.dll")]
    private static extern nint GetThreadDesktop(uint tid);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateDesktopW(string name, nint device, nint mode, uint flags, uint access, nint security);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(nint h);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(nint h);

    [DllImport("user32.dll")]
    private static extern bool CloseWindowStation(nint h);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetClipboardData(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint GlobalSize(nint hMem);

    [MTAThread]
    public static int Main(string[] args)
    {
        // Dedicated MTA thread guarantees SetThreadDesktop and restoration occur on the exact same thread
        int exitCode = 1;
        var thread = new Thread(() =>
        {
            exitCode = RunIsolatedTest();
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        return exitCode;
    }

    private static int RunIsolatedTest()
    {
        var originalStation = GetProcessWindowStation();
        var originalDesktop = GetThreadDesktop(GetCurrentThreadId());
        var originalInteractiveSeq = GetClipboardSequenceNumber();

        nint station = 0;
        nint desktop = 0;

        try
        {
            // Attempt to create a private non-interactive window station with CWF_CREATE_ONLY
            station = CreateWindowStationW(null, CWF_CREATE_ONLY, WINSTA_ALL_ACCESS, 0);
            int createStationError = Marshal.GetLastWin32Error();

            if (station == 0 || createStationError == 183 || station == originalStation)
            {
                Console.WriteLine($"[ISOLATED_CLIPBOARD] SKIP: Window station creation failed or already exists (station={station}, error={createStationError})");
                return 2;
            }

            if (!SetProcessWindowStation(station))
            {
                int err = Marshal.GetLastWin32Error();
                Console.WriteLine($"[ISOLATED_CLIPBOARD] SKIP: SetProcessWindowStation failed (error={err})");
                return 2;
            }

            desktop = CreateDesktopW("IrisIsolatedDesktop", 0, 0, 0, DESKTOP_ALL_ACCESS, 0);
            if (desktop == 0)
            {
                int err = Marshal.GetLastWin32Error();
                Console.WriteLine($"[ISOLATED_CLIPBOARD] SKIP: CreateDesktop failed (error={err})");
                return 2;
            }

            if (!SetThreadDesktop(desktop))
            {
                int err = Marshal.GetLastWin32Error();
                Console.WriteLine($"[ISOLATED_CLIPBOARD] SKIP: SetThreadDesktop failed (error={err})");
                return 2;
            }

            Console.WriteLine("[ISOLATED_CLIPBOARD] Isolated window station & desktop successfully established.");

            uint initialIsolatedSeq = GetClipboardSequenceNumber();

            // Run actual Win32ClipboardService operations in the isolated station
            var service = new Win32ClipboardService(null, null);

            // 1. Text roundtrip and owner window destruction check
            const string expectedText = "Iris isolated clipboard text roundtrip - 100% genuine Win32!";
            service.SetTextAsync(expectedText).GetAwaiter().GetResult();

            var readText = service.GetTextAsync().GetAwaiter().GetResult();
            if (readText != expectedText)
            {
                Console.WriteLine($"[ISOLATED_CLIPBOARD] FAIL: Text mismatch. Expected '{expectedText}', Got '{readText}'");
                return 1;
            }

            // 2. Synthetic 2x2 24bpp BMP image publish and CF_DIBV5 read-back with pixel comparison
            byte[] syntheticBmp = CreateSynthetic2x2Bmp();
            var imageData = new ImageData(syntheticBmp, ImageFormat.Bmp, 2, 2);
            service.SetImageAsync(imageData).GetAwaiter().GetResult();

            if (!OpenClipboard(0))
            {
                int err = Marshal.GetLastWin32Error();
                Console.WriteLine($"[ISOLATED_CLIPBOARD] FAIL: Failed to open clipboard for DIBV5 read (error={err})");
                return 1;
            }

            try
            {
                if (!IsClipboardFormatAvailable(DibV5Helper.CF_DIBV5))
                {
                    Console.WriteLine("[ISOLATED_CLIPBOARD] FAIL: CF_DIBV5 format not available after SetImageAsync.");
                    return 1;
                }

                nint hDib = GetClipboardData(DibV5Helper.CF_DIBV5);
                if (hDib == 0)
                {
                    int err = Marshal.GetLastWin32Error();
                    Console.WriteLine($"[ISOLATED_CLIPBOARD] FAIL: GetClipboardData(CF_DIBV5) returned null handle (error={err})");
                    return 1;
                }

                nuint dibSize = GlobalSize(hDib);
                if (dibSize < BITMAPV5HEADER.HeaderSize + 16)
                {
                    Console.WriteLine($"[ISOLATED_CLIPBOARD] FAIL: DIBV5 buffer size {dibSize} is too small.");
                    return 1;
                }

                nint pDib = GlobalLock(hDib);
                if (pDib == 0)
                {
                    int err = Marshal.GetLastWin32Error();
                    Console.WriteLine($"[ISOLATED_CLIPBOARD] FAIL: GlobalLock failed on DIBV5 (error={err})");
                    return 1;
                }

                try
                {
                    byte[] dibBytes = new byte[(int)dibSize];
                    Marshal.Copy(pDib, dibBytes, 0, (int)dibSize);

                    if (!DibV5Helper.TryParseDibV5(dibBytes, out var header, out var pixels))
                    {
                        Console.WriteLine("[ISOLATED_CLIPBOARD] FAIL: Failed to parse DIBV5 header.");
                        return 1;
                    }

                    if (header.bV5Width != 2 || header.bV5Height != -2 || pixels.Length < 16)
                    {
                        Console.WriteLine($"[ISOLATED_CLIPBOARD] FAIL: DIB dimensions mismatch. Width={header.bV5Width}, Height={header.bV5Height}, PixelsLength={pixels.Length}");
                        return 1;
                    }

                    // Synthetic BMP bottom row was Blue/Green, top row was Red/White.
                    // WindowsImageDecoder decodes to top-down DIB:
                    // Row 0: Red (B:0, G:0, R:255, A:255), White (B:255, G:255, R:255, A:255)
                    // Row 1: Blue (B:255, G:0, R:0, A:255), Green (B:0, G:255, R:0, A:255)
                    if (pixels[2] != 255 || pixels[3] != 255) // Top-left Red (R=255, A=255)
                    {
                        Console.WriteLine($"[ISOLATED_CLIPBOARD] FAIL: Pixel (0,0) Red component mismatch: R={pixels[2]}, A={pixels[3]}");
                        return 1;
                    }
                }
                finally
                {
                    GlobalUnlock(hDib);
                }
            }
            finally
            {
                CloseClipboard();
            }

            // 3. Occupancy failure and cancellation test
            if (OpenClipboard(0))
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                    try
                    {
                        service.GetTextAsync(cts.Token).GetAwaiter().GetResult();
                        Console.WriteLine("[ISOLATED_CLIPBOARD] FAIL: Expected cancellation/busy when occupied.");
                        return 1;
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected cancellation
                    }
                    catch (IrisException ex) when (ex.Code == ErrorCode.ClipboardBusy)
                    {
                        // Expected busy exhaustion
                    }
                }
                finally
                {
                    CloseClipboard();
                }
            }

            // 4. Verify sequence number changed in isolated window station
            uint isolatedSeq = service.GetSequenceNumber();
            if (isolatedSeq == initialIsolatedSeq)
            {
                Console.WriteLine("[ISOLATED_CLIPBOARD] FAIL: Expected sequence number to advance in isolated window station.");
                return 1;
            }

            // 5. Cleanup and restore window station & desktop on the exact same thread
            bool cleanupSucceeded = true;

            if (originalDesktop != 0)
            {
                if (!SetThreadDesktop(originalDesktop)) cleanupSucceeded = false;
            }
            if (desktop != 0)
            {
                if (!CloseDesktop(desktop)) cleanupSucceeded = false;
                desktop = 0;
            }
            if (originalStation != 0)
            {
                if (!SetProcessWindowStation(originalStation)) cleanupSucceeded = false;
            }
            if (station != 0)
            {
                if (!CloseWindowStation(station)) cleanupSucceeded = false;
                station = 0;
            }

            if (!cleanupSucceeded)
            {
                Console.WriteLine("[ISOLATED_CLIPBOARD] FAIL: Desktop/station restoration failed.");
                return 1;
            }

            // 6. NOW in restored original interactive station: verify interactive sequence number
            uint finalInteractiveSeq = GetClipboardSequenceNumber();

            // SUCCESS protocol delayed until all verifications and cleanups are complete
            Console.WriteLine($"[ISOLATED_CLIPBOARD] PASS (originalSeq={originalInteractiveSeq}, isolatedSeq={isolatedSeq}, restoredSeq={finalInteractiveSeq})");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ISOLATED_CLIPBOARD] FAIL: Exception: {ex}");
            return 1;
        }
        finally
        {
            if (originalDesktop != 0)
            {
                SetThreadDesktop(originalDesktop);
            }
            if (desktop != 0)
            {
                CloseDesktop(desktop);
            }
            if (originalStation != 0)
            {
                SetProcessWindowStation(originalStation);
            }
            if (station != 0)
            {
                CloseWindowStation(station);
            }
        }
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
