using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Iris.Core.Serialization;
using Iris.Core.Settings;

if (Environment.UserName != "WDAGUtilityAccount") throw new InvalidOperationException("Owned T-040 Sandbox only.");
if (args.Length != 1 || args[0] is not ("reflection" or "generated")) throw new ArgumentException("Expected reflection or generated mode.");
string mode = args[0];
string root = @"C:\IrisT040Output";
if (!Directory.Exists(root)) throw new InvalidOperationException("Missing owned output mapping.");
string input = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixture.json"));
using var process = Process.GetCurrentProcess();
process.Refresh();
long beforeWorkingSet = process.WorkingSet64;
long beforePrivate = process.PrivateMemorySize64;
JsonSerializerOptions options;
if (mode == "generated") options = IrisJson.Options;
else
{
    options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
    options.Converters.Add(new JsonStringEnumConverter());
}
var watch = Stopwatch.StartNew();
var settings = JsonSerializer.Deserialize<AppSettings>(input, options) ?? throw new InvalidOperationException("Fixture did not deserialize.");
byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(settings, options);
watch.Stop();
if (settings.Hotkeys.Count != 7 || settings.General.UiLanguage != "zh-CN") throw new InvalidOperationException("Fixture/defaults differ.");
await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
var samples = new List<object>();
for (int index = 0; index < 10; index++)
{
    process.Refresh();
    samples.Add(new { Utc = DateTimeOffset.UtcNow, WorkingSetBytes = process.WorkingSet64, PrivateBytes = process.PrivateMemorySize64, ManagedBytes = GC.GetTotalMemory(forceFullCollection: false) });
    await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
}
var state = new
{
    Utc = DateTimeOffset.UtcNow,
    Mode = mode,
    Pid = Environment.ProcessId,
    Framework = RuntimeInformation.FrameworkDescription,
    BeforeWorkingSet = beforeWorkingSet,
    BeforePrivateBytes = beforePrivate,
    FirstSettingsOperationMs = watch.Elapsed.TotalMilliseconds,
    SerializedSha256 = Convert.ToHexString(SHA256.HashData(serialized)),
    Samples = samples,
    ProductOperationPerformed = false,
    InstalledIrisInvoked = false,
    GcRequested = false,
    WorkingSetTrimmed = false,
    RealAppNfrPassed = false,
};
string path = Path.Combine(root, "serializer-metadata-cost-" + mode + "-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", CultureInfo.InvariantCulture) + ".json");
await File.WriteAllTextAsync(path, JsonSerializer.Serialize(state)).ConfigureAwait(false);
