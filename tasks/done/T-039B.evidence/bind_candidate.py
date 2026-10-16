from pathlib import Path
import hashlib
import json
import shutil

e = Path(__file__).resolve().parent
root = e.parents[2]
dest = root / 'artifacts/t039b/review-final-candidate-1'
dest.mkdir(exist_ok=False)
files = []
for project, name in [('Iris.App', 'App'), ('Iris.Maintenance', 'Maintenance')]:
    source = root / 'src' / project / 'bin/Release/net10.0-windows10.0.19041.0'
    target = dest / name
    target.mkdir()
    for path in source.iterdir():
        if path.is_file():
            shutil.copyfile(path, target / path.name)
    if (source / 'en-US').exists():
        shutil.copytree(source / 'en-US', target / 'en-US')
    for path in target.rglob('*'):
        if path.is_file():
            files.append({'Path': str(path.relative_to(dest)), 'Sha256': hashlib.sha256(path.read_bytes()).hexdigest(), 'Bytes': path.stat().st_size})
sources = []
for path in (root / 'src').rglob('*'):
    if path.is_file() and path.suffix in {'.cs', '.csproj', '.resx', '.xaml'} and not {'bin', 'obj'} & set(path.parts):
        sources.append({'Path': str(path.relative_to(root)), 'Sha256': hashlib.sha256(path.read_bytes()).hexdigest()})
binding = {'ArtifactDirectory': str(dest), 'Files': files, 'Sources': sources,
           'Validation': {'BuildLog': 'final-release-1.log', 'FullLog': 'final-full-1.log'}}
(e / 'review-final-candidate-1.json').write_text(json.dumps(binding, indent=2), encoding='utf-8')
print(json.dumps({'artifactFiles': len(files), 'sourceFiles': len(sources), 'directory': str(dest)}))
