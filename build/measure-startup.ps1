<#
.SYNOPSIS
  Iris startup / memory / shutdown smoke measurement. Architect-provided tool (ARCH 1.2, 10.1.1).

.DESCRIPTION
  Runs Iris.App in a throw-away PORTABLE sandbox under %TEMP% (never touches %APPDATA%\Iris).
  For each run:
    1. start Iris.App.exe and wait for the AppBootstrapper "startup ready" log line (parses "<n> ms");
    2. (run 1 only) start a second instance and check it exits immediately with code 0;
    3. stay idle for -IdleSeconds, then sample Working Set and Private Bytes;
    4. simulate a Windows logoff (WM_QUERYENDSESSION + WM_ENDSESSION to all top-level windows),
       wait for exit, and count log lines written during shutdown.
  Paste the COMPLETE console output into the completion report. Do not retype or summarize numbers.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\measure-startup.ps1
  powershell -ExecutionPolicy Bypass -File build\measure-startup.ps1 -Runs 1 -IdleSeconds 5
#>
param(
    [ValidateSet("Release", "Debug")] [string]$Configuration = "Release",
    [ValidateRange(1, 20)] [int]$Runs = 3,
    [ValidateRange(1, 600)] [int]$IdleSeconds = 30
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$bin = Join-Path $repo "src\Iris.App\bin\$Configuration\net10.0-windows10.0.19041.0"
if (-not (Test-Path (Join-Path $bin "Iris.App.exe"))) {
    throw "Iris.App.exe not found under $bin. Build first: dotnet build Iris.slnx -c $Configuration"
}
if (Get-Process -Name "Iris.App" -ErrorAction SilentlyContinue) {
    throw "An Iris.App process is already running. Exit it first (the single-instance mutex is per user)."
}

if (-not ("IrisMeasure.Win" -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
namespace IrisMeasure {
    public static class Win {
        public delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
        public static List<IntPtr> TopLevelWindows(uint pid) {
            var list = new List<IntPtr>();
            EnumWindows(delegate (IntPtr h, IntPtr l) { uint p; GetWindowThreadProcessId(h, out p); if (p == pid) list.Add(h); return true; }, IntPtr.Zero);
            return list;
        }
        public static int VisibleWindowCount(uint pid) {
            int n = 0; foreach (var h in TopLevelWindows(pid)) if (IsWindowVisible(h)) n++; return n;
        }
        public static void SimulateLogoff(uint pid) {
            const uint WM_QUERYENDSESSION = 0x0011, WM_ENDSESSION = 0x0016, SMTO_ABORTIFHUNG = 0x0002;
            IntPtr ENDSESSION_LOGOFF = new IntPtr(unchecked((int)0x80000000));
            IntPtr r;
            foreach (var h in TopLevelWindows(pid)) SendMessageTimeout(h, WM_QUERYENDSESSION, IntPtr.Zero, ENDSESSION_LOGOFF, SMTO_ABORTIFHUNG, 2000, out r);
            foreach (var h in TopLevelWindows(pid)) SendMessageTimeout(h, WM_ENDSESSION, new IntPtr(1), ENDSESSION_LOGOFF, SMTO_ABORTIFHUNG, 2000, out r);
        }
    }
}
'@
}

function Read-IrisLog([string]$logDir) {
    if (-not (Test-Path $logDir)) { return @() }
    return @(Get-ChildItem $logDir -Filter "iris-*.log" | Sort-Object Name | ForEach-Object { Get-Content $_.FullName -Encoding UTF8 })
}

Write-Output ("Iris measure-startup | {0} | {1} | Configuration={2} Runs={3} IdleSeconds={4}" -f `
    (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), [Environment]::OSVersion.VersionString, $Configuration, $Runs, $IdleSeconds)
$cpuLoad = (Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average
Write-Output ("System CPU load at start: {0}% (startup-time NFR is only meaningful below 30%)" -f $cpuLoad)
Write-Output ("Binary: {0} (built {1})" -f $bin, (Get-Item (Join-Path $bin "Iris.App.dll")).LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"))

$results = @()
$secondInstance = $null
for ($i = 1; $i -le $Runs; $i++) {
    $sandbox = Join-Path $env:TEMP ("iris-measure-" + [guid]::NewGuid().ToString("N"))
    $p = $null
    try {
        Copy-Item $bin $sandbox -Recurse
        New-Item -ItemType File -Path (Join-Path $sandbox "portable.txt") | Out-Null
        $logDir = Join-Path $sandbox "data\logs"

        $p = Start-Process (Join-Path $sandbox "Iris.App.exe") -PassThru
        $startupMs = $null
        $deadline = (Get-Date).AddSeconds(20)
        while ($null -eq $startupMs -and (Get-Date) -lt $deadline -and -not $p.HasExited) {
            Start-Sleep -Milliseconds 200
            foreach ($line in (Read-IrisLog $logDir)) {
                if ($line -match '\(Iris\.App\.Hosting\.AppBootstrapper\).* (\d+(\.\d+)?) ms') { $startupMs = [double]$Matches[1]; break }
            }
        }
        if ($p.HasExited) { throw "run ${i}: Iris.App exited during startup with code $($p.ExitCode)" }
        if ($null -eq $startupMs) { throw "run ${i}: startup log line not found within 20 s" }

        if ($i -eq 1) {
            $linesBefore = (Read-IrisLog $logDir).Count
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $p2 = Start-Process (Join-Path $sandbox "Iris.App.exe") -PassThru
            $p2Exited = $p2.WaitForExit(5000)
            if (-not $p2Exited) { Stop-Process -Id $p2.Id -Force }
            $secondInstance = [pscustomobject]@{
                ExitedWithin5s = $p2Exited
                ElapsedMs      = $sw.ElapsedMilliseconds
                ExitCode       = $(if ($p2Exited) { $p2.ExitCode } else { "n/a" })
                LogLinesAdded  = (Read-IrisLog $logDir).Count - $linesBefore
            }
        }

        Start-Sleep -Seconds $IdleSeconds
        $p.Refresh()
        $visible = [IrisMeasure.Win]::VisibleWindowCount([uint32]$p.Id)
        $ws = [math]::Round($p.WorkingSet64 / 1MB, 1)
        $pb = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)

        $linesBeforeExit = (Read-IrisLog $logDir).Count
        [IrisMeasure.Win]::SimulateLogoff([uint32]$p.Id)
        $exited = $p.WaitForExit(10000)
        if (-not $exited) { Stop-Process -Id $p.Id -Force }
        Start-Sleep -Milliseconds 300
        $shutdownLines = @(Read-IrisLog $logDir | Select-Object -Skip $linesBeforeExit)

        $results += [pscustomobject]@{
            Run              = $i
            StartupMs        = $startupMs
            VisibleWindows   = $visible
            WorkingSetMB     = $ws
            PrivateBytesMB   = $pb
            ExitCode         = $(if ($exited) { $p.ExitCode } else { "timeout" })
            ShutdownLogLines = $shutdownLines.Count
        }
        if ($i -eq 1) {
            Write-Output "--- run 1: log lines written during logoff simulation ---"
            if ($shutdownLines.Count -eq 0) { Write-Output "(none)" } else { $shutdownLines | ForEach-Object { Write-Output $_ } }
        }
    }
    finally {
        if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force }
        Start-Sleep -Milliseconds 300
        Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Output "--- second instance (run 1) ---"
$secondInstance | Format-List | Out-String | Write-Output
Write-Output "--- per run ---"
$results | Format-Table -AutoSize | Out-String | Write-Output
Write-Output ("--- averages --- StartupMs={0:N1}  WorkingSetMB={1:N1}  PrivateBytesMB={2:N1}" -f `
    ($results | Measure-Object StartupMs -Average).Average,
    ($results | Measure-Object WorkingSetMB -Average).Average,
    ($results | Measure-Object PrivateBytesMB -Average).Average)
Write-Output "--- NFR (ARCH 1.2): StartupMs <= 1500; PrivateBytesMB <= 100; WorkingSetMB <= 160; VisibleWindows = 0; ExitCode = 0; ShutdownLogLines >= 1 ---"
