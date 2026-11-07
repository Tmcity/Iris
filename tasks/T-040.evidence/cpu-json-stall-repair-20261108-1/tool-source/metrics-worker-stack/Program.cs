using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Diagnostics.Runtime;

if (Environment.UserName != "WDAGUtilityAccount") throw new InvalidOperationException("Owned Sandbox only.");
using var process = Process.GetProcessById(2432);
string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
if (!string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected helper path.");
if (Math.Abs((process.StartTime.ToUniversalTime() - DateTime.Parse("2026-11-07T19:11:31.258Z", CultureInfo.InvariantCulture).ToUniversalTime()).TotalMilliseconds) > 1) throw new InvalidOperationException("Helper process identity changed.");
string dac = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework64", "v4.0.30319", "mscordacwks.dll");
using var target = DataTarget.AttachToProcess(process.Id, suspend: false);
using var runtime = target.ClrVersions.Single().CreateRuntime(dac);
var threads = runtime.Threads.Select(thread => new {
    thread.OSThreadId,
    thread.ManagedThreadId,
    Frames = thread.EnumerateStackTrace().Take(24).Select(frame => new { Kind = frame.Kind.ToString(), Method = frame.Method?.Signature, Module = frame.Method?.Type?.Module.Name }).ToArray(),
}).ToArray();
string output = Path.Combine(@"C:\IrisT040Output", "metrics-worker-stack-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", CultureInfo.InvariantCulture) + ".json");
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new {
    Utc = DateTimeOffset.UtcNow, Pid = process.Id, TargetSuspended = false,
    ProductOperationPerformed = false, UserTextOrCredentialsRead = false, Threads = threads,
})).ConfigureAwait(false);
Console.WriteLine(output);
