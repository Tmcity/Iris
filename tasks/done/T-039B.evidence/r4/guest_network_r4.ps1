$ErrorActionPreference = 'Stop'
$output = 'C:\IrisBOutput'
$report = [ordered]@{ Checks=@(); Failure=$null }
function Record([string]$Name, $Value) {
    $script:report.Checks += [pscustomobject]@{Name=$Name;Value=$Value}
    $script:report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $output 'progress.json') -Encoding UTF8
}
try {
    Record 'guest-only-diagnostic' @{User=[Environment]::UserName;RuntimeInstallPerformed=$false;IrisExecuted=$false}
    Get-NetIPConfiguration | Format-List | Out-String | Set-Content -LiteralPath (Join-Path $output 'guest-ip.txt') -Encoding UTF8
    foreach ($dnsServer in @('1.1.1.1','8.8.8.8')) {
        try {
            $answers=@(Resolve-DnsName -Name 'builds.dotnet.microsoft.com' -Type A -Server $dnsServer -DnsOnly -QuickTimeout | Select-Object Name,Type,IPAddress,NameHost)
            Record ('public-dns-'+$dnsServer) $answers
        } catch { Record ('public-dns-'+$dnsServer) $_.ToString() }
    }
    $endpoints = @(
        @('blob-version','https://dotnetcli.blob.core.windows.net/dotnet/WindowsDesktop/10.0/latest.version'),
        @('builds-version','https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0/latest.version'),
        @('builds-runtime-head','https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.13/windowsdesktop-runtime-10.0.13-win-x64.exe'),
        @('blob-runtime-head','https://dotnetcli.blob.core.windows.net/dotnet/WindowsDesktop/10.0.13/windowsdesktop-runtime-10.0.13-win-x64.exe'),
        @('public-doh','https://dns.google/resolve?name=builds.dotnet.microsoft.com&type=A'),
        @('metadata-default','https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json'),
        @('metadata-tls12','https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json')
    )
    foreach ($endpoint in $endpoints) {
        $name=$endpoint[0]; $url=$endpoint[1]
        $arguments=@('--silent','--show-error','--verbose','--location','--max-time','40','--output',(Join-Path $output ($name+'.body.txt')))
        if ($name -like '*-head') { $arguments += '--head' }
        if ($name -eq 'metadata-tls12') { $arguments += @('--tls-max','1.2') }
        $arguments += $url
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = (Join-Path $env:SystemRoot 'System32\curl.exe')
        $start.Arguments = (($arguments | ForEach-Object { '"' + $_ + '"' }) -join ' ')
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true
        $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
        $child=[Diagnostics.Process]::Start($start)
        $stdout=$child.StandardOutput.ReadToEndAsync(); $stderr=$child.StandardError.ReadToEndAsync()
        $child.WaitForExit()
        [IO.File]::WriteAllText((Join-Path $output ($name+'.stdout.txt')), $stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $output ($name+'.stderr.txt')), $stderr.GetAwaiter().GetResult())
        Record $name @{Url=$url;ExitCode=$child.ExitCode}
        $child.Dispose()
    }
} catch { $report.Failure=$_.ToString() }
finally { $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding UTF8 }
if ($report.Failure) { exit 2 }; exit 0
