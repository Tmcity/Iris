param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if([Environment]::UserName -ne 'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
$taskOut=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $taskOut.StartsWith('C:\IrisT040Output\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected output directory'}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class IrisOwnedSaferDiagnostic {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    public struct CodeProperties {
        public uint cbSize;
        public uint dwCheckFlags;
        [MarshalAs(UnmanagedType.LPWStr)] public string ImagePath;
        public IntPtr hImageFileHandle;
        public uint UrlZoneId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst=64)] public byte[] ImageHash;
        public uint dwImageHashSize;
        public long ImageSize;
        public uint HashAlgorithm;
        public IntPtr pByteBlock;
        public IntPtr hWndParent;
        public uint dwWVTUIChoice;
    }
    [DllImport("advapi32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    public static extern bool SaferIdentifyLevel(uint count,ref CodeProperties properties,out IntPtr level,IntPtr reserved);
    [DllImport("advapi32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    public static extern bool SaferCloseLevel(IntPtr level);
}
'@
$taskMsi='C:\ProgramData\Package Cache\{7A1A9C6B-1386-478E-9C35-7242968A5634}v80.56.61678\dotnet-host-10.0.14-win-x64.msi'
if(-not(Test-Path -LiteralPath $taskMsi -PathType Leaf)){throw 'Expected Microsoft MSI cache is absent'}
$taskState=[ordered]@{StartedUtc=[DateTime]::UtcNow.ToString('o');ProbePid=$PID;Username=[Environment]::UserName;MsiPath=$taskMsi;ProductOperationPerformed=$false;SecurityPolicyChanged=$false;Calls=@();Completed=$false;CurrentCall=$null;Failure=$null}
$taskUtf8=New-Object Text.UTF8Encoding($false)
function SaveState{[IO.File]::WriteAllText((Join-Path $taskOut 'state.json'),($script:taskState | ConvertTo-Json -Depth 7),$script:taskUtf8)}
SaveState
try{
    foreach($taskFlags in @(1,9)){
        $taskProperties=New-Object IrisOwnedSaferDiagnostic+CodeProperties
        $taskProperties.cbSize=[Runtime.InteropServices.Marshal]::SizeOf($taskProperties)
        $taskProperties.dwCheckFlags=$taskFlags
        $taskProperties.ImagePath=$taskMsi
        $taskProperties.ImageHash=New-Object byte[] 64
        $taskProperties.dwWVTUIChoice=2
        $taskLevel=[IntPtr]::Zero
        $taskStarted=[DateTime]::UtcNow.ToString('o')
        $taskState.CurrentCall=@{Flags=$taskFlags;StartedUtc=$taskStarted;StructBytes=$taskProperties.cbSize};SaveState
        $taskWatch=[Diagnostics.Stopwatch]::StartNew()
        $taskSuccess=[IrisOwnedSaferDiagnostic]::SaferIdentifyLevel(1,[ref]$taskProperties,[ref]$taskLevel,[IntPtr]::Zero)
        $taskError=[Runtime.InteropServices.Marshal]::GetLastWin32Error()
        $taskWatch.Stop()
        $taskClosed=$null
        if($taskLevel -ne [IntPtr]::Zero){$taskClosed=[IrisOwnedSaferDiagnostic]::SaferCloseLevel($taskLevel)}
        $taskState.Calls+=@{Flags=$taskFlags;StartedUtc=$taskStarted;FinishedUtc=[DateTime]::UtcNow.ToString('o');ElapsedMs=$taskWatch.Elapsed.TotalMilliseconds;Success=$taskSuccess;LastError=if($taskSuccess){0}else{$taskError};LevelHandleClosed=$taskClosed;StructBytes=$taskProperties.cbSize}
        $taskState.CurrentCall=$null;SaveState
    }
}catch{$taskState.Failure=@{Type=$_.Exception.GetType().FullName;Id=$_.FullyQualifiedErrorId}}
$taskState.Completed=$true;$taskState.FinishedUtc=[DateTime]::UtcNow.ToString('o');SaveState
