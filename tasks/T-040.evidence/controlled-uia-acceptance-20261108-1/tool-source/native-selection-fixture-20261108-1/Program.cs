using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class Program
{
    private delegate nint WindowProcedure(nint window, uint message, nuint wparam, nint lparam);
    private static readonly WindowProcedure Procedure = HandleMessage;
    private const string ClassName = "IrisT040OwnedSelectionFixture";
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public nint Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint Wparam;
        public nint Lparam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(in WindowClass value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint exstyle, string classname, string text, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint window, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadCursor(nint instance, nint name);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(in Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DispatchMessage(in Message message);

    [MTAThread]
    private static int Main()
    {
        if (Environment.UserName != "WDAGUtilityAccount") throw new InvalidOperationException("Owned Sandbox only.");
        nint instance = GetModuleHandle(null);
        if (instance == 0) throw new InvalidOperationException("Module identity unavailable.");
        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = Marshal.GetFunctionPointerForDelegate(Procedure),
            Instance = instance, Cursor = LoadCursor(0, 32512), Background = 6, Name = ClassName,
        };
        if (RegisterClassEx(in windowClass) == 0) throw new InvalidOperationException("Owned fixture class registration failed.");
        nint window = CreateWindowEx(0, ClassName, "Iris T040 native selection scene", 0x10CF0000, 80, 80, 760, 450, 0, 0, instance, 0);
        if (window == 0) throw new InvalidOperationException("Owned fixture window creation failed.");
        nint label = CreateWindowEx(0, "Static", "Ordinary native Edit control with synthetic text", 0x50000000, 22, 22, 670, 28, window, 0, instance, 0);
        nint edit = CreateWindowEx(0, "Edit", "Iris T040 native selection sample 013", 0x508000C4, 22, 58, 670, 110, window, 0, instance, 0);
        nint protectedLabel = CreateWindowEx(0, "Static", "Synthetic password control; no account or authentication", 0x50000000, 22, 194, 670, 28, window, 0, instance, 0);
        nint password = CreateWindowEx(0, "Edit", "Iris T040 fake protected sample 013", 0x508000A0, 22, 228, 670, 36, window, 0, instance, 0);
        if (label == 0 || edit == 0 || protectedLabel == 0 || password == 0) throw new InvalidOperationException("Owned fixture controls could not be created.");
        string path = Path.Combine(@"C:\IrisT040Output", "native-selection-fixture-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", CultureInfo.InvariantCulture) + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { Utc = DateTimeOffset.UtcNow, Pid = Environment.ProcessId,
            Window = window.ToInt64(), Edit = edit.ToInt64(), Password = password.ToInt64(),
            SyntheticOnly = true, ForegroundInputInjected = false, ProductOperationPerformed = false,
            Scope = "Owned native UIA and safe-copy target, containing only fixed synthetic data." }));
        int result;
        while ((result = GetMessage(out var message, 0, 0, 0)) > 0)
        {
            _ = TranslateMessage(in message);
            _ = DispatchMessage(in message);
        }
        return result == 0 ? 0 : 1;
    }
    private static nint HandleMessage(nint window, uint message, nuint wparam, nint lparam)
    {
        if (message == 2) { PostQuitMessage(0); return 0; }
        return DefWindowProc(window, message, wparam, lparam);
    }
}
