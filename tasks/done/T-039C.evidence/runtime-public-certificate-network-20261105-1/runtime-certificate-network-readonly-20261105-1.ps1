$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned acceptance Sandbox only' }
Add-Type -AssemblyName System.Net.Http
$taskState = Get-Content -LiteralPath 'C:\IrisAcceptanceOutput\active-session.json' -Raw -Encoding UTF8 | ConvertFrom-Json
$taskSession = [IO.Path]::GetFullPath($taskState.SessionDirectory)
if (-not $taskSession.StartsWith('C:\IrisAcceptanceOutput\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected diagnostic output' }
$taskCertificateRecord = Get-Content -LiteralPath (Join-Path $taskSession 'runtime-public-certificates-20261105-1.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$taskExtensions = @($taskCertificateRecord.Rows[0].Certificate.Extensions)
$taskHttpUrls = @(
    'http://www.microsoft.com/pkiops/crl/Microsoft%20Code%20Signing%20PCA%202024.crl',
    'http://www.microsoft.com/pkiops/certs/Microsoft%20Code%20Signing%20PCA%202024.crt'
)
foreach ($taskUrl in $taskHttpUrls) {
    if (-not ($taskExtensions.Value -join "`n").Contains($taskUrl)) { throw 'Diagnostic URL is not bound to the actual public signing certificate' }
}
$taskRows = @()
foreach ($taskOriginal in $taskHttpUrls) {
    foreach ($taskUrl in @($taskOriginal, $taskOriginal.Replace('http://','https://'))) {
        $taskHandler = New-Object Net.Http.HttpClientHandler
        $taskHandler.AllowAutoRedirect = $true
        $taskHandler.UseCookies = $false
        $taskHandler.UseDefaultCredentials = $false
        $taskHandler.DefaultProxyCredentials = $null
        $taskClient = New-Object Net.Http.HttpClient($taskHandler)
        $taskClient.Timeout = [TimeSpan]::FromSeconds(12)
        $taskTimer = [Diagnostics.Stopwatch]::StartNew()
        $taskRow = [ordered]@{ Url = $taskUrl; OriginalCertificateUrl = $taskOriginal; Status = $null; FinalUrl = $null; Bytes = $null; BodySha256 = $null; BodyExcerpt = $null; ElapsedMilliseconds = $null; Failure = $null; AuthenticationAdded = $false }
        try {
            $taskResponse = $taskClient.GetAsync($taskUrl).GetAwaiter().GetResult()
            try {
                $taskRow.Status = [int]$taskResponse.StatusCode
                $taskRow.FinalUrl = $taskResponse.RequestMessage.RequestUri.AbsoluteUri
                $taskBytes = $taskResponse.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
                if ($taskBytes.Length -gt 16MB) { throw 'Public certificate response exceeded its diagnostic budget' }
                $taskRow.Bytes = $taskBytes.Length
                $taskHash = [Security.Cryptography.SHA256]::Create()
                try { $taskRow.BodySha256 = [BitConverter]::ToString($taskHash.ComputeHash($taskBytes)).Replace('-','').ToLowerInvariant() }
                finally { $taskHash.Dispose() }
                if ($taskRow.Status -ne 200 -and $taskBytes.Length -lt 1024) { $taskRow.BodyExcerpt = [Text.Encoding]::UTF8.GetString($taskBytes) }
            } finally { $taskResponse.Dispose() }
        } catch { $taskRow.Failure = @{ Type = $_.Exception.GetType().FullName; Message = $_.Exception.Message } }
        finally { $taskTimer.Stop(); $taskRow.ElapsedMilliseconds = $taskTimer.ElapsedMilliseconds; $taskClient.Dispose() }
        $taskRows += $taskRow
    }
}
$taskResult = @{ Utc = [DateTime]::UtcNow.ToString('o'); ReadOnlyNetworkDiagnostic = $true; ProductOperationPerformed = $false; CertificateStoreChanged = $false; SystemSettingsChanged = $false; Rows = $taskRows }
$taskOutput = Join-Path $taskSession 'runtime-certificate-network-20261105-1.json'
if (Test-Path -LiteralPath $taskOutput) { throw 'This diagnostic output already exists' }
[IO.File]::WriteAllText($taskOutput, ($taskResult | ConvertTo-Json -Depth 7), (New-Object Text.UTF8Encoding($false)))
