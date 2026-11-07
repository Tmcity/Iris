"""Run an explicitly selected T-040 tool in an existing, identified Sandbox.

The default is a diagnostic. Explicitly authorized product operations must be
marked separately. The runner never creates a Sandbox or runs an installer.
"""
from pathlib import Path
import argparse
import base64
import datetime
import hashlib
import json
import re
import subprocess

ROOT = Path('F:/Iris')
parser = argparse.ArgumentParser()
parser.add_argument('script')
parser.add_argument('--session-directory', required=True)
parser.add_argument('--guest-root', required=True)
parser.add_argument('--label', required=True)
parser.add_argument('--product-operation', action='store_true')
args = parser.parse_args()
session = Path(args.session_directory).resolve(strict=True)
if session.parent != ROOT / 'artifacts/t040' or not session.name.startswith('manual-verification-'):
    raise ValueError('Explicit owned T-040 session required')
state = json.loads((session / 'session.json').read_text(encoding='utf-8-sig'))
sandbox_id = state['SandboxId']
if not re.fullmatch(r'[a-f0-9-]{36}', sandbox_id):
    raise ValueError('Invalid Sandbox identifier')
if not re.fullmatch(r'C:\\IrisT040[A-Za-z0-9]+', args.guest_root):
    raise ValueError('Unexpected read-only guest input alias')
if not re.fullmatch(r'[a-zA-Z0-9-]+', args.label):
    raise ValueError('Unexpected archive label')
script = Path(args.script).resolve(strict=True)
if not (script.is_relative_to(ROOT / 'tasks') or script.is_relative_to(session)) or script.suffix.lower() != '.ps1':
    raise ValueError('Explicit diagnostic script under tasks or the session required')
raw = script.read_bytes()
if any(b > 127 for b in raw) and not raw.startswith(b'\xef\xbb\xbf'):
    raise ValueError('PowerShell 5.1 requires a UTF-8 BOM for non-ASCII scripts')
response = subprocess.run(['wsb.exe', 'list', '--raw'], capture_output=True, check=True)
listed = json.loads(response.stdout.decode('utf-8-sig'))['WindowsSandboxEnvironments']
if sandbox_id not in {item['Id'] for item in listed}:
    raise RuntimeError('The identified Sandbox is absent; no replacement was started')
stamp = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
archive = ROOT / 'tasks/T-040.evidence' / ('guest-' + args.label + '-' + stamp)
archive.mkdir(parents=True, exist_ok=False)
guest_script = args.guest_root + '\\' + script.name
transcript = 'C:\\IrisT040Output\\tool-transcript-' + stamp + '.txt'
invocation = ("$ErrorActionPreference='Stop'; Start-Transcript -LiteralPath '" + transcript + "' | Out-Null; "
              "try { & '" + guest_script.replace("'", "''") + "'; if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw 'Nested diagnostic failed' } } "
              "catch { $_ | Format-List * -Force | Out-String | Write-Output; Stop-Transcript | Out-Null; exit 1 }; "
              "Stop-Transcript | Out-Null; exit 0")
encoded = base64.b64encode(invocation.encode('utf-16-le')).decode('ascii')
command = ['wsb.exe', 'exec', '--id', sandbox_id, '--run-as', 'ExistingLogin', '--command',
           'powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ' + encoded, '--raw']
(archive / 'command.json').write_text(json.dumps({
    'Command': command, 'PowerShellCommand': invocation, 'StartedUtc': stamp,
    'Script': str(script), 'ScriptSha256': hashlib.sha256(raw).hexdigest(),
    'SessionDirectory': str(session), 'ProductOperationPerformed': args.product_operation,
}, indent=2) + '\n', encoding='utf-8')
result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=55)
(archive / 'command.log').write_bytes(result.stdout)
(archive / 'completion.json').write_text(json.dumps({
    'FinishedUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'RunnerExitCode': result.returncode, 'ProductOperationPerformed': args.product_operation,
}, indent=2) + '\n', encoding='utf-8')
print(result.stdout.decode('utf-8-sig'), flush=True)
print(str(archive), flush=True)
result.check_returncode()
payload = json.loads(result.stdout.decode('utf-8-sig'))
if payload.get('ExitCode') != 0:
    raise RuntimeError('Guest diagnostic failed; command log and transcript retained')
