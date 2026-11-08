from pathlib import Path
import hashlib
import json
import shutil
import subprocess

ROOT = Path('F:/Iris')
SESSION = ROOT / 'artifacts/t040/manual-verification-20261107-3'
PACKAGE = ROOT / 'artifacts/t040/history-close-local-candidate-20261108-1'
DEST = SESSION / 'history-fixed-input'
review = json.loads((ROOT / 'tasks/T-040.evidence/history-close-defect-20261108-1/candidate-review.json').read_text(encoding='utf-8-sig'))
assert review['Version'] == '0.2.12' and review['PayloadFiles'] == 97
assert hashlib.sha256((PACKAGE / 'Iris-0.2.12-Setup.exe').read_bytes()).hexdigest() == review['InstallerSha256']
session = json.loads((SESSION / 'session.json').read_text(encoding='utf-8-sig'))
listed = json.loads(subprocess.check_output(['wsb.exe', 'list', '--raw']).decode('utf-8-sig'))
assert session['SandboxId'] in {item['Id'] for item in listed['WindowsSandboxEnvironments']}
DEST.mkdir(exist_ok=False)
for name in ('Iris-0.2.12-Setup.exe', 'payload-manifest.json'):
    shutil.copy2(PACKAGE / name, DEST / name)
for name in ('t040_verify_controlled_candidate_installed_readonly.ps1', 't040_data_snapshot_readonly.ps1', 't040_collect_current_readonly.ps1'):
    shutil.copy2(ROOT / 'tasks' / name, DEST / name)
shutil.copytree(SESSION / 'cli-input/tools/state-snapshot', DEST / 'tools/state-snapshot')
identity = {'Version': review['Version'], 'SourceCommit': review['SourceCommit'],
            'ManifestSha256': hashlib.sha256((DEST / 'payload-manifest.json').read_bytes()).hexdigest(),
            'InstallerSha256': review['InstallerSha256'], 'PublicWritePerformed': False}
(DEST / 'candidate-identity.json').write_text(json.dumps(identity, indent=2) + '\n', encoding='utf-8')
result = subprocess.run(['wsb.exe','share','--id',session['SandboxId'],'--host-path',str(DEST),'--sandbox-path',r'C:\IrisT040HistoryFixed','--raw'],capture_output=True)
result.check_returncode()
print(json.dumps({'GuestInput':r'C:\IrisT040HistoryFixed',**identity},indent=2))
