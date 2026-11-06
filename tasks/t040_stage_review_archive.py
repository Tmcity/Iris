"""Stage explicit T-040 review files and verify that raw evidence retains exact bytes."""
from pathlib import Path
import hashlib
import json
import subprocess

ROOT=Path('F:/Iris')
names=[
    'tasks/current_task.md',
    'tasks/T-040.environment-choice-20261106.json',
    'tasks/T-040.manual-startup-20261106.md',
    'tasks/T-040.manual-session2-install.md',
    'tasks/T-040.manual-clean-idle-20261107.md',
    'tasks/T-040.manual-features-stage1-20261107.md',
    'tasks/T-040.progress-20261107.md',
    'tasks/start_t040_sandbox_20261106.py',
    'tasks/t040_correct_observer_encoding.py',
    'tasks/t040_run_guest_tool.py',
    'tasks/t040_collect_current_readonly.ps1',
    'tasks/t040_verify_installed_readonly.ps1',
    'tasks/t040_verify_installed_v2_readonly.ps1',
    'tasks/t040_verify_official_payload_readonly.ps1',
    'tasks/t040_save_clean_idle_readonly.ps1',
    'tasks/t040_validate_manual_input_readonly.ps1',
    'tasks/t040_collect_install_events_readonly.ps1',
    'tasks/t040_measure_msi_signature_readonly.ps1',
    'tasks/t040_start_signature_probe_readonly.ps1',
    'tasks/t040_query_msi_policy_readonly.ps1',
    'tasks/t040_start_policy_probe_readonly.ps1',
    'tasks/t040_archive_session2_review.py',
    'tasks/t040_start_synthetic_provider.ps1',
    'tasks/t040_probe_synthetic_http.ps1',
    'tasks/t040_start_counter_fixture_probe.ps1',
    'tasks/t040_save_feature_state_readonly.ps1',
    'tasks/t040_capture_feature_view_manual.ps1',
    'tasks/t040_archive_tool_preparation.py',
    'tasks/t040_stage_review_archive.py',
]
evidence=ROOT/'tasks/T-040.evidence'
names.extend(path.relative_to(ROOT).as_posix() for path in sorted(evidence.rglob('*')) if path.is_file())
names=sorted(set(names))
for name in names:
    path=(ROOT/name).resolve(strict=True)
    assert path.is_relative_to(ROOT/'tasks') and '.codex' not in path.parts
before=subprocess.run(['git','diff','--cached','--name-only'],cwd=ROOT,capture_output=True,check=True)
existing=set(before.stdout.decode('utf-8').splitlines())
if existing-set(names): raise RuntimeError('Unrelated staged changes exist; do not combine them')
command=['git','add','-f','--']+names
result=subprocess.run(command,cwd=ROOT,stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
(evidence/'stage-output-20261107-1.log').write_bytes(result.stdout)
result.check_returncode()
subprocess.run(['git','add','-f','--','tasks/T-040.evidence/stage-output-20261107-1.log'],cwd=ROOT,check=True)
checked=subprocess.run(['git','diff','--cached','--check'],cwd=ROOT,stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
if checked.returncode:
    (evidence/'stage-diff-check-failure-20261107-1.log').write_bytes(checked.stdout)
    raise RuntimeError('Index whitespace check failed; detailed output retained in evidence')
count=0
for name in names:
    if name.startswith('tasks/T-040.evidence/'):
        indexed=subprocess.run(['git','show',':'+name],cwd=ROOT,capture_output=True,check=True).stdout
        assert indexed==(ROOT/name).read_bytes(), 'Index does not match the frozen evidence bytes: '+name
        count+=1
audit={'StagedFiles':len(names),'RawEvidenceByteMatches':count,'ExcludedCodex':True,'ExcludedUnrelatedHistory':True,
       'ProductSourceChanged':False,'Paths':names}
(evidence/'index-audit-20261107-1.json').write_text(json.dumps(audit,indent=2)+'\n',encoding='utf-8')
subprocess.run(['git','add','-f','--','tasks/T-040.evidence/stage-output-20261107-1.log','tasks/T-040.evidence/index-audit-20261107-1.json'],cwd=ROOT,check=True)
print(json.dumps({k:v for k,v in audit.items() if k!='Paths'},indent=2))
