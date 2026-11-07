param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned T-040 Sandbox only' }
$taskOut = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $taskOut.StartsWith('C:\IrisT040Output\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected diagnostic output directory' }
$taskMsi = 'C:\ProgramData\Package Cache\{7A1A9C6B-1386-478E-9C35-7242968A5634}v80.56.61678\dotnet-host-10.0.14-win-x64.msi'
if (-not (Test-Path -LiteralPath $taskMsi -PathType Leaf)) { throw 'The known Microsoft MSI is absent; no installer will be run' }
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
public static class IrisOwnedWinTrustDiagnostic {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    public struct FileInfo {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    public struct TrustData {
        public uint cbStruct;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UIChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UIContext;
        public IntPtr SignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling=true, CharSet=CharSet.Unicode)]
    public static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref TrustData data);
    public static uint[] Verify(string path, string actionId) {
        using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
            var file = new FileInfo { FilePath = path, FileHandle = stream.SafeFileHandle.DangerousGetHandle() };
            file.cbStruct = (uint)Marshal.SizeOf(typeof(FileInfo));
            var memory = Marshal.AllocHGlobal((int)file.cbStruct);
            Marshal.StructureToPtr(file, memory, false);
            var action = new Guid(actionId);
            var data = new TrustData { cbStruct=(uint)Marshal.SizeOf(typeof(TrustData)), UIChoice=2, RevocationChecks=1, UnionChoice=1, File=memory, StateAction=1, ProviderFlags=0x80001080, UIContext=0 };
            try {
                var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                data.StateAction = 2;
                var closeResult = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                return new uint[] { unchecked((uint)result), unchecked((uint)closeResult), file.cbStruct, data.cbStruct };
            } finally {
                if (data.StateAction != 2) { data.StateAction=2; WinVerifyTrust(IntPtr.Zero, ref action, ref data); }
                Marshal.DestroyStructure(memory, typeof(FileInfo));
                Marshal.FreeHGlobal(memory);
            }
        }
    }
}
'@
$taskUtf8 = New-Object Text.UTF8Encoding($false)
$taskState = @{ StartedUtc=[DateTime]::UtcNow.ToString('o'); ProbePid=$PID; MsiPath=$taskMsi; MsiSha256=(Get-FileHash -LiteralPath $taskMsi -Algorithm SHA256).Hash; OriginalActionId='6078065b-8f22-4b13-bd9b-5b762776f386'; ProviderFlags='80001080'; UIChoice=2; RevocationCheck=1; PolicyCallback='NULL'; SipClientData='NULL'; OriginalCallbackDataNotCaptured=$true; CurrentCall=$null; Calls=@(); Completed=$false; Failure=$null; InstallerExecuted=$false; ProductOperationPerformed=$false; SecurityPolicyChanged=$false; OriginalInstallRootCauseProved=$false }
function Save-T040TrustState { [IO.File]::WriteAllText((Join-Path $taskOut 'state.json'),($script:taskState | ConvertTo-Json -Depth 8),$script:taskUtf8) }
Save-T040TrustState
try {
    foreach ($taskAction in @(@{Name='GenericAuthenticode';Id='00aac56b-cd44-11d0-8cc2-00c04fc295ee'},@{Name='ConfigCI';Id='6078065b-8f22-4b13-bd9b-5b762776f386'})) {
        $taskState.CurrentCall = @{ Name=$taskAction.Name; ActionId=$taskAction.Id; StartedUtc=[DateTime]::UtcNow.ToString('o') }
        Save-T040TrustState
        $taskWatch = [Diagnostics.Stopwatch]::StartNew()
        $taskResult = [IrisOwnedWinTrustDiagnostic]::Verify($taskMsi,$taskAction.Id)
        $taskWatch.Stop()
        $taskState.Calls += @{ Name=$taskAction.Name; ActionId=$taskAction.Id; StartedUtc=$taskState.CurrentCall.StartedUtc; FinishedUtc=[DateTime]::UtcNow.ToString('o'); ElapsedMs=$taskWatch.Elapsed.TotalMilliseconds; ResultHex=$taskResult[0].ToString('X8'); CloseResultHex=$taskResult[1].ToString('X8'); FileInfoBytes=$taskResult[2]; TrustDataBytes=$taskResult[3] }
        $taskState.CurrentCall = $null
        Save-T040TrustState
    }
} catch { $taskState.Failure = @{ Type=$_.Exception.GetType().FullName; Message=$_.Exception.Message; Position=$_.InvocationInfo.PositionMessage } }
$taskState.Completed = $true
$taskState.FinishedUtc = [DateTime]::UtcNow.ToString('o')
Save-T040TrustState
if ($taskState.Failure) { throw 'Read-only trust diagnostic failed; original state was retained' }
