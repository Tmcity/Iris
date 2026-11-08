"""Check frozen bytes in the actual archive commit without text conversions."""
from pathlib import Path
from io import BytesIO
import hashlib
import json
import subprocess

ROOT = Path('F:/Iris')
archive = ROOT / 'tasks/T-040.evidence/controlled-clipboard-acceptance-20261108-1'
binding = json.loads((archive / 'binding.json').read_text(encoding='utf-8'))
commit = subprocess.check_output(['git', 'rev-parse', '1b445b1^{commit}'], cwd=ROOT).decode().strip()
relative = archive.relative_to(ROOT).as_posix()
requests = ''.join(commit + ':' + relative + '/' + row['Path'] + '\n' for row in binding['Files'])
result = subprocess.run(['git', 'cat-file', '--batch'], cwd=ROOT, input=requests.encode('utf-8'), capture_output=True, check=True)
stream = BytesIO(result.stdout)
failures = []
for row in binding['Files']:
    header = stream.readline().split()
    assert len(header) == 3 and header[1] == b'blob', 'Missing committed archive blob'
    body = stream.read(int(header[2]))
    assert stream.read(1) == b'\n'
    if len(body) != row['Bytes'] or hashlib.sha256(body).hexdigest() != row['Sha256']:
        failures.append(row['Path'])
assert not stream.read(), 'Unexpected extra batch output'
record = {'Commit': commit, 'BoundEvidenceFiles': len(binding['Files']), 'AllCommittedBytesMatch': not failures,
          'Failures': failures, 'WholeT040Complete': False, 'PublicWritePerformed': False}
output = ROOT / 'tasks/T-040.evidence/clipboard-acceptance-index-20261108-1/commit-byte-check.json'
output.write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
print(json.dumps(record, indent=2))
assert not failures
