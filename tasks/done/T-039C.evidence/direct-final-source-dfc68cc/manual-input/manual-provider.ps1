param([Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
$taskBase = [IO.Path]::GetFullPath('C:\IrisAcceptanceOutput') + '\'
$taskOut = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $taskOut.StartsWith($taskBase, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must be a new acceptance session' }
$taskListener = New-Object Net.HttpListener
$taskListener.Prefixes.Add('http://127.0.0.1:18039/')
$taskWriter = New-Object IO.StreamWriter((Join-Path $taskOut 'provider-requests.jsonl'), $false, (New-Object Text.UTF8Encoding($false)))
try {
    $taskListener.Start()
    [IO.File]::WriteAllText((Join-Path $taskOut 'provider-status.json'), '{"Ready":true,"BaseUrl":"http://127.0.0.1:18039/v1","Model":"iris-fixture","SyntheticOnly":true}', (New-Object Text.UTF8Encoding($false)))
    while (-not (Test-Path -LiteralPath (Join-Path $taskOut 'stop.request'))) {
        $taskPending = $taskListener.GetContextAsync()
        while (-not $taskPending.IsCompleted -and -not (Test-Path -LiteralPath (Join-Path $taskOut 'stop.request'))) { Start-Sleep -Milliseconds 100 }
        if (-not $taskPending.IsCompleted) { break }
        $taskContext = $taskPending.GetAwaiter().GetResult()
        $taskPath = $taskContext.Request.Url.AbsolutePath
        $taskAuthMatches = $taskContext.Request.Headers['Authorization'] -ceq 'Bearer iris-evidence-not-a-real-key'
        $taskStatus = 200
        if (-not $taskAuthMatches) {
            $taskStatus = 401
            $taskBody = '{"error":{"message":"Synthetic credential did not match","type":"authentication_error"}}'
            $taskContext.Response.ContentType = 'application/json'
        } elseif ($taskContext.Request.HttpMethod -eq 'GET' -and $taskPath -eq '/v1/models') {
            $taskBody = '{"object":"list","data":[{"id":"iris-fixture","object":"model","owned_by":"local-acceptance-fixture"}]}'
            $taskContext.Response.ContentType = 'application/json'
        } elseif ($taskContext.Request.HttpMethod -eq 'POST' -and $taskPath -eq '/v1/chat/completions' -and $taskContext.Request.ContentLength64 -le 131072) {
            $taskBody = 'data: {"id":"iris-acceptance","object":"chat.completion.chunk","model":"iris-fixture","choices":[{"index":0,"delta":{"role":"assistant","content":"Iris \u516c\u5171\u5347\u7ea7\u9a8c\u8bc1"},"finish_reason":null}]}' + "`n`n" + 'data: {"id":"iris-acceptance","object":"chat.completion.chunk","model":"iris-fixture","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}' + "`n`n" + 'data: [DONE]' + "`n`n"
            $taskContext.Response.ContentType = 'text/event-stream'
        } else {
            $taskStatus = 404
            $taskBody = '{"error":{"message":"Unsupported synthetic fixture request"}}'
            $taskContext.Response.ContentType = 'application/json'
        }
        $taskContext.Response.StatusCode = $taskStatus
        $taskBytes = [Text.Encoding]::UTF8.GetBytes($taskBody)
        $taskContext.Response.ContentLength64 = $taskBytes.Length
        $taskContext.Response.OutputStream.Write($taskBytes, 0, $taskBytes.Length)
        $taskContext.Response.Close()
        $taskWriter.WriteLine((@{ Utc = [DateTime]::UtcNow.ToString('o'); Method = $taskContext.Request.HttpMethod; Path = $taskPath; Status = $taskStatus; SyntheticCredentialMatched = $taskAuthMatches } | ConvertTo-Json -Compress))
        $taskWriter.Flush()
    }
} catch {
    [IO.File]::WriteAllText((Join-Path $taskOut 'provider-failure.json'), (@{ Type = $_.Exception.GetType().Name; HResult = $_.Exception.HResult } | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
} finally { $taskListener.Stop(); $taskListener.Close(); $taskWriter.Dispose() }
