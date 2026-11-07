"""Stage only this continuation and audit exact archived bytes."""
from pathlib import Path
import hashlib
import json
import subprocess

ROOT = Path('F:/Iris')
EVIDENCE = ROOT / 'tasks/T-040.evidence'
names = ['tasks/current_task.md', 'tasks/T-040.progress-20261108.md', 'tasks/T-040.manual-r2r-candidate-20261108.md']
tools = (
    't040_compare_diagnostic_r2r.ps1', 't040_snapshot_diagnostic_repeat_readonly.ps1',
    't040_review_r2r_repeat_20261108.py', 't040_compare_r2r_with_cpu.ps1', 't040_start_r2r_cpu_comparison.ps1',
    't040_capture_stalled_cpu_comparison_readonly.ps1', 't040_read_metrics_worker_stack.ps1',
    't040_recover_runaway_metrics_worker.ps1', 't040_json_line_repro.ps1',
    't040_validate_plain_log_json_ps51.ps1', 't040_validate_shared_log_reader_ps51.ps1',
    't040_recover_completed_cpu_comparison.ps1', 't040_verify_completed_r2r_cpu_readonly.ps1',
    't040_archive_r2r_cpu_and_sampler_repair_20261108.py', 't040_verify_r2r_candidate_20261108.py',
    't040_prepare_r2r_candidate_session_20261108.py', 't040_data_snapshot_readonly.ps1',
    't040_save_r2r_candidate_installed_readonly.ps1', 't040_verify_r2r_input_readonly.ps1',
    't040_archive_r2r_candidate_review_20261108.py', 't040_stage_r2r_continuation_20261108.py',
)
names += ['tasks/' + name for name in tools]
folders = [EVIDENCE / name for name in ('r2r-repeat-review-20261108-1', 'cpu-json-stall-repair-20261108-1',
           'r2r-cpu-comparison-review-20261108-1', 'r2r-local-candidate-review-20261108-1', 'computer-control-recheck-20261108-1')]
labels = (
    'r2r-repeat-current-state', 'restore-after-r2r-repeat', 'restored-repeat-installed-state',
    'start-r2r-cpu-comparison', 'cpu-comparison-process-audit', 'stalled-cpu-comparison-snapshot',
    'stalled-metrics-worker-stack', 'recover-runaway-metrics-worker', 'plain-log-json-ps51-validation',
    'shared-log-reader-ps51-validation', 'restore-after-log-sharing-diagnostic',
    'start-r2r-plain-cpu-comparison', 'start-r2r-shared-plain-cpu-comparison',
    'r2r-cpu-independent-terminal-state', 'before-r2r-candidate-data', 'r2r-candidate-mapped-byte-check',
)
for label in labels:
    folders += list(EVIDENCE.glob('guest-' + label + '-*'))
for folder in folders:
    names += [file.relative_to(ROOT).as_posix() for file in folder.rglob('*') if file.is_file()]
names = sorted(set(names))
for name in names:
    file = (ROOT / name).resolve(strict=True)
    assert file.is_relative_to(ROOT / 'tasks') and '.codex' not in file.parts
existing = subprocess.check_output(['git', 'diff', '--cached', '--name-only'], cwd=ROOT).decode().splitlines()
assert not set(existing) - set(names), 'Unrelated staged changes are present'
subprocess.run(['git', 'add', '-f', '--', *names], cwd=ROOT, check=True)
subprocess.run(['git', 'diff', '--cached', '--check'], cwd=ROOT, check=True)
raw_names = [name for name in names if name.startswith('tasks/T-040.evidence/')]
for name in raw_names:
    assert subprocess.check_output(['git', 'show', ':' + name], cwd=ROOT) == (ROOT / name).read_bytes(), name
audit = {'StagedFiles': len(names), 'RawEvidenceByteMatches': len(raw_names), 'SourceCandidateCommit': '90822806f8840c3272dd27f3b2f14abf0f57f2b9',
         'ExcludedCodex': True, 'ExcludedUnrelatedHistory': True, 'PublicWritePerformed': False,
         'Paths': names}
audit_file = EVIDENCE / 'r2r-local-candidate-review-20261108-1/index-audit.json'
audit_file.write_text(json.dumps(audit, indent=2) + '\n', encoding='utf-8')
subprocess.run(['git', 'add', '-f', '--', str(audit_file)], cwd=ROOT, check=True)
assert subprocess.check_output(['git', 'show', ':' + audit_file.relative_to(ROOT).as_posix()], cwd=ROOT) == audit_file.read_bytes()
print(json.dumps({key: value for key, value in audit.items() if key != 'Paths'}, indent=2))
