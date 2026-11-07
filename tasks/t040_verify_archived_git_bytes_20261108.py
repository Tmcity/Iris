from pathlib import Path
import hashlib
import json
import subprocess

folders = [Path('tasks/T-040.evidence/session3-diagnostic-repair-20261107-1'), Path('tasks/T-040.evidence/native-cli-review-20261108-1')]
errors = []
total = 0
for directory in folders:
    for row in json.loads((directory / 'binding.json').read_text(encoding='utf-8-sig'))['Files']:
        total += 1
        path = (directory / row['ArchivePath']).as_posix()
        raw = subprocess.check_output(['git', 'show', 'HEAD:' + path])
        if len(raw) != row['Bytes'] or hashlib.sha256(raw).hexdigest() != row['Sha256']:
            errors.append(path)
print(json.dumps({'GitArtifactsChecked': total, 'Mismatches': errors}, indent=2))
if errors:
    raise SystemExit(1)
