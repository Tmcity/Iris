"""Freeze the reviewed local trial package evidence and its pre-install baseline."""
from pathlib import Path
import hashlib
import json
import shutil

ROOT = Path('F:/Iris')
SESSION = ROOT / 'artifacts/t040/manual-verification-20261107-3'
CANDIDATE = ROOT / 'artifacts/t040/r2r-local-candidate-20261108-1'
REVIEW = ROOT / 'tasks/T-040.evidence/r2r-local-candidate-review-20261108-1'

def copy(source, destination):
    destination.parent.mkdir(parents=True, exist_ok=True)
    if source.is_dir():
        shutil.copytree(source, destination)
    else:
        shutil.copy2(source, destination)

copy(ROOT / 'artifacts/t040/r2r-validation-20261108-1', REVIEW / 'validation')
for file in (ROOT / 'artifacts/t040/r2r-package-logs-20261108-1').glob('package.*'):
    copy(file, REVIEW / 'package-execution' / file.name)
artifacts = []
for file in sorted(CANDIDATE.iterdir()):
    artifacts.append({'LocalPath': str(file), 'Bytes': file.stat().st_size, 'Sha256': hashlib.sha256(file.read_bytes()).hexdigest()})
    if file.suffix.lower() not in ('.exe', '.nupkg'):
        copy(file, REVIEW / 'candidate-metadata' / file.name)
(REVIEW / 'artifact-references.json').write_text(json.dumps(artifacts, indent=2) + '\n', encoding='utf-8')
baseline = sorted((SESSION / 'output').glob('feature-data-*'))[-1]
copy(baseline, REVIEW / 'before-install' / baseline.name)
state = json.loads((baseline / 'state.json').read_text(encoding='utf-8-sig'))
assert state['SqliteReadOnly'] and state['SecretsDecrypted'] is False and state['HistoryTotal'] == 5 and state['SqliteFailureCode'] is None
assert all(row['source_text'].startswith('Iris T040 ') for row in state['History'])
input_check = sorted((SESSION / 'output').glob('r2r-candidate-input-check-*.json'))[-1]
check = json.loads(input_check.read_text(encoding='utf-8-sig'))
assert check['AllMatch'] and check['FileCount'] == 16 and not check['InstallerExecuted']
copy(input_check, REVIEW / 'mapped-input' / input_check.name)
for name in ('t040_verify_r2r_candidate_20261108.py', 't040_prepare_r2r_candidate_session_20261108.py',
             't040_data_snapshot_readonly.ps1', 't040_save_r2r_candidate_installed_readonly.ps1',
             't040_verify_r2r_input_readonly.ps1', 't040_archive_r2r_candidate_review_20261108.py',
             'T-040.manual-r2r-candidate-20261108.md'):
    copy(ROOT / 'tasks' / name, REVIEW / 'tool-source' / name)
for name in ('pack.ps1', 'release_pipeline.py'):
    copy(ROOT / 'build' / name, REVIEW / 'build-source' / name)
files = [{'Path': file.relative_to(REVIEW).as_posix(), 'Bytes': file.stat().st_size,
          'Sha256': hashlib.sha256(file.read_bytes()).hexdigest()}
         for file in sorted(REVIEW.rglob('*')) if file.is_file() and file.name != 'binding.json']
(REVIEW / 'binding.json').write_text(json.dumps({'Files': files, 'BinaryArtifactsRemainAtReferencedLocalPaths': True,
                                               'PublicWritePerformed': False, 'FormalNfrResult': False}, indent=2) + '\n', encoding='utf-8')
for file in files:
    raw = (REVIEW / file['Path']).read_bytes()
    assert len(raw) == file['Bytes'] and hashlib.sha256(raw).hexdigest() == file['Sha256']
print(json.dumps({'BoundFiles': len(files), 'ArtifactReferences': len(artifacts), 'PreInstallHistoryEntries': 5,
                  'GuestInputFilesVerified': 16, 'PublicWritePerformed': False, 'FormalNfrResult': False}, indent=2))
