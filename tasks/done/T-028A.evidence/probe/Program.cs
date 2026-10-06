using Iris.Platform.Windows.Imaging;

string directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
var failures = new List<Exception>();
int checks = 0;
async Task Check(string name, Func<Task> action)
{
    checks++;
    try { await action(); Console.WriteLine($"CHECK {name}=PASS"); }
    catch (Exception ex) { failures.Add(ex); Console.WriteLine($"CHECK {name}=FAIL {ex}"); }
}
var real = new WinRtImagePreprocessor(new WinRtImagePreparationFactory());
try
{
    await GeometryChecks.Run(real, directory, Check);
    await ContractChecks.Run(real, directory, Check);
    await OwnershipChecks.Run(real, directory, Check);
    await LifecycleChecks.Run(Check);
}
catch (Exception ex) { failures.Add(ex); Console.WriteLine($"PROBE_INFRASTRUCTURE_FAILURE {ex}"); }
finally
{
    try { await real.DisposeAsync(); }
    catch (Exception ex) { failures.Add(ex); Console.WriteLine($"FINAL_CLEANUP_FAILURE {ex}"); }
}
Console.WriteLine($"PREPROCESSOR_REVIEW checks={checks} failures={failures.Count} actualService=True actualMemoryCodec=True fakeFailureBoundaries=True userContent=False");
Environment.ExitCode = failures.Count == 0 ? 0 : 2;
