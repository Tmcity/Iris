"""Stage only the explicitly reviewed T-040 memory continuation.

Keep unrelated historical untracked files and all .codex contents untouched.
"""
from pathlib import Path
import datetime
import hashlib
import json
import subprocess

ROOT = Path('F:/Iris')
EVIDENCE = ROOT / 'tasks/T-040.evidence'
AUDIT = EVIDENCE / 'memory-continuation-stage-review-20261107-1'
names = [
    'tasks/current_task.md',
    'tasks/T-040.progress-20261107.md',
    'tasks/T-040.memory-defect-20261107.md',
    'tasks/T-040.manual-local-memory-candidate-20261107.md',
    'tasks/T-040.manual-memory-candidate-ui-cycle-20261107.md',
    'tasks/T-040.manual-candidate-post-gc-idle-20261107.md',
    'tasks/T-040.manual-stage2a-cache-20261107.md',
    'tasks/T-040.manual-stage2-cache-retry-cancel-20261107.md',
    'tasks/t040_archive_memory_failure_and_fix.py',
    'tasks/t040_archive_memory_candidate_baseline_20261107.py',
    'tasks/t040_observe_heap_metadata_readonly.ps1',
    'tasks/t040_observe_jit_rundown_readonly.ps1',
    'tasks/t040_save_candidate_post_gc_steady_readonly.ps1',
    'tasks/t040_save_memory_candidate_installed_readonly.ps1',
    'tasks/t040_validate_memory_candidate_input_readonly.ps1',
    'tasks/t040_parse_memory_tools_ps51.ps1',
    'tasks/t040_stage_memory_continuation_20261107.py',
]
folders = [
    'memory-failure-fix-review-20261107-1',
    'memory-candidate-baseline-review-20261107-1',
    'guest-candidate-continuation-process-check-20261107T104250759276Z',
    'guest-candidate-continuation-visible-state-20261107T104308931635Z',
    'guest-candidate-ui-cycle-feedback-live-20261107T084433350041Z',
    'guest-closed-settings-root-path-20261107T070457938609Z',
    'guest-closed-settings-root-path-corrected-20261107T070934148626Z',
    'guest-closed-window-heap-metadata-20261107T065218366047Z',
    'guest-closed-window-reachable-heap-20261107T065832016681Z',
    'guest-formal-0210-data-baseline-before-local-candidate-20261107T082055819704Z',
    'guest-local-candidate-user-install-live-check-20261107T082914838718Z',
    'guest-local-memory-candidate-input-ready-20261107T082050897296Z',
    'guest-memory-candidate-gc-observer-20261107T083352733527Z',
    'guest-memory-candidate-observer-service-live-20261107T083357555050Z',
    'guest-memory-candidate-synthetic-service-20261107T083347514462Z',
    'guest-memory-investigation-service-state-20261107T070017302261Z',
    'guest-post-gc-save-feedback-live-check-20261107T062949945790Z',
    'guest-post-gc-steady-resident-map-20261107T063343780414Z',
    'guest-property-identifier-metadata-20261107T071906581745Z',
    'guest-retaining-property-name-20261107T072316410987Z',
    'guest-runtime-code-rundown-20261107T064102754434Z',
    'guest-runtime-code-rundown-typed-parser-20261107T064426069539Z',
    'guest-shared-context-menu-property-20261107T071538472986Z',
    'heap-before-root-graph-20261107-1',
    'jit-rundown-unparsed-20261107-1',
]
for folder in folders:
    path = EVIDENCE / folder
    assert path.is_dir()
    names.extend(item.relative_to(ROOT).as_posix() for item in sorted(path.rglob('*')) if item.is_file())
for name in names:
    path = (ROOT / name).resolve(strict=True)
    assert path.is_relative_to(ROOT / 'tasks') and '.codex' not in path.parts
assert not subprocess.run(['git', 'diff', '--cached', '--name-only'], cwd=ROOT, capture_output=True, check=True).stdout.strip()
parse_path = ROOT / 'artifacts/t040/candidate-continuation-ps51-parse-20261107-1.json'
parse_raw = parse_path.read_bytes()
parse_record = json.loads(parse_raw.decode('utf-8-sig'))
assert parse_record['PsVersion'].startswith('5.1.')
assert not parse_record['IrisExecuted'] and not parse_record['InstallersExecuted']
for row in parse_record['Scripts']:
    raw = (ROOT / 'tasks' / row['File']).read_bytes()
    assert row['ParserErrors'] == 0 and row['EncodingValid']
    assert hashlib.sha256(raw).hexdigest().upper() == row['Sha256'].upper()
AUDIT.mkdir(parents=True, exist_ok=False)
(AUDIT / 'powershell51-parse.json').open('xb').write(parse_raw)
names.append((AUDIT / 'powershell51-parse.json').relative_to(ROOT).as_posix())
names = sorted(set(names))
for start in range(0, len(names), 50):
    subprocess.run(['git', 'add', '-f', '--', *names[start:start + 50]], cwd=ROOT, check=True)
subprocess.run(['git', 'diff', '--cached', '--check'], cwd=ROOT, check=True)
selected = set(names)
indexed_names = set(subprocess.run(['git', 'diff', '--cached', '--name-only'], cwd=ROOT, capture_output=True, check=True).stdout.decode('utf-8').splitlines())
assert indexed_names <= selected and indexed_names
evidence_names = [name for name in names if name.startswith('tasks/T-040.evidence/')]
objects = subprocess.run(['git', 'cat-file', '--batch'], cwd=ROOT,
    input=('\n'.join(':' + name for name in evidence_names) + '\n').encode('utf-8'),
    capture_output=True, check=True).stdout
position = 0
byte_matches = []
for name in evidence_names:
    header_end = objects.index(b'\n', position)
    header = objects[position:header_end].decode('ascii').split()
    assert len(header) == 3 and header[1] == 'blob'
    length = int(header[2])
    raw = objects[header_end + 1:header_end + 1 + length]
    assert raw == (ROOT / name).read_bytes(), 'Raw evidence index bytes differ: ' + name
    byte_matches.append({'Path': name, 'Bytes': length, 'Sha256': hashlib.sha256(raw).hexdigest()})
    position = header_end + 1 + length + 1
assert position == len(objects)
audit = {
    'Utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'Task': 'T-040', 'SelectedFiles': len(names), 'ChangedFiles': len(indexed_names),
    'FrozenEvidenceFilesByteMatched': len(byte_matches), 'Evidence': byte_matches,
    'PsVersion': parse_record['PsVersion'], 'ParserErrors': 0,
    'ProductSourceChangedInThisStage': False, 'PublicWritesPerformed': False,
    'ExcludedCodexAndUnrelatedHistory': True, 'ClosedWindowMemoryAcceptanceStillPending': True,
    'SelectedPaths': names}
audit_path = AUDIT / 'index-audit.json'
audit_path.write_text(json.dumps(audit, indent=2) + '\n', encoding='utf-8')
subprocess.run(['git', 'add', '-f', '--', audit_path.relative_to(ROOT).as_posix()], cwd=ROOT, check=True)
assert subprocess.run(['git', 'show', ':' + audit_path.relative_to(ROOT).as_posix()], cwd=ROOT, capture_output=True, check=True).stdout == audit_path.read_bytes()
print(json.dumps({key: audit[key] for key in ('SelectedFiles', 'ChangedFiles', 'FrozenEvidenceFilesByteMatched', 'PsVersion', 'ParserErrors', 'ExcludedCodexAndUnrelatedHistory', 'ClosedWindowMemoryAcceptanceStillPending')}, indent=2))
