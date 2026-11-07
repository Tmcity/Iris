"""Build a fixture configurator against the exact local candidate assemblies."""
from pathlib import Path
import hashlib
import json
import shutil
import subprocess
from zipfile import ZipFile

ROOT = Path('F:/Iris')
PACKAGE = ROOT / 'artifacts/t040/memory-fix-local-candidate-20261107-1'
DEST = ROOT / 'artifacts/t040/toolsource/native-cli-fixture-20261107-1'
INPUT = ROOT / 'artifacts/t040/manual-verification-20261107-3/cli-input'
DEST.mkdir(parents=True, exist_ok=False)
INPUT.mkdir(exist_ok=False)
references = DEST / 'references'
references.mkdir()
manifest = {row['Path']: row for row in json.loads((PACKAGE / 'payload-manifest.json').read_text(encoding='utf-8-sig'))}
with ZipFile(PACKAGE / 'Iris-0.2.11-full.nupkg') as archive:
    for name in archive.namelist():
        if name.startswith('lib/app/') and '/' not in name.removeprefix('lib/app/') and name.endswith('.dll'):
            leaf = name.removeprefix('lib/app/')
            raw = archive.read(name)
            expected = manifest[leaf]
            assert len(raw) == expected['Bytes'] and hashlib.sha256(raw).hexdigest().lower() == expected['Sha256'].lower()
            (references / leaf).write_bytes(raw)
source = ROOT / 'tasks/T-040.evidence/native-cli-preparation-20261107-1/source'
for name in ('T040FixtureConfigure.csproj', 'Program.cs'):
    shutil.copyfile(source / name, DEST / name)
command = ['dotnet', 'publish', str(DEST / 'T040FixtureConfigure.csproj'), '-c', 'Release', '-r', 'win-x64',
           '--self-contained', 'false', '-o', str(INPUT / 'tools/fixture-configure'), '-v', 'minimal']
result = subprocess.run(command, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
(source.parent / 'publish.log').write_bytes(result.stdout)
print(result.stdout.decode('utf-8-sig', errors='replace'), flush=True)
result.check_returncode()
shutil.copytree(ROOT / 'artifacts/t040/manual-verification-20261106-2/input/tools/synthetic-provider', INPUT / 'tools/synthetic-provider')
shutil.copytree(ROOT / 'artifacts/t040/manual-verification-20261107-3/input/tools/state-snapshot', INPUT / 'tools/state-snapshot')
shutil.copyfile(PACKAGE / 'payload-manifest.json', INPUT / 'LOCAL_0211_payload-manifest.json')
print(str(INPUT), flush=True)
