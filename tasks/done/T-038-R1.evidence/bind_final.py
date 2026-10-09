from pathlib import Path
import hashlib
import json
import subprocess

root = Path(__file__).resolve().parents[3]
evidence = root / 'tasks/done/T-038-R1.evidence'

def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def git(*args, **kwargs):
    return subprocess.check_output(['git', *args], cwd=root, **kwargs).decode('utf-8').strip()

parent = json.loads((root / 'tasks/done/T-038.evidence/final-binding.json').read_text(encoding='utf-8'))
paths = [x['Path'] for x in parent['SourceInputs']]
blobs = git('hash-object', '--stdin-paths', input=('\n'.join(paths) + '\n').encode()).splitlines()
sources = [{'Path': p, 'Sha256': digest(root / p), 'GitBlob': b} for p, b in zip(paths, blobs, strict=True)]
old_blobs = {x['Path']: x['GitBlob'] for x in parent['SourceInputs']}
changed = [x['Path'] for x in sources if x['GitBlob'] != old_blobs[x['Path']]]
assert sorted(changed) == ['tests/Iris.App.Tests/Localization/LocalizationTests.cs', 'tests/Iris.App.Tests/Settings/NetworkSettingsUiScope.cs'], changed
received = json.loads((evidence / 'scope-and-assertions.json').read_text(encoding='utf-8'))['ReviewInputs']
for x in received:
    assert digest(root / x['Path']) == x['Sha256'], x['Path']
artifacts = []
for folder in ['tasks/done/T-038.review', 'tasks/done/T-038-R1.review', 'tasks/done/T-038-R1.evidence']:
    for path in (root / folder).rglob('*'):
        if path.is_file() and not {'bin', 'obj', '__pycache__'} & set(path.parts) and path != evidence / 'final-binding.json':
            artifacts.append({'Path': path.relative_to(root).as_posix(), 'Sha256': digest(path)})
assemblies = []
for folder in ['src', 'tests']:
    for path in (root / folder).rglob('Iris.*.dll'):
        if 'bin' in path.parts and 'Release' in path.parts:
            assemblies.append({'Path': path.relative_to(root).as_posix(), 'Sha256': digest(path)})
result = {
    'BaseCommit': git('rev-parse', 'HEAD'),
    'SourceInputs': sources,
    'ChangedImplementationPaths': changed,
    'ProductionContractResourceLayoutPackageDiff': [],
    'ValidationAssemblies': assemblies,
    'Evidence': artifacts,
    'ReceivedReviewInputsUnchanged': len(received),
    'VisualComparison': json.loads((evidence / 'visual-comparison.json').read_text(encoding='utf-8')),
    'Note': 'R1 developer delivery pending independent review. Assemblies are the actual pre-commit validation inputs; post-commit rebuild informational versions can differ.'
}
(evidence / 'final-binding.json').write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'SourceInputs': len(sources), 'ChangedImplementationPaths': changed, 'EvidenceFiles': len(artifacts), 'ReceivedReviewInputsUnchanged': len(received)}))
