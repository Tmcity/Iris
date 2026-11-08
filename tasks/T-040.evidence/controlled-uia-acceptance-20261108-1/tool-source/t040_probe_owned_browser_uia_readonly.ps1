$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class T040OwnedForegroundQuery {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    public static string Title(IntPtr window) { var text = new StringBuilder(512); GetWindowText(window, text, text.Capacity); return text.ToString(); }
}
'@
$taskWindow = [T040OwnedForegroundQuery]::GetForegroundWindow()
$taskSourcePid = [uint32]0
$null = [T040OwnedForegroundQuery]::GetWindowThreadProcessId($taskWindow, [ref]$taskSourcePid)
$taskSource = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $taskSourcePid)
$taskTitle = [T040OwnedForegroundQuery]::Title($taskWindow)
if (-not $taskSource -or $taskSource.Name -ne 'msedge.exe' -or $taskTitle -notlike 'Iris T040 owned UI scene*') { throw 'Only the observed local synthetic browser scene may be inspected' }
$taskHelper = Join-Path $env:LOCALAPPDATA 'Iris\current\uia\Iris.UiaHost.exe'
$taskIdentity = [IO.File]::ReadAllText((Join-Path $env:LOCALAPPDATA 'Iris\current\release-source.json')) | ConvertFrom-Json
if ($taskIdentity.Version -ne '0.2.13') { throw 'Expected reviewed installed candidate' }
$taskOut = Join-Path 'C:\IrisT040Output' ('owned-browser-uia-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'))
$null = New-Item -ItemType Directory -Path $taskOut
$taskInfo = New-Object Diagnostics.ProcessStartInfo
$taskInfo.FileName = $taskHelper
$taskInfo.UseShellExecute = $false
$taskInfo.CreateNoWindow = $true
$taskInfo.RedirectStandardInput = $true
$taskInfo.RedirectStandardOutput = $true
$taskInfo.RedirectStandardError = $true
$taskInfo.StandardOutputEncoding = New-Object Text.UTF8Encoding($false)
$taskProcess = New-Object Diagnostics.Process
$taskProcess.StartInfo = $taskInfo
$taskRecord = @{ StartedUtc = [DateTime]::UtcNow.ToString('o'); SourcePid = $taskSourcePid; SourceWindow = $taskWindow.ToInt64(); SourceName = $taskSource.Name; OwnSyntheticSceneConfirmed = $true; ProductOperationPerformed = $false; ForegroundInputInjected = $false; InstalledHelperUsed = $true; HelperSha256 = (Get-FileHash -LiteralPath $taskHelper -Algorithm SHA256).Hash; Stage = 'Start'; Completed = $false; Failure = $null; WholeFeatureProved = $false }
$taskUtf8 = New-Object Text.UTF8Encoding($false)
function Save-T040OwnedUia { [IO.File]::WriteAllText((Join-Path $script:taskOut 'state.json'), ($script:taskRecord | ConvertTo-Json -Depth 7), $script:taskUtf8) }
Save-T040OwnedUia
try {
    if (-not $taskProcess.Start()) { throw 'Reviewed read-only helper was not started' }
    $null = $taskProcess.Handle
    $taskRecord.HelperPid = $taskProcess.Id
    $taskRecord.Stage = 'AwaitReady'
    Save-T040OwnedUia
    $taskReadyRead = $taskProcess.StandardOutput.ReadLineAsync()
    if (-not $taskReadyRead.Wait(5000)) { throw 'Exact helper is still running; its identity is retained for inspection' }
    $taskRecord.Ready = $taskReadyRead.Result | ConvertFrom-Json
    $taskRequest = @{ protocolVersion = 1; kind = 'Request'; requestId = [Guid]::NewGuid().ToString('N'); hwnd = $taskWindow.ToInt64(); processId = $taskSourcePid }
    $taskRecord.Stage = 'AwaitReply'
    $taskTimer = [Diagnostics.Stopwatch]::StartNew()
    $taskProcess.StandardInput.WriteLine(($taskRequest | ConvertTo-Json -Compress))
    $taskProcess.StandardInput.Close()
    $taskReplyRead = $taskProcess.StandardOutput.ReadLineAsync()
    if (-not $taskReplyRead.Wait(5000)) { throw 'Exact helper did not reply within the diagnostic bound; inspect it before retrying' }
    $taskTimer.Stop()
    $taskReply = $taskReplyRead.Result | ConvertFrom-Json
    $taskRecord.RequestElapsedMilliseconds = $taskTimer.Elapsed.TotalMilliseconds
    $taskRecord.ReplyStatus = $taskReply.status
    $taskRecord.ReplyErrorCode = $taskReply.errorCode
    $taskRecord.SyntheticTextMatched = $taskReply.text -eq 'Iris T040 selection sample 013'
    $taskRecord.TextLength = if ($null -eq $taskReply.text) { 0 } else { $taskReply.text.Length }
    if ($taskRecord.SyntheticTextMatched) { $taskRecord.SyntheticText = $taskReply.text }
    if (-not $taskProcess.WaitForExit(5000)) { throw 'Read-only helper exit is not confirmed; exact identity retained' }
    $taskRecord.ExitCode = $taskProcess.ExitCode
    $taskRecord.Stage = 'Complete'
} catch { $taskRecord.Failure = @{ Type = $_.Exception.GetType().FullName; Message = $_.Exception.Message }; $taskRecord.Stage = 'Failed' }
finally { $taskRecord.Completed = $true; $taskRecord.FinishedUtc = [DateTime]::UtcNow.ToString('o'); Save-T040OwnedUia; $taskProcess.Dispose() }
Write-Output $taskOut
if ($taskRecord.Failure) { throw 'Read-only UIA diagnostic failed; original state and exact helper identity retained' }
