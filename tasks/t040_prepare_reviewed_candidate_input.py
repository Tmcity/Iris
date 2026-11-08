"""Map an explicitly reviewed local candidate into the existing owned session."""
from pathlib import Path
import argparse
import hashlib
import json
import re
import shutil
import subprocess

ROOT = Path('F:/Iris')
SESSION = ROOT / 'artifacts/t040/manual-verification-20261107-3'
parser = argparse.ArgumentParser()
parser.add_argument('--candidate', type=Path, required=True)
parser.add_argument('--review', type=Path, required=True)
parser.add_argument('--version', required=True)
parser.add_argument('--input-name', required=True)
parser.add_argument('--guest-alias', required=True)
args = parser.parse_args()
assert re.fullmatch(r'0\.2\.[0-9]+', args.version)
assert re.fullmatch(r'[a-z0-9-]+', args.input_name)
assert re.fullmatch(r'C:\\IrisT040[A-Za-z0-9]+', args.guest_alias)
package = args.candidate.resolve(strict=True)
review_path = args.review.resolve(strict=True)
assert package.is_relative_to(ROOT / 'artifacts/t040') and review_path.is_relative_to(ROOT / 'tasks/T-040.evidence')
review = json.loads(review_path.read_text(encoding='utf-8-sig'))
assert review['Version'] == args.version and review['PayloadFiles'] == 97 and not review['PublicWritePerformed']
installer = package / ('Iris-' + args.version + '-Setup.exe')
assert hashlib.sha256(installer.read_bytes()).hexdigest() == review['InstallerSha256']
session = json.loads((SESSION / 'session.json').read_text(encoding='utf-8-sig'))
listed = json.loads(subprocess.check_output(['wsb.exe', 'list', '--raw']).decode('utf-8-sig'))
assert session['SandboxId'] in {item['Id'] for item in listed['WindowsSandboxEnvironments']}
dest = SESSION / args.input_name
dest.mkdir(exist_ok=False)
for name in (installer.name, 'payload-manifest.json'):
    shutil.copy2(package / name, dest / name)
for name in ('t040_verify_controlled_candidate_installed_readonly.ps1', 't040_data_snapshot_readonly.ps1', 't040_collect_current_readonly.ps1'):
    shutil.copy2(ROOT / 'tasks' / name, dest / name)
shutil.copytree(SESSION / 'cli-input/tools/state-snapshot', dest / 'tools/state-snapshot')
identity = {'Version': args.version, 'SourceCommit': review['SourceCommit'],
            'ManifestSha256': hashlib.sha256((dest / 'payload-manifest.json').read_bytes()).hexdigest(),
            'InstallerSha256': review['InstallerSha256'], 'PublicWritePerformed': False}
(dest / 'candidate-identity.json').write_text(json.dumps(identity, indent=2) + '\n', encoding='utf-8')
subprocess.run(['wsb.exe', 'share', '--id', session['SandboxId'], '--host-path', str(dest), '--sandbox-path', args.guest_alias, '--raw'], check=True, capture_output=True)
print(json.dumps({'GuestInput': args.guest_alias, **identity}, indent=2))
