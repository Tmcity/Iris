"""Prepare a fresh owned manual session after authoritative absence of the old Sandbox."""
from pathlib import Path
import hashlib
import json
import shutil
import subprocess
import uuid

ROOT = Path('F:/Iris')
DEST = ROOT / 'artifacts/t040/manual-verification-20261107-3'
OLD = ROOT / 'artifacts/t040/manual-verification-20261106-2/input'
response = subprocess.run(['wsb.exe', 'list', '--raw'], capture_output=True, check=True)
assert not json.loads(response.stdout.decode('utf-8-sig'))['WindowsSandboxEnvironments']
DEST.mkdir(exist_ok=False)
INPUT = DEST / 'input'
INPUT.mkdir()
(DEST / 'output').mkdir()
(DEST / 'launcher').mkdir()
for name in ('Iris-0.2.11-Setup.exe', 'LOCAL_0211_payload-manifest.json',
             't040_collect_current_readonly.ps1',
             't040_save_memory_candidate_installed_readonly.ps1'):
    shutil.copyfile(OLD / name, INPUT / name)
shutil.copytree(OLD / 'tools/state-snapshot', INPUT / 'tools/state-snapshot')
for name in ('t040_capture_live_capi2_readonly.ps1', 't040_begin_cold_install_diagnostics_manual.ps1',
             't040_metrics_process_readonly.psm1', 'observe-process-metrics-v3-readonly.ps1',
             'start-metrics-observer-v3-readonly.ps1', 'save-metrics-state-v3-readonly.ps1'):
    shutil.copyfile(ROOT / 'tasks' / name, INPUT / name)
candidate_save = INPUT / 't040_save_memory_candidate_installed_readonly.ps1'
candidate_save.write_text(candidate_save.read_text(encoding='utf-8-sig').replace(
    'save-metrics-state-v2-readonly.ps1', 'save-metrics-state-v3-readonly.ps1'), encoding='utf-8-sig')
for name, script in (
    ('Begin-Install-Diagnostics.cmd', 't040_begin_cold_install_diagnostics_manual.ps1'),
    ('Save-Installed-State.cmd', 't040_save_memory_candidate_installed_readonly.ps1')):
    raw = ('@echo off\r\npowershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0' + script
           + '"\r\nset "T040_EXIT=%ERRORLEVEL%"\r\necho Exit code: %T040_EXIT%\r\npause\r\nexit /b %T040_EXIT%\r\n').encode('ascii')
    (INPUT / name).write_bytes(raw)
setup = INPUT / 'Iris-0.2.11-Setup.exe'
assert setup.stat().st_size == 116770352
assert hashlib.sha256(setup.read_bytes()).hexdigest() == 'fb4619a4de5453a174de06d7792e05594a6f89d3736600ef486e2da2619a7762'
identifier = str(uuid.uuid4())
config = '<Configuration><vGPU>Disable</vGPU><Networking>Enable</Networking><ClipboardRedirection>Disable</ClipboardRedirection><AudioInput>Disable</AudioInput><VideoInput>Disable</VideoInput><PrinterRedirection>Disable</PrinterRedirection><MemoryInMB>4096</MemoryInMB></Configuration>'
(DEST / 'Iris-T040-Session3.wsb').write_text(config + '\n', encoding='utf-8')
state = {'SandboxId': identifier, 'Stage': 'prepared-not-started', 'PreviousSandboxId': '4fc7c24d-f1e5-4b3e-adaa-2abe01f643fe',
         'PreviousSandboxAuthoritativelyAbsent': True, 'InputDirectory': str(INPUT), 'OutputDirectory': str(DEST / 'output'),
         'Version': '0.2.11', 'SourceCommit': 'dccc0dbd3054122e34e27842d55c6585ff953812', 'IrisStarted': False,
         'InstallerExecuted': False, 'OldSidSecretsCopied': False, 'SecurityPolicyChanged': False}
(DEST / 'session.json').write_text(json.dumps(state, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'PreparedDirectory': str(DEST), 'SandboxId': identifier, 'InstallerVerified': True, 'OldDataNotMigrated': True}, indent=2))
