"""Prepare a fresh user-operated 0.2.2 guest; preserve the previous disposable session."""
from pathlib import Path
import hashlib
import json
import xml.etree.ElementTree as ET

ROOT = Path("F:/Iris")
OLD = ROOT / "artifacts/t039c/manual-verification-v022-20261101-1"
NEW = ROOT / "artifacts/t039c/manual-verification-v022-20261105-1"
NEW.mkdir(exist_ok=False)
(NEW / "input").mkdir()
(NEW / "output").mkdir()
copied = []
input_names = (
    "Iris-0.2.2-Official-Download.url", "Begin-Manual-Evidence.cmd", "Save-Installed-State.cmd",
    "Save-Offer-State.cmd", "Save-Ready-State.cmd", "Save-Updated-State.cmd", "Save-Uninstalled-State.cmd",
    "Stop-Manual-Evidence.cmd", "manual-capture-state.ps1", "manual-provider.ps1",
    "manual-stop-evidence.ps1", "save-update-evidence.ps1", "inspect-old-ready-readonly.ps1",
    "verify-public-install-readonly.ps1", "PUBLIC_payload-manifest.json", "PUBLIC_release-provenance.json",
    "PUBLIC_SHA256.json", "public-release-v022.json", "manual-process-watch-v3.ps1",
    "start-process-watch-v3.ps1",
)
for name in input_names:
    source = OLD / "input" / name
    data = source.read_bytes()
    (NEW / "input" / name).open("xb").write(data)
    copied.append({"Source": source.relative_to(ROOT).as_posix(), "Destination": "input/" + name,
                   "Bytes": len(data), "Sha256": hashlib.sha256(data).hexdigest()})
assert hashlib.sha256((NEW / "input/manual-process-watch-v3.ps1").read_bytes()).hexdigest() == (
    "3515546d9e344331cc2c95fc4c97f60b213bfc06ae11ada98d96349b3e3dac62")
for name in ("Start-Iris-Acceptance-Sandbox.ps1", "Open-Iris-Sandbox.cmd",
             "Reconnect-Iris-Acceptance-Sandbox.ps1", "Reconnect-Iris-Sandbox.cmd"):
    source = OLD / name
    data = source.read_bytes()
    (NEW / name).open("xb").write(data)
    copied.append({"Source": source.relative_to(ROOT).as_posix(), "Destination": name,
                   "Bytes": len(data), "Sha256": hashlib.sha256(data).hexdigest()})
config = ET.fromstring((OLD / "Iris-Manual-Acceptance.wsb").read_bytes())
assert config.find("LogonCommand") is None
folders = config.findall("MappedFolders/MappedFolder")
assert len(folders) == 2
for folder, name, readonly in zip(folders, ("input", "output"), ("true", "false")):
    folder.find("HostFolder").text = str(NEW / name)
    assert folder.findtext("ReadOnly") == readonly
(NEW / "Iris-Manual-Acceptance.wsb").write_bytes(ET.tostring(config, encoding="utf-8"))

starter = r'''$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Run only inside the acceptance Sandbox' }
$taskRoot = 'C:\IrisAcceptanceOutput'
$taskPointer = Join-Path $taskRoot 'active-session.json'
if (Test-Path -LiteralPath $taskPointer) { throw 'This guest already has an acceptance session. Keep its evidence and use its Save commands.' }
$taskSession = Join-Path $taskRoot ('session-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $taskSession
$taskShell = 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
$taskUtf8 = New-Object Text.UTF8Encoding($false)
$taskProvider = $null
$taskState = [ordered]@{
    SessionDirectory = $taskSession
    StartedUtc = [DateTime]::UtcNow.ToString('o')
    ProviderPid = $null
    WatcherPid = $null
    RecorderVersion = 3
    Ready = $false
    Failure = $null
    Scope = 'Owned synthetic provider and read-only process observer; user manually installs and updates production Iris'
}
function SaveSessionState {
    [IO.File]::WriteAllText($script:taskPointer, ($script:taskState | ConvertTo-Json -Depth 6), $script:taskUtf8)
}
try {
    $taskProvider = Start-Process -FilePath $taskShell -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File','C:\IrisAcceptanceInput\manual-provider.ps1','-OutputDirectory',$taskSession) -WindowStyle Hidden -PassThru
    $taskState.ProviderPid = $taskProvider.Id
    SaveSessionState
    $taskLimit = [DateTime]::UtcNow.AddSeconds(15)
    $taskProviderStatus = Join-Path $taskSession 'provider-status.json'
    while (-not (Test-Path -LiteralPath $taskProviderStatus)) {
        if ($taskProvider.HasExited) { throw 'Synthetic provider exited before becoming ready' }
        if ([DateTime]::UtcNow -ge $taskLimit) { throw 'Synthetic provider did not become ready' }
        Start-Sleep -Milliseconds 100
    }
    $taskStatus = Get-Content -LiteralPath $taskProviderStatus -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $taskStatus.Ready -or $taskProvider.HasExited) { throw 'Synthetic provider is unavailable' }
    & (Join-Path $PSScriptRoot 'start-process-watch-v3.ps1')
    $taskRecorder = Get-Content -LiteralPath (Join-Path $taskSession 'active-recorder-v2.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $taskRecorder.Ready -or $taskRecorder.RecorderVersion -ne 3) { throw 'V3 observer did not become ready' }
    $taskState.WatcherPid = $taskRecorder.RecorderPid
    $taskState.Ready = $true
    SaveSessionState
    $taskState | ConvertTo-Json -Depth 6
    Write-Output 'Evidence helpers are ready. Install Iris manually from the official 0.2.2 download link.'
} catch {
    $taskState.Failure = @{ Type = $_.Exception.GetType().FullName; Message = $_.Exception.Message }
    [IO.File]::WriteAllText((Join-Path $taskSession 'stop.request'), 'startup failed', $taskUtf8)
    SaveSessionState
    if ($taskProvider) { $null = $taskProvider.WaitForExit(15000) }
    throw
} finally {
    if ($taskProvider) { $taskProvider.Dispose() }
}
'''
(NEW / "input/manual-start-evidence.ps1").write_text(starter.replace("\n", "\r\n"), encoding="utf-8", newline="")
save_old = r'''@echo off
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0save-update-evidence.ps1" -Stage old-ready
echo.
pause
'''
(NEW / "input/Save-Old-Ready-State.cmd").write_text(save_old.replace("\n", "\r\n"), encoding="ascii", newline="")
instructions = """# 新会话：正式 0.2.2 首次安装

2026-11-05 实际查询没有运行中的 Sandbox。旧会话及其安装、合成数据、记录器证据继续保留；升级验收需要先在新的会话建立旧版实例。本入口只创建和连接 Sandbox，不安装 Iris，不准备运行时缓存。

1. 在宿主双击本目录 `Open-Iris-Sandbox.cmd`，等待 Sandbox 桌面。如果启动失败，保留控制台，反馈步骤、退出码及错误原文。
2. 在 Sandbox 的 `C:\\IrisAcceptanceInput` 双击 `Begin-Manual-Evidence.cmd`。控制台应显示 `Ready: true`（JSON 的属性名为 Ready）。它启动本单虚构翻译服务和 V3 只读记录器，并验证自有辅助进程的启动/退出；辅助验证不能代替生产升级验收。该命令只运行一次。
3. 同目录双击 `Iris-0.2.2-Official-Download.url`，下载正式 0.2.2 安装器，手工执行安装。系统下载/安装运行时按实际提示操作。
4. 安装后从 Iris 托盘打开设置查看版本，双击同目录 `Save-Installed-State.cmd` 保存记录。反馈安装成功/失败、显示版本和异常原文。

此段只核对安装。保留 Sandbox 和 Iris 实例，之后再准备保存的更新开关、虚构凭据与历史，然后在新版本正式发布通过后分段验证升级及卸载。

正式安装器： https://github.com/Tmcity/Iris/releases/download/v0.2.2/Iris-0.2.2-Setup.exe

正式 Setup SHA256： `ddae984ba4bcc65e5398ecd7a036afe0485966c5d61fe9b1603cc9bb487e7a0c`

宿主输出目录：本目录 `output/session-*`。不要使用旧会话的进程 ID 或数据哈希证明此会话状态。
"""
(NEW / "input/FIRST_INSTALL.md").write_text(instructions, encoding="utf-8", newline="\n")
(NEW / "MANUAL_FIRST_INSTALL.md").write_text(instructions, encoding="utf-8", newline="\n")
fingerprints = []
for path in sorted(NEW.rglob("*")):
    if path.is_file():
        data = path.read_bytes()
        fingerprints.append({"Path": path.relative_to(NEW).as_posix(), "Bytes": len(data),
                             "Sha256": hashlib.sha256(data).hexdigest()})
(NEW / "input-fingerprints.json").write_text(json.dumps({"CopiedFromPreviousMaterials": copied,
    "Files": fingerprints, "AutomaticIrisOperation": False, "RuntimeCachePrefilled": False,
    "OldSessionPreserved": True}, indent=2) + "\n", encoding="utf-8", newline="\n")
print(json.dumps({"Prepared": str(NEW), "Files": len(fingerprints), "AutomaticIrisOperation": False}, indent=2))
