"""Run an explicit owned diagnostic/tool script in the current T-040 Sandbox.

This runner never starts Iris or an installer. Tool selection remains explicit.
"""
from pathlib import Path
import argparse
import base64
import datetime
import json
import re
import subprocess

ROOT = Path('F:/Iris')
SESSION = ROOT / 'artifacts/t040/manual-verification-20261106-2'
SANDBOX_ID = '4fc7c24d-f1e5-4b3e-adaa-2abe01f643fe'

parser = argparse.ArgumentParser()
parser.add_argument('script', help='ASCII or UTF-8-BOM PowerShell tool under tasks or session input')
parser.add_argument('--label', required=True)
parser.add_argument('--guest-root', default=r'C:\IrisT040MetricsV2', help='Already mapped owned read-only input alias')
args = parser.parse_args()
if not re.fullmatch(r'C:\\IrisT040[A-Za-z0-9]+', args.guest_root):
    raise ValueError('Unexpected owned guest input alias')
script = Path(args.script).resolve(strict=True)
allowed = [ROOT / 'tasks', SESSION / 'input']
if not any(script.is_relative_to(p) for p in allowed):
    raise ValueError('Unexpected tool location')
if script.suffix.lower() != '.ps1':
    raise ValueError('Explicit PowerShell tool required')
raw = script.read_bytes()
if any(b > 127 for b in raw) and not raw.startswith(b'\xef\xbb\xbf'):
    raise ValueError('Non-ASCII PS5.1 tool requires UTF-8 BOM')
input_script = SESSION / 'input' / script.name
if input_script != script:
    if input_script.exists() and input_script.read_bytes() != raw:
        raise ValueError('Refusing to replace an existing different input tool')
    if not input_script.exists():
        input_script.write_bytes(raw)
before = subprocess.run(['wsb.exe','list','--raw'], capture_output=True, check=True)
listed = json.loads(before.stdout.decode('utf-8-sig'))['WindowsSandboxEnvironments']
if SANDBOX_ID not in {item['Id'] for item in listed}:
    raise RuntimeError('Expected current Sandbox is absent; do not create a replacement')
stamp = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
archive = ROOT / 'tasks/T-040.evidence' / ('guest-'+args.label+'-'+stamp)
archive.mkdir(parents=True,exist_ok=False)
guest_script = (args.guest_root+'\\'+script.name).replace("'", "''")
transcript = 'C:\\IrisT040Output\\tool-transcript-'+stamp+'.txt'
invocation = ("$ErrorActionPreference='Stop'; Start-Transcript -LiteralPath '"+transcript+"' | Out-Null; "
              "try { & '"+guest_script+"'; if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw 'Nested diagnostic failed' } } "
              "catch { $_ | Format-List * -Force | Out-String | Write-Output; Stop-Transcript | Out-Null; exit 1 }; "
              "Stop-Transcript | Out-Null; exit 0")
encoded = base64.b64encode(invocation.encode('utf-16-le')).decode('ascii')
command = ['wsb.exe','exec','--id',SANDBOX_ID,'--run-as','ExistingLogin',
           '--command','powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand '+encoded,'--raw']
result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=55)
(archive/'command.log').write_bytes(result.stdout)
(archive/'command.json').write_text(json.dumps({
    'Command': command, 'StartedUtc': stamp, 'FinishedUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'RunnerExitCode': result.returncode, 'Script': str(script),
    'ProductOperationPerformed': False
},indent=2)+'\n',encoding='utf-8')
print(result.stdout.decode('utf-8-sig'),flush=True)
print(str(archive),flush=True)
result.check_returncode()
try:
    payload=json.loads(result.stdout.decode('utf-8-sig'))
except json.JSONDecodeError:
    raise RuntimeError('Unexpected wsb diagnostic execution response')
if payload.get('ExitCode') != 0:
    raise RuntimeError('Guest diagnostic tool failed; original command log retained')
