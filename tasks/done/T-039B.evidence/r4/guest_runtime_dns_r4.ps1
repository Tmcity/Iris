# Owned guest network fixture only; leave production URLs, TLS and package bytes unchanged.
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only.' }
$dnsOutput=Join-Path $output 'runtime-dns'
New-Item -ItemType Directory -Path $dnsOutput | Out-Null
$hostsFile=Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
Copy-Item -LiteralPath $hostsFile -Destination (Join-Path $dnsOutput 'hosts-before.txt')
foreach ($endpoint in @('builds.dotnet.microsoft.com','dotnetcli.blob.core.windows.net')) {
    $original=@(Resolve-DnsName -Name $endpoint -Type A -DnsOnly | Select-Object Name,Type,IPAddress,NameHost)
    $response=Join-Path $dnsOutput ($endpoint+'.json')
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=Join-Path $env:SystemRoot 'System32\curl.exe'
    $start.Arguments='--fail --silent --show-error --retry 2 --retry-all-errors --max-time 40 --output "'+$response+'" "https://dns.google/resolve?name='+$endpoint+'&type=A"'
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $child=[Diagnostics.Process]::Start($start)
    $stdout=$child.StandardOutput.ReadToEndAsync(); $stderr=$child.StandardError.ReadToEndAsync()
    $child.WaitForExit()
    [IO.File]::WriteAllText((Join-Path $dnsOutput ($endpoint+'.stdout.log')), $stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $dnsOutput ($endpoint+'.stderr.log')), $stderr.GetAwaiter().GetResult())
    $code=$child.ExitCode; $child.Dispose()
    Record ('guest-runtime-doh-'+$endpoint) @{ExitCode=$code;OriginalAnswers=$original}
    if ($code -ne 0) { throw 'Guest public DNS request failed.' }
    $answers=Get-Content -Raw -LiteralPath $response | ConvertFrom-Json
    $address=@($answers.Answer | Where-Object { $_.type -eq 1 })[0].data
    $parsed=$null
    if ($answers.Status -ne 0 -or -not [Net.IPAddress]::TryParse($address,[ref]$parsed) -or $parsed.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or $address -match '^(127\.|0\.|10\.|192\.168\.|198\.1[89]\.)') { throw 'Invalid public Microsoft endpoint address.' }
    Add-Content -LiteralPath $hostsFile -Value ($address+' '+$endpoint) -Encoding ASCII
    Record ('guest-runtime-address-'+$endpoint) @{Address=$address;Source='https://dns.google/resolve';HostNetworkChanged=$false;TlsValidationUnchanged=$true}
}
Clear-DnsClientCache
Copy-Item -LiteralPath $hostsFile -Destination (Join-Path $dnsOutput 'hosts-after.txt')
Record 'guest-runtime-dns-applied' @{Runtimes=@(Runtimes);NoRuntimeInstalledByFixture=$true}
