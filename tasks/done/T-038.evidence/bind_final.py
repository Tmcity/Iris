"""Bind the actual final source, compiled validation inputs, evidence and visual review."""
import hashlib
import json
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[3]
evidence = root / 'tasks/done/T-038.evidence'

def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def git(*args, **kwargs):
    return subprocess.check_output(['git', *args], cwd=root, **kwargs).decode('utf-8').strip()

paths = sorted(set(git('ls-files', '-c', '-o', '--exclude-standard', '--', 'src', 'tests', 'build', 'Directory.Build.props', 'Directory.Packages.props', 'global.json', 'Iris.slnx', 'specs/CONTRACTS.cs', 'specs/LOCALIZATION.md').splitlines()))
paths = [p for p in paths if not {'bin', 'obj', '__pycache__'} & set(Path(p).parts)]
blobs = git('hash-object', '--stdin-paths', input=('\n'.join(paths) + '\n').encode()).splitlines()
sources = [{'Path': p, 'Sha256': digest(root / p), 'GitBlob': b} for p, b in zip(paths, blobs, strict=True)]
dlls = []
for top in ['src', 'tests', 'build/LocalizationAudit']:
    for path in (root / top).rglob('*.dll'):
        if 'bin' in path.parts and 'Release' in path.parts and (path.name.startswith('Iris.') or path.name == 'LocalizationAudit.dll'):
            dlls.append({'Path': path.relative_to(root).as_posix(), 'Sha256': digest(path)})
visual_dir = evidence / 'final-full-visual-3'
images = json.loads((visual_dir / 'manifest.json').read_text(encoding='utf-8-sig'))
carry = json.loads((evidence / 'final-delivery-review/carry.json').read_text(encoding='utf-8'))
assert len(carry) == len(images)
for entry in images:
    assert digest(visual_dir / entry['File']).upper() == entry['Sha256']
    assert any(c['File'] == entry['File'] and c['Sha256'] == entry['Sha256'] for c in carry)
received = json.loads((evidence / 'received-sha256.json').read_text(encoding='utf-8-sig'))
preserved = [p for p in received if p.startswith('tasks/done/T-037.review/')]
for p in preserved:
    assert digest(root / p) == received[p], p
artifact_dirs = ['tasks/done/T-037.review', 'tasks/done/T-038.review', 'tasks/done/T-038.evidence']
artifacts = []
for directory in artifact_dirs:
    for path in (root / directory).rglob('*'):
        if path.is_file() and not {'bin', 'obj', '__pycache__'} & set(path.parts) and path.name != 'final-binding.json':
            artifacts.append({'Path': path.relative_to(root).as_posix(), 'Sha256': digest(path)})
result = {
    'BaseCommit': git('rev-parse', 'HEAD'),
    'Scope': 'T-038 developer delivery; independent architect acceptance pending',
    'SourceInputs': sources,
    'ValidationAssemblies': sorted(dlls, key=lambda e: e['Path']),
    'Evidence': sorted(artifacts, key=lambda e: e['Path']),
    'VisualDelivery': 'tasks/done/T-038.evidence/final-full-visual-3/manifest.json',
    'VisualReview': 'tasks/done/T-038.evidence/final-delivery-review/carry.json',
    'VisualImages': len(images),
    'AllFinalImagesMatchPreviouslyViewedSha': True,
    'ReceivedT037ReviewFilesUnchanged': len(preserved),
    'Note': 'Assemblies are the pre-commit Release/full validation artifacts. Rebuilding after commit changes informational version; compare source Git blobs separately.'
}
(evidence / 'final-binding.json').write_text(json.dumps(result, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
print(json.dumps({'sourceInputs': len(sources), 'validationAssemblies': len(dlls), 'evidenceFiles': len(artifacts), 'finalImages': len(images), 'receivedT037ReviewFilesUnchanged': len(preserved)}))
