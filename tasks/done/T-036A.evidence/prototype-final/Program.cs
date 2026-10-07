using System.Reflection;
using System.Runtime.InteropServices;

Console.WriteLine("RUNTIME " + RuntimeInformation.FrameworkDescription);
Console.WriteLine("HTTP_ASSEMBLY " + typeof(HttpClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
try
{
    if (args.Length == 0 || args.Contains("current")) await CurrentChecks.Run();
    if (args.Length == 0 || args.Contains("runtime")) await RuntimeChecks.Run();
    if (args.Length == 0 || args.Contains("persistence")) await PersistenceChecks.Run();
    if (args.Length == 0 || args.Contains("actual-coordinator")) await ActualCoordinatorChecks.Run();
    if (args.Length == 0 || args.Contains("migration")) await MigrationChecks.Run();
    if (args.Length == 0 || args.Contains("pipelines")) await PipelineChecks.Run();
    if (args.Length == 0 || args.Contains("connectivity")) await ConnectivityChecks.Run();
    if (args.Length == 0 || args.Contains("streaming")) await StreamingChecks.Run();
    Console.WriteLine("PASS completed requested modes");
}
catch (Exception ex)
{
    Console.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message);
    Console.WriteLine(ex.StackTrace);
    Environment.ExitCode = 1;
}

internal static class Check
{
    internal static void That(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    internal static void Pass(string message) => Console.WriteLine("PASS " + message);
}
