using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Iris.Platform.Windows.Maintenance;
using Shouldly;

namespace Iris.Maintenance.Tests;

public sealed class UpdateTransactionTests
{
    [Theory]
    [InlineData("cancel")]
    [InlineData("apply")]
    [InlineData("native-failure")]
    [InlineData("verify-failure")]
    public async Task ActualControlPipeWaitsForOriginalProcessAndPreservesCommittedFailures(string scenario)
    {
        string root = Path.Combine(Path.GetTempPath(), "iris-update-transaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string pipe = "Iris.Maintenance." + Guid.NewGuid().ToString("N"), secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var pipeReady = Event(pipe + ".pipe"); using var ready = Event(pipe + ".ready");
        using var commit = Event(pipe + ".commit"); using var committed = Event(pipe + ".committed"); using var exit = Event(pipe + ".exit");
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); guard.CancelAfter(TimeSpan.FromSeconds(30));
        var system = new Boundary(root, scenario); Task? work = null; Process? parent = null;
        try
        {
            string package = MakePackage(system.InstallRoot); byte[] bytes = await File.ReadAllBytesAsync(package, guard.Token);
            string offer = JsonSerializer.Serialize(new MaintenanceMessage("prepare", secret, Path.GetFileName(package), Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length, "1.1.0"), ReleaseIdentity.Json);
            var start = new ProcessStartInfo(PowerShell) { UseShellExecute = false, CreateNoWindow = true };
            string script = ParentScript.Replace("PIPE_NAME", pipe, StringComparison.Ordinal)
                .Replace("OFFER_JSON", offer, StringComparison.Ordinal).Replace("TOKEN_VALUE", secret, StringComparison.Ordinal)
                .Replace("SIGNAL_KIND", scenario == "cancel" ? "cancel" : "commit", StringComparison.Ordinal);
            foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
            parent = Process.Start(start)!; _ = parent.Handle; system.Parent = parent;
            await Signal(pipeReady, guard.Token);
            work = new UpdateTransaction(system).RunAsync(pipe, secret, parent.Id, parent.StartTime.ToUniversalTime().Ticks, guard.Token);
            await Signal(ready, guard.Token);
            MaintenanceGate.IsBlocked(system.StateRoot).ShouldBeTrue(); system.NativeCalls.ShouldBe(0);
            commit.Set();
            if (scenario != "cancel")
            {
                await Signal(committed, guard.Token); parent.HasExited.ShouldBeFalse(); work.IsCompleted.ShouldBeFalse(); system.NativeCalls.ShouldBe(0);
            }
            exit.Set(); await parent.WaitForExitAsync(guard.Token); parent.ExitCode.ShouldBe(0);
            if (scenario is "native-failure" or "verify-failure")
            {
                await Should.ThrowAsync<CommittedUpdateException>(() => work);
                MaintenanceGate.IsBlocked(system.StateRoot).ShouldBeTrue(); system.Starts.ShouldBe(0);
            }
            else
            {
                await work; MaintenanceGate.IsBlocked(system.StateRoot).ShouldBeFalse();
                system.NativeCalls.ShouldBe(scenario == "cancel" ? 0 : 1); system.Starts.ShouldBe(scenario == "cancel" ? 0 : 1);
            }
        }
        finally
        {
            commit.Set(); exit.Set(); await guard.CancelAsync();
            if (parent != null) { await parent.WaitForExitAsync(CancellationToken.None); parent.Dispose(); }
            if (work != null) try { await work; } catch (OperationCanceledException) when (guard.IsCancellationRequested) { }
                catch (CommittedUpdateException) when (scenario is "native-failure" or "verify-failure") { }
            Directory.Delete(root, true);
        }
    }

    private static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    private static EventWaitHandle Event(string name) => new(false, EventResetMode.ManualReset, name);
    private static async Task Signal(EventWaitHandle signal, CancellationToken token)
    { (await Task.Run(() => WaitHandle.WaitAny([signal, token.WaitHandle], TimeSpan.FromSeconds(15)), token)).ShouldBe(0); }

    private sealed class Boundary(string root, string scenario) : MaintenanceSystem
    {
        internal Process Parent = null!;
        internal int NativeCalls, Starts;
        internal override string InstallRoot => Path.Combine(root, "install");
        internal override string StateRoot => Path.Combine(root, "state");
        internal override void ValidateParent(Process parent, long ticks)
        {
            parent.Id.ShouldBe(Parent.Id); parent.StartTime.ToUniversalTime().Ticks.ShouldBe(ticks);
            MaintenancePaths.Same(parent.MainModule!.FileName, PowerShell).ShouldBeTrue();
        }
        internal override Task CloseApplicationsAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        internal override Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, Action<int, long> started)
        {
            Parent.HasExited.ShouldBeTrue(); NativeCalls++; started(int.MaxValue, 0);
            arguments.ShouldContain("--norestart");
            Directory.CreateDirectory(Path.Combine(InstallRoot, "current"));
            File.WriteAllText(Path.Combine(InstallRoot, "current", ReleaseIdentity.FileName), JsonSerializer.Serialize(new ReleaseIdentity("Iris", "win", "win-x64", ReleaseIdentity.OfficialRepository, scenario == "verify-failure" ? "1.0.0" : "1.1.0")));
            return Task.FromResult(scenario == "native-failure" ? 5 : 0);
        }
        internal override Task ConfigureAsync() => Task.CompletedTask;
        internal override void StartApplication() => Starts++;
    }

    private static string MakePackage(string root)
    {
        string directory = Path.Combine(root, "packages"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "Iris-1.1.0-full.nupkg");
        using var file = File.Create(path); using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        using (var content = new StreamWriter(zip.CreateEntry("Iris.nuspec").Open(), Encoding.UTF8))
            content.Write("<package><metadata><id>Iris</id><version>1.1.0</version><channel>win</channel><rid>win-x64</rid><mainExe>Iris.App.exe</mainExe></metadata></package>");
        foreach (string name in new[] { "Iris.App.exe", "Iris.App.runtimeconfig.json", "uia/Iris.UiaHost.exe", "en-US/Iris.App.resources.dll", "maintenance/Iris.Maintenance.exe" })
            using (var content = zip.CreateEntry("lib/app/" + name).Open()) content.WriteByte(1);
        return path;
    }

    private const string ParentScript = """
        $ErrorActionPreference='Stop'
        $name='PIPE_NAME'
        $events=@{}
        foreach($key in @('pipe','ready','commit','committed','exit')) { $events[$key]=[Threading.EventWaitHandle]::OpenExisting($name+'.'+$key) }
        $server=New-Object IO.Pipes.NamedPipeServerStream($name,[IO.Pipes.PipeDirection]::InOut,1,[IO.Pipes.PipeTransmissionMode]::Byte,[IO.Pipes.PipeOptions]::Asynchronous)
        $limit=New-Object Threading.CancellationTokenSource; $limit.CancelAfter(25000)
        function Send([string]$text) { $bytes=[Text.Encoding]::UTF8.GetBytes($text);$size=[BitConverter]::GetBytes([int]$bytes.Length);$server.Write($size,0,4);$server.Write($bytes,0,$bytes.Length) }
        function Read-Exact([int]$size) { $bytes=New-Object byte[] $size;$offset=0;while($offset -lt $size){$task=$server.ReadAsync($bytes,$offset,$size-$offset,$limit.Token);$count=$task.GetAwaiter().GetResult();if($count -eq 0){throw 'EOF'};$offset+=$count};return ,$bytes }
        function Receive { $prefix=Read-Exact 4;$size=[BitConverter]::ToInt32($prefix,0);if($size -le 0 -or $size -gt 8192){throw 'Frame size'};$bytes=Read-Exact $size;return [Text.Encoding]::UTF8.GetString($bytes)|ConvertFrom-Json }
        try {
            $wait=$server.WaitForConnectionAsync($limit.Token);$null=$events['pipe'].Set();$wait.GetAwaiter().GetResult()
            Send 'OFFER_JSON'
            if((Receive).Kind -ne 'ready'){throw 'No ready'};$null=$events['ready'].Set()
            if(-not $events['commit'].WaitOne(20000)){throw 'Commit timeout'}
            Send '{"Kind":"SIGNAL_KIND","Token":"TOKEN_VALUE"}'
            if('SIGNAL_KIND' -eq 'commit') { if((Receive).Kind -ne 'committed'){throw 'No committed'};$null=$events['committed'].Set();if(-not $events['exit'].WaitOne(20000)){throw 'Exit timeout'} }
        } finally {$server.Dispose();$limit.Dispose();foreach($entry in $events.Values){$entry.Dispose()}}
        """;
}
