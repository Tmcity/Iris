using Iris.App.Tests.TestSupport;

var failures = new List<Exception>();
int checks = 0;
async Task Case(string name, Func<Task> action)
{
    checks++;
    try { await action(); Console.WriteLine($"CHECK {name}=PASS"); }
    catch (Exception ex) { failures.Add(ex); Console.WriteLine($"CHECK {name}=FAIL {ex}"); }
}
try
{
    await GeometryChecks.Run(Case);
    await LifecycleChecks.Run(Case);
    await VisualChecks.Run(Case);
    await StartupCloseChecks.Run(Case);
}
finally { await WpfTestRunner.TeardownAsync(); }
Console.WriteLine($"SCREENSHOT_REVIEW checks={checks} failures={failures.Count} actualServiceAndWindows=True fakeExternalBoundaries=True userContent=False actualScreenshot=False staAlive={WpfTestRunner.IsStaThreadAlive}");
Environment.ExitCode = failures.Count == 0 && !WpfTestRunner.IsStaThreadAlive ? 0 : 2;
