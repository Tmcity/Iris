$ErrorActionPreference='Stop'
if([Environment]::UserName -ne 'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
Add-Type -AssemblyName System.Net.Http
$taskClient=New-Object Net.Http.HttpClient
$taskClient.Timeout=[TimeSpan]::FromSeconds(15)
$taskClient.DefaultRequestHeaders.Authorization=New-Object Net.Http.Headers.AuthenticationHeaderValue('Bearer','iris-t040-not-a-real-key')
$taskRows=@()
function NewMessage([string]$path,[string]$text){
    $taskRequest=New-Object Net.Http.HttpRequestMessage([Net.Http.HttpMethod]::Post,('http://127.0.0.1:18040'+$path))
    $taskBody=@{model='iris-fixture';stream=$true;max_tokens=32;messages=@(@{role='user';content=$text})}|ConvertTo-Json -Depth 6 -Compress
    $taskRequest.Content=New-Object Net.Http.StringContent($taskBody,[Text.Encoding]::UTF8,'application/json')
    return $taskRequest
}
try{
    foreach($taskPath in @('/v1/models','/anthropic/v1/models')){
        $taskResponse=$taskClient.GetAsync('http://127.0.0.1:18040'+$taskPath).GetAwaiter().GetResult()
        try{$taskBody=$taskResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();$taskRows+=@{Path=$taskPath;Status=[int]$taskResponse.StatusCode;Body=$taskBody};if([int]$taskResponse.StatusCode-ne200){throw 'Fixture model response failed'}}finally{$taskResponse.Dispose()}
    }
    foreach($taskPath in @('/v1/chat/completions','/anthropic/v1/messages')){
        $taskRequest=NewMessage $taskPath 'Iris T040 protocol probe'
        try{
            $taskResponse=$taskClient.SendAsync($taskRequest).GetAwaiter().GetResult()
            try{
                $taskBody=$taskResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                $taskRows+=@{Path=$taskPath;Status=[int]$taskResponse.StatusCode;ContentType=$taskResponse.Content.Headers.ContentType.ToString();Body=$taskBody}
                if([int]$taskResponse.StatusCode-ne200-or$taskBody-notlike'*Iris T040 synthetic translation*'){throw 'Fixture stream response failed'}
            }finally{$taskResponse.Dispose()}
        }finally{$taskRequest.Dispose()}
    }
    $taskSlowRequest=NewMessage '/v1/chat/completions' 'Iris T040 cancel probe'
    $taskFastRequest=NewMessage '/anthropic/v1/messages' 'Iris T040 concurrent probe'
    $taskWatch=[Diagnostics.Stopwatch]::StartNew()
    $taskSlow=$taskClient.SendAsync($taskSlowRequest)
    $taskFast=$taskClient.SendAsync($taskFastRequest)
    try{
        $taskFastResponse=$taskFast.GetAwaiter().GetResult()
        try{$taskFastMs=$taskWatch.Elapsed.TotalMilliseconds;$taskFastBody=$taskFastResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();$taskSlowCompletedWhenFastDone=$taskSlow.IsCompleted}
        finally{$taskFastResponse.Dispose()}
        $taskSlowResponse=$taskSlow.GetAwaiter().GetResult();$taskSlowResponse.Dispose()
        $taskRows+=@{Path='concurrency';FastCompletionMs=$taskFastMs;SlowWasComplete=$taskSlowCompletedWhenFastDone;SlowCompletionMs=$taskWatch.Elapsed.TotalMilliseconds;FastBody=$taskFastBody}
        if($taskSlowCompletedWhenFastDone-or$taskFastMs-gt2000){throw 'Synthetic fixture serializes independent requests'}
    }finally{$taskSlowRequest.Dispose();$taskFastRequest.Dispose()}
}finally{$taskClient.Dispose()}
$taskResult=@{Utc=[DateTime]::UtcNow.ToString('o');ProductOperationPerformed=$false;OnlySyntheticLoopbackRequests=$true;Results=$taskRows;Passed=$true}
$taskOut='C:\IrisT040Output\fixture-http-probe-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')+'.json'
[IO.File]::WriteAllText($taskOut,($taskResult | ConvertTo-Json -Depth 7),(New-Object Text.UTF8Encoding($false)))
