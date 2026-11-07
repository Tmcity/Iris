"""Prepare a read-only installer packet for the existing owned Sandbox."""
from pathlib import Path
import hashlib
import json
import shutil
import subprocess

ROOT = Path('F:/Iris')
SESSION = ROOT / 'artifacts/t040/manual-verification-20261107-3'
CANDIDATE = ROOT / 'artifacts/t040/r2r-local-candidate-20261108-1'
DEST = SESSION / 'r2r-candidate-input'
REVIEW = ROOT / 'tasks/T-040.evidence/r2r-local-candidate-review-20261108-1'
review = json.loads((REVIEW / 'review.json').read_text(encoding='utf-8-sig'))
state = json.loads((SESSION / 'session.json').read_text(encoding='utf-8-sig'))
listed = json.loads(subprocess.check_output(['wsb.exe', 'list', '--raw']).decode('utf-8-sig'))
assert state['SandboxId'] in {item['Id'] for item in listed['WindowsSandboxEnvironments']}
assert not review['PublicWritePerformed'] and review['PayloadFiles'] == 97
installer = CANDIDATE / 'Iris-0.2.11-Setup.exe'
assert hashlib.sha256(installer.read_bytes()).hexdigest() == review['InstallerSha256']
DEST.mkdir(exist_ok=False)
shutil.copy2(installer, DEST / installer.name)
shutil.copy2(CANDIDATE / 'payload-manifest.json', DEST / 'LOCAL_0211_R2R_payload-manifest.json')
for name in ('t040_save_r2r_candidate_installed_readonly.ps1', 't040_data_snapshot_readonly.ps1', 't040_collect_current_readonly.ps1'):
    shutil.copy2(ROOT / 'tasks' / name, DEST / name)
shutil.copytree(SESSION / 'cli-input/tools/state-snapshot', DEST / 'tools/state-snapshot')
manifest = [{'Path': file.relative_to(DEST).as_posix(), 'Bytes': file.stat().st_size,
             'Sha256': hashlib.sha256(file.read_bytes()).hexdigest()}
            for file in sorted(DEST.rglob('*')) if file.is_file()]
(DEST / 'input-manifest.json').write_text(json.dumps({'Files': manifest, 'InstallerExecuted': False}, indent=2) + '\n', encoding='utf-8')
for row in manifest:
    assert hashlib.sha256((DEST / row['Path']).read_bytes()).hexdigest() == row['Sha256']
command = ['wsb.exe', 'share', '--id', state['SandboxId'], '--host-path', str(DEST), '--sandbox-path', r'C:\IrisT040R2rCandidate', '--raw']
result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
(REVIEW / 'share-command.json').write_text(json.dumps({'Command': command, 'ReadOnly': True, 'InstallerExecuted': False, 'ExitCode': result.returncode}, indent=2) + '\n', encoding='utf-8')
(REVIEW / 'share-command.log').write_bytes(result.stdout)
result.check_returncode()
shutil.copy2(DEST / 'input-manifest.json', REVIEW / 'input-manifest.json')
print(json.dumps({'InputDirectory': str(DEST), 'GuestInput': r'C:\IrisT040R2rCandidate',
                  'FilesVerified': len(manifest), 'InstallerSha256': review['InstallerSha256'],
                  'InstallerExecuted': False, 'SandboxId': state['SandboxId']}, indent=2))
