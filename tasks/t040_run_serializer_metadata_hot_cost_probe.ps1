$ErrorActionPreference='Stop'
if([Environment]::UserName -ne 'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
$taskExe=Join-Path $PSScriptRoot 'tools\serializer-metadata-hot-cost\SerializerMetadataHotCostProbe.exe'
foreach($taskMode in @('reflection','generated')){
    $taskStamp=[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')
    $taskProcess=Start-Process -FilePath $taskExe -ArgumentList @($taskMode) -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput ('C:\IrisT040Output\serializer-hot-cost-stdout-'+$taskStamp+'.txt') -RedirectStandardError ('C:\IrisT040Output\serializer-hot-cost-stderr-'+$taskStamp+'.txt')
    if($taskProcess.ExitCode -ne 0){throw 'Owned repeated serializer cost probe failed; retain its logs'}
}
Write-Host 'Repeated serializer comparison completed; installed Iris was not invoked.'
