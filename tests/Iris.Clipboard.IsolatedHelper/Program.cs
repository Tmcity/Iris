using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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

    public static async Task<int> Main(string[] args)
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

            // Run actual Win32ClipboardService operations in the isolated station
            using var service = new Win32ClipboardService(null, null);

            // 1. Text roundtrip
            const string expectedText = "Iris isolated clipboard text roundtrip - 100% genuine Win32!";
            await service.SetTextAsync(expectedText, CancellationToken.None);

            var readText = await service.GetTextAsync(CancellationToken.None);
            if (readText != expectedText)
            {
                Console.WriteLine($"[ISOLATED_CLIPBOARD] FAIL: Text mismatch. Expected '{expectedText}', Got '{readText}'");
                return 1;
            }

            // 2. Synthetic 2x2 24bpp BMP image publish
            byte[] syntheticBmp = CreateSynthetic2x2Bmp();
            var imageData = new ImageData(syntheticBmp, ImageFormat.Bmp, 2, 2);
            await service.SetImageAsync(imageData, CancellationToken.None);

            // 3. Verify sequence number of isolated station changed
            uint isolatedSeq = service.GetSequenceNumber();
            if (isolatedSeq == 0)
            {
                Console.WriteLine("[ISOLATED_CLIPBOARD] FAIL: Expected positive sequence number in isolated window station.");
                return 1;
            }

            // 4. Verify interactive clipboard sequence number remained completely untouched
            uint finalInteractiveSeq = GetClipboardSequenceNumber();
            if (finalInteractiveSeq != originalInteractiveSeq)
            {
                Console.WriteLine($"[ISOLATED_CLIPBOARD] FAIL: Interactive clipboard sequence changed from {originalInteractiveSeq} to {finalInteractiveSeq}!");
                return 1;
            }

            Console.WriteLine("[ISOLATED_CLIPBOARD] PASS");
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
            if (originalStation != 0)
            {
                SetProcessWindowStation(originalStation);
            }
            if (desktop != 0)
            {
                CloseDesktop(desktop);
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
        // 'BM'
        bytes[0] = 0x42;
        bytes[1] = 0x4D;
        // File size = 70
        BitConverter.GetBytes(70).CopyTo(bytes, 2);
        // Pixel offset = 54
        BitConverter.GetBytes(54).CopyTo(bytes, 10);
        // Header size = 40
        BitConverter.GetBytes(40).CopyTo(bytes, 14);
        // Width = 2
        BitConverter.GetBytes(2).CopyTo(bytes, 18);
        // Height = 2
        BitConverter.GetBytes(2).CopyTo(bytes, 22);
        // Planes = 1
        BitConverter.GetBytes((short)1).CopyTo(bytes, 26);
        // BitCount = 24
        BitConverter.GetBytes((short)24).CopyTo(bytes, 28);
        // ImageSize = 16
        BitConverter.GetBytes(16).CopyTo(bytes, 34);

        // Pixels (2 rows * 8 bytes): B G R, B G R, 2 pad bytes
        // Row 0
        bytes[54] = 0xFF; bytes[55] = 0x00; bytes[56] = 0x00; // Blue
        bytes[57] = 0x00; bytes[58] = 0xFF; bytes[59] = 0x00; // Green
        bytes[60] = 0x00; bytes[61] = 0x00;                   // padding
        // Row 1
        bytes[62] = 0x00; bytes[63] = 0x00; bytes[64] = 0xFF; // Red
        bytes[65] = 0xFF; bytes[66] = 0xFF; bytes[67] = 0xFF; // White
        bytes[68] = 0x00; bytes[69] = 0x00;                   // padding

        return bytes;
    }
}
