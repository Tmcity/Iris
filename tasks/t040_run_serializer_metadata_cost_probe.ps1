$ErrorActionPreference='Stop'
if([Environment]::UserName -ne 'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
$taskExe=Join-Path $PSScriptRoot 'tools\serializer-metadata-cost\SerializerMetadataCostProbe.exe'
foreach($taskMode in @('reflection','generated','reflection','generated','reflection','generated')){
    $taskStamp=[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')
    $taskProcess=Start-Process -FilePath $taskExe -ArgumentList @($taskMode) -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput ('C:\IrisT040Output\serializer-cost-stdout-'+$taskStamp+'.txt') -RedirectStandardError ('C:\IrisT040Output\serializer-cost-stderr-'+$taskStamp+'.txt')
    if($taskProcess.ExitCode -ne 0){throw 'Owned serializer cost probe failed; retain its logs'}
}
Write-Host 'Six isolated serializer cost runs completed; installed Iris was not invoked.'
