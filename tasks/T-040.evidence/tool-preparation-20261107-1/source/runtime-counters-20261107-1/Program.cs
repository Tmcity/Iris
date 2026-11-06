using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;

if (Environment.UserName != "WDAGUtilityAccount")
    throw new InvalidOperationException("Owned T-040 Sandbox only.");
bool fixtureMode = args.Length == 3 && string.Equals(args[2], "--fixture", StringComparison.Ordinal);
if ((args.Length != 2 && !fixtureMode) || !int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
    || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int duration)
    || duration is < 5 or > 600)
    throw new ArgumentException("Expected owned Iris PID and 5..600 second observation duration.");
using var process = Process.GetProcessById(pid);
string expected = fixtureMode ? @"C:\IrisT040MetricsV2\tools\synthetic-provider\T040SyntheticProvider.exe"
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Iris", "current", "Iris.App.exe");
if (!string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Observation target does not match the explicitly selected owned process.");
string root = @"C:\IrisT040Output";
if (!Directory.Exists(root)) throw new InvalidOperationException("Missing owned output mapping.");
string output = Path.Combine(root, "runtime-counters-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", CultureInfo.InvariantCulture));
Directory.CreateDirectory(output);
using var writer = new StreamWriter(Path.Combine(output, "events.jsonl"), false, new UTF8Encoding(false));
var gate = new object();
int sequence = 0, fullGcEvents = 0;
void Record(object value)
{
    lock (gate) { writer.WriteLine(JsonSerializer.Serialize(value)); writer.Flush(); sequence++; }
}
Record(new { Kind = "ObserverStarted", Utc = DateTimeOffset.UtcNow, Pid = pid, DurationSeconds = duration,
    TargetRole = fixtureMode ? "OwnedSyntheticProvider" : "InstalledIris",
    ProductOperationPerformed = false, ForceGcRequested = false, EventPipeCircularBufferMiB = 1,
    Scope = "Runtime counters and natural GC events; trace buffer can add memory. Exclude active tracing from the formal idle-memory pass." });
using var deadline = new CancellationTokenSource();
string? failure = null;
try
{
    var providers = new[] {
        new EventPipeProvider("System.Runtime", EventLevel.Informational, 0,
            new Dictionary<string, string> { ["EventCounterIntervalSec"] = "1" }),
        new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, 1)
    };
    var client = new DiagnosticsClient(pid);
    using var session = client.StartEventPipeSession(providers, requestRundown: false, circularBufferMB: 1);
    using var source = new EventPipeEventSource(session.EventStream);
    source.Clr.GCStart += data => {
        if (data.Depth == 2) fullGcEvents++;
        Record(new { Kind = "GCStart", Utc = DateTimeOffset.UtcNow, EventTime = data.TimeStamp,
            data.Count, data.Depth, Reason = data.Reason.ToString(), Type = data.Type.ToString() });
    };
    source.Clr.GCStop += data => Record(new { Kind = "GCStop", Utc = DateTimeOffset.UtcNow, EventTime = data.TimeStamp, data.Count, data.Depth });
    source.Dynamic.All += data => {
        if (!string.Equals(data.ProviderName, "System.Runtime", StringComparison.Ordinal)
            || !string.Equals(data.EventName, "EventCounters", StringComparison.Ordinal)) return;
        if (data.PayloadValue(0) is not IDictionary<string, object> payload) return;
        if (payload.TryGetValue("Payload", out object? nested) && nested is IDictionary<string, object> inner) payload = inner;
        if (!payload.TryGetValue("Name", out object? name)) return;
        payload.TryGetValue("Mean", out object? mean);
        payload.TryGetValue("Increment", out object? increment);
        payload.TryGetValue("IntervalSec", out object? interval);
        Record(new { Kind = "RuntimeCounter", Utc = DateTimeOffset.UtcNow, Name = name, Mean = mean, Increment = increment, IntervalSec = interval });
    };
    Task stop = Task.Run(async () => {
        try { await Task.Delay(TimeSpan.FromSeconds(duration), deadline.Token).ConfigureAwait(false); session.Stop(); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        catch (Exception error) { Record(new { Kind = "SessionStopFailure", Utc = DateTimeOffset.UtcNow, Type = error.GetType().FullName }); }
    });
    try { source.Process(); }
    finally { await deadline.CancelAsync().ConfigureAwait(false); await stop.ConfigureAwait(false); }
}
catch (Exception error) { failure = error.GetType().FullName; }
Record(new { Kind = "ObserverStopped", Utc = DateTimeOffset.UtcNow, Failure = failure, Sequence = sequence, NaturalFullGcStarts = fullGcEvents });
await File.WriteAllTextAsync(Path.Combine(output, "state.json"), JsonSerializer.Serialize(new {
    Utc = DateTimeOffset.UtcNow, Pid = pid, ObserverPid = Environment.ProcessId, Completed = true,
    ProductOperationPerformed = false, ForceGcRequested = false, Failure = failure, NaturalFullGcStarts = fullGcEvents, Events = sequence
})).ConfigureAwait(false);
return failure is null ? 0 : 1;
