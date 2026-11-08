"""Stage exact clipboard acceptance paths and verify Git index bytes."""
from pathlib import Path
import hashlib
import json
import subprocess

ROOT = Path('F:/Iris')
ARCHIVE = ROOT / 'tasks/T-040.evidence/controlled-clipboard-acceptance-20261108-1'
REPORT = ROOT / 'tasks/T-040.evidence/clipboard-acceptance-index-20261108-1'
binding = json.loads((ARCHIVE / 'binding.json').read_text(encoding='utf-8'))
paths = [str((ARCHIVE / row['Path']).relative_to(ROOT)).replace('\\', '/') for row in binding['Files']]
paths.append(str((ARCHIVE / 'binding.json').relative_to(ROOT)).replace('\\', '/'))
tools = [
    't040_archive_clipboard_acceptance_20261108.py', 't040_stage_clipboard_acceptance_20261108.py',
    't040_run_current_guest_tool.py', 't040_clipboard_format_metadata_readonly.ps1',
    't040_keyboard_state_metadata_readonly.ps1', 't040_probe_selection_copy_state_readonly.ps1',
    't040_request_selection_cli_diagnostic.ps1', 't040_probe_hotkey_state_readonly.ps1',
    't040_modifier_delivery_worker_readonly.ps1', 't040_observe_modifier_delivery_readonly.ps1',
    't040_keyboard_delivery_worker_readonly.ps1', 't040_start_keyboard_delivery_observer_readonly.ps1',
    't040_foreground_identity_readonly.ps1', 't040_probe_native_password_style_readonly.ps1',
]
paths += ['tasks/' + name for name in tools]
paths += ['tasks/current_task.md', 'tasks/T-040.progress-20261108.md', 'tasks/T-040.completion-ledger-20261108.md']
paths = sorted(set(paths))
assert all(not path.startswith('.codex/') and Path(path).parts[0] == 'tasks' for path in paths)
existing = subprocess.check_output(['git', 'diff', '--name-only', '--cached', '-z'], cwd=ROOT).decode('utf-8').split('\0')
assert all(not path or path in paths for path in existing), 'Unrelated staged changes require independent handling'
stage = subprocess.run(['git', 'add', '-f', '--pathspec-from-file=-', '--pathspec-file-nul'],
                       input=('\0'.join(paths) + '\0').encode('utf-8'), cwd=ROOT, capture_output=True)
REPORT.mkdir(parents=True, exist_ok=True)
(REPORT / 'stage.stdout').write_bytes(stage.stdout)
(REPORT / 'stage.stderr').write_bytes(stage.stderr)
stage.check_returncode()
failures = []
for row in binding['Files']:
    relative = str((ARCHIVE / row['Path']).relative_to(ROOT)).replace('\\', '/')
    indexed = subprocess.check_output(['git', 'show', ':' + relative], cwd=ROOT)
    if len(indexed) != row['Bytes'] or hashlib.sha256(indexed).hexdigest() != row['Sha256']:
        failures.append(relative)
record = {'BoundEvidenceFiles': len(binding['Files']), 'StagedPaths': len(paths),
          'ExactEvidenceIndexBytesMatch': not failures, 'Failures': failures,
          'PublicWritePerformed': False, 'WholeT040Complete': False}
(REPORT / 'index-byte-check.json').write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
print(json.dumps(record, indent=2), flush=True)
assert not failures, 'Preserve original/index mismatch instead of changing evidence bytes'
