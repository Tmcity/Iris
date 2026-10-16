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
