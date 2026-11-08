using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

internal static class Program
{
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, [Out] char[] text, int count);
    private static readonly JsonSerializerOptions OutputJson = new() { WriteIndented = true };

    [MTAThread]
    private static int Main()
    {
        if (Environment.UserName != "WDAGUtilityAccount") throw new InvalidOperationException("Owned Sandbox only.");
        nint source = GetForegroundWindow();
        if (GetWindowThreadProcessId(source, out uint sourcePid) == 0) throw new InvalidOperationException("Foreground identity unavailable.");
        using var process = Process.GetProcessById(checked((int)sourcePid));
        var title = new char[512];
        int titleLength = GetWindowText(source, title, title.Length);
        string caption = titleLength > 0 ? new string(title, 0, titleLength) : "";
        bool browser = process.ProcessName == "msedge" && caption.StartsWith("Iris T040 owned UI scene", StringComparison.Ordinal);
        bool native = process.ProcessName == "NativeSelectionFixture" && caption == "Iris T040 native selection scene"
            && process.MainModule?.FileName == @"C:\IrisT040R2rRepeat\tools\native-selection-fixture\NativeSelectionFixture.exe";
        if (!browser && !native)
            throw new InvalidOperationException("Only the current owned synthetic browser scene may be queried.");
        string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Iris", "current");
        using var identity = JsonDocument.Parse(File.ReadAllText(Path.Combine(installed, "release-source.json")));
        if (identity.RootElement.GetProperty("Version").GetString() != "0.2.13") throw new InvalidOperationException("Reviewed candidate only.");
        string libraries = Path.Combine(installed, "uia");
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string simple = name.Name ?? throw new InvalidOperationException("Missing library name.");
            if (simple.IndexOfAny(['/', '\\', ':']) >= 0) return null;
            string candidate = Path.Combine(libraries, simple + ".dll");
            return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
        };
        string platformPath = Path.Combine(libraries, "Iris.Platform.Windows.dll");
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(platformPath);
        var adapterType = assembly.GetType("Iris.Platform.Windows.Selection.RealNativeUiaAdapter", throwOnError: true)!;
        var adapter = adapterType.GetField("Instance", BindingFlags.Static | BindingFlags.Public)!.GetValue(null)!;
        var elementInterface = assembly.GetType("Windows.Win32.UI.Accessibility.IUIAutomationElement", throwOnError: true)!;
        var variantMethod = elementInterface.GetMethod("GetCurrentPropertyValue")!;
        object? session = null;
        var owned = new List<object>();
        var rows = new List<object>();
        object? failure = null;
        object? managedProperties = null;
        string mode = Environment.GetEnvironmentVariable("IRIS_T040_UIA_PROBE_MODE") ?? "ManagedBefore";
        object? proxyMapState = null;
        var timer = Stopwatch.StartNew();
        try
        {
            if (mode == "ManagedBefore") managedProperties = ReadManagedProperties();
            if (mode == "IdentifiersOnly") managedProperties = ReadIdentifierMetadata();
            session = adapterType.GetMethod("CreateSession")!.Invoke(adapter, [300, 300]);
            if (mode == "RestoreProxies")
            {
                var automation = session!.GetType().GetField("_uia", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
                var automationInterface = assembly.GetType("Windows.Win32.UI.Accessibility.IUIAutomation", throwOnError: true)!;
                var mappingInterface = assembly.GetType("Windows.Win32.UI.Accessibility.IUIAutomationProxyFactoryMapping", throwOnError: true)!;
                proxyMapState = new {
                    Stage = "MemberDiscovery",
                    AutomationMembers = automationInterface.GetMembers().Where(member => member.Name.Contains("Proxy", StringComparison.Ordinal)).Select(member => member.Name).ToArray(),
                    MappingMembers = mappingInterface.GetMembers().Select(member => member.Name).ToArray()
                };
                var mapping = automationInterface.GetProperty("ProxyFactoryMapping")!.GetValue(automation)!;
                object? before = mappingInterface.GetProperty("Count")!.GetValue(mapping);
                mappingInterface.GetMethod("RestoreDefaultTable")!.Invoke(mapping, null);
                object? after = mappingInterface.GetProperty("Count")!.GetValue(mapping);
                proxyMapState = new { Before = before, After = after, RestoredInDiagnosticProcessOnly = true };
                if (Marshal.IsComObject(mapping)) Marshal.ReleaseComObject(mapping);
            }
            object? current = session!.GetType().GetMethod("GetFocusedElement")!.Invoke(session, null);
            for (int depth = 0; current != null && depth < 32; depth++)
            {
                owned.Add(current);
                Type type = current.GetType();
                uint pid = Convert.ToUInt32(type.GetProperty("ProcessId")!.GetValue(current), CultureInfo.InvariantCulture);
                nint handle = (nint)type.GetProperty("NativeWindowHandle")!.GetValue(current)!;
                bool password = (bool)type.GetProperty("IsPassword")!.GetValue(current)!;
                var comElement = type.GetField("_element", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(current)!;
                var variantPid = ReadNumericOrBooleanProperty(variantMethod, comElement, 30002);
                var variantPassword = ReadNumericOrBooleanProperty(variantMethod, comElement, 30019);
                bool belongs = handle != 0 && (bool)adapterType.GetMethod("IsWindowOrAncestor")!.Invoke(adapter, [handle, source])!;
                rows.Add(new { Depth = depth, ProcessId = pid, NativeWindow = handle.ToInt64(), IsPassword = password,
                    MatchesForegroundPid = pid == sourcePid, NativeWindowWithinForeground = belongs,
                    VariantProcessId = variantPid, VariantIsPassword = variantPassword,
                    ProcessGetterImplementationFlags = elementInterface.GetProperty("CurrentProcessId")!.GetMethod!.MethodImplementationFlags.ToString() });
                if (password || belongs) break;
                current = type.GetMethod("GetParent")!.Invoke(current, [1]);
            }
        }
        catch (Exception error)
        {
            Exception original = error is TargetInvocationException { InnerException: { } inner } ? inner : error;
            failure = new { Type = original.GetType().FullName, HResult = original.HResult };
        }
        finally
        {
            for (int i = owned.Count - 1; i >= 0; i--)
            {
                try { ((IDisposable)owned[i]).Dispose(); }
                catch (Exception error) { failure ??= new { Type = error.GetType().FullName, HResult = error.HResult }; }
            }
            try { ((IDisposable?)session)?.Dispose(); }
            catch (Exception error) { failure ??= new { Type = error.GetType().FullName, HResult = error.HResult }; }
        }
        timer.Stop();
        string output = Path.Combine(@"C:\IrisT040Output", "uia-source-metadata-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", CultureInfo.InvariantCulture) + ".json");
        File.WriteAllText(output, JsonSerializer.Serialize(new { Utc = DateTimeOffset.UtcNow, SourceWindow = source.ToInt64(), SourcePid = sourcePid,
            ProbePid = Environment.ProcessId, ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
            InstalledPlatformSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(platformPath))),
            Rows = rows, Mode = mode, ProxyMapState = proxyMapState, ManagedProperties = managedProperties, Failure = failure, ProductOperationPerformed = false, ForegroundInputInjected = false,
            UserTextOrCredentialsRead = false, TextPatternsRead = false, TargetSuspended = false, GcRequested = false,
            Scope = "Actual reviewed UIA adapter metadata on an owned synthetic browser or native scene; no text pattern read and no source-policy change." }, OutputJson));
        Console.WriteLine(output);
        return failure == null ? 0 : 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object? ReadManagedProperties()
    {
        var element = System.Windows.Automation.AutomationElement.FocusedElement;
        if (element == null) return null;
        var properties = element.Current;
        return new { properties.ProcessId, properties.NativeWindowHandle, properties.IsPassword };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object ReadIdentifierMetadata() => new {
        ProcessId = System.Windows.Automation.AutomationElementIdentifiers.ProcessIdProperty.Id,
        NativeWindowHandle = System.Windows.Automation.AutomationElementIdentifiers.NativeWindowHandleProperty.Id,
        IsPassword = System.Windows.Automation.AutomationElementIdentifiers.IsPasswordProperty.Id,
    };

    private static object ReadNumericOrBooleanProperty(MethodInfo method, object element, int propertyId)
    {
        var parameters = method.GetParameters();
        var arguments = new object?[parameters.Length];
        Type first = parameters[0].ParameterType;
        arguments[0] = first.IsEnum ? Enum.ToObject(first, propertyId) : Convert.ChangeType(propertyId, first, CultureInfo.InvariantCulture);
        object? result = method.Invoke(element, arguments);
        object? value = parameters.Length == 1 ? result : arguments[^1];
        return value switch
        {
            int number => new { Type = "Int32", Value = (object)number },
            uint number => new { Type = "UInt32", Value = (object)number },
            bool flag => new { Type = "Boolean", Value = (object)flag },
            _ => (object)new { Type = value?.GetType().FullName, SupportedScalar = false },
        };
    }
}
