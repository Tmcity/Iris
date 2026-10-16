from pathlib import Path
import hashlib, json, shutil
e = Path(__file__).resolve().parent
root = e.parents[2]
out = root / 'artifacts/t039b/sandbox/69e00710-ca7b-4fb0-83a7-6ceaa37781c2/output'
for source, target in [('update-final-1','guest-update-final-1'),('committed-failure-final-1','guest-committed-failure-final-1'),('remaining-final-1','guest-remaining-final-1'),('coverage-final-2','guest-coverage-final-2')]:
    destination = e / target / 'output'; destination.mkdir(exist_ok=True)
    for path in (out / source).rglob('*'):
        if path.is_file() and path.suffix in {'.json','.log','.png','.ps1'}:
            copy = destination / path.relative_to(out / source); copy.parent.mkdir(parents=True, exist_ok=True); shutil.copyfile(path, copy)
for path in out.iterdir():
    if path.is_file() and (path.name.startswith('complete-') or path.name.startswith('final-') or path.name.startswith('continued-')):
        shutil.copyfile(path, e / 'guest-update-final-1' / path.name)
state = json.loads((out / 'final-state.json').read_text(encoding='utf-8-sig'))
manifest = json.loads((root / 'artifacts/t039b/release-0.2.1-final-3/payload-manifest.json').read_text(encoding='utf-8-sig'))
expected = {entry['Path'].replace('\\','/'): entry['Sha256'] for entry in manifest}
assert state['Processes'] == [] and state['Identity']['Version'] == '0.2.1'
assert all(expected[path.replace('\\','/')] == sha for path,sha in state['InstalledHashes'].items())
binding = {'FinalSandbox': '69e00710-ca7b-4fb0-83a7-6ceaa37781c2', 'InstalledFilesMatchActualPackage': True, 'InstalledHashes': state['InstalledHashes'],
           'ImageFiles': [{'Path': str(path.relative_to(e)), 'Sha256': hashlib.sha256(path.read_bytes()).hexdigest()} for directory in ['guest-update-final-1','guest-committed-failure-final-1','guest-remaining-final-1','guest-coverage-final-2'] for path in (e / directory).rglob('*.png')]}
(e / 'final-guest-binding.json').write_text(json.dumps(binding, indent=2), encoding='utf-8')
print(json.dumps({'InstalledFilesMatch': len(state['InstalledHashes']), 'BoundImages': len(binding['ImageFiles'])}))
