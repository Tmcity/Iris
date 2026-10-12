using Velopack;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        Console.WriteLine("Owned T-039A payload; no Iris application, user data, or network operations.");
    }
}
