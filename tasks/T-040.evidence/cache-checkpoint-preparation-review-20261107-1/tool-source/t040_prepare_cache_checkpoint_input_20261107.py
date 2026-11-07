"""Publish immutable owned cache-stage collectors, without performing an Iris action."""
from pathlib import Path
import hashlib
import json

ROOT = Path('F:/Iris')
INPUT = ROOT / 'artifacts/t040/manual-verification-20261106-2/input'
files = []


def publish(name, raw):
    path = INPUT / name
    if path.exists():
        assert path.read_bytes() == raw, 'Do not replace an existing different input: ' + name
    else:
        path.open('xb').write(raw)
    files.append({'Path': name, 'Bytes': len(raw), 'Sha256': hashlib.sha256(raw).hexdigest()})


for name in ('t040_save_cache_checkpoint_readonly.ps1', 't040_collect_cache_baseline_readonly.ps1'):
    raw = (ROOT / 'tasks' / name).read_bytes()
    assert all(byte < 128 for byte in raw)
    publish(name, raw)
for name, phase in (
    ('Save-Cache-Prime-State.cmd', 'prime'),
    ('Save-Cache-Hit-State.cmd', 'cache'),
    ('Save-Card-Retry-State.cmd', 'retry')):
    lines = [
        '@echo off', 'setlocal',
        'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0t040_save_cache_checkpoint_readonly.ps1" -Phase ' + phase,
        'set "T040_CACHE_EVIDENCE_EXIT=%ERRORLEVEL%"',
        'echo.', 'echo Evidence exit code: %T040_CACHE_EVIDENCE_EXIT%',
        'pause', 'exit /b %T040_CACHE_EVIDENCE_EXIT%']
    publish(name, ('\r\n'.join(lines) + '\r\n').encode('ascii'))
for name in ('t040_save_feature_state_readonly.ps1', 'save-metrics-state-v2-readonly.ps1',
             't040_collect_current_readonly.ps1', 'LOCAL_0211_payload-manifest.json',
             'tools/state-snapshot/T040StateProbe.exe'):
    assert (INPUT / name).is_file(), 'Required existing input is missing: ' + name
candidate_manifest = ROOT / 'artifacts/t040/memory-fix-local-candidate-20261107-1/payload-manifest.json'
assert (INPUT / 'LOCAL_0211_payload-manifest.json').read_bytes() == candidate_manifest.read_bytes()
path = ROOT / 'artifacts/t040/cache-checkpoint-input-20261107-1.json'
record = {'Files': files, 'PublishedIntoExistingInputOnly': True,
          'ProductOperationPerformed': False, 'ManualCacheStageStarted': False}
if path.exists():
    assert json.loads(path.read_text(encoding='utf-8')) == record
else:
    path.open('x', encoding='utf-8').write(json.dumps(record, indent=2) + '\n')
print(json.dumps({'InputsVerified': len(files), 'IrisActionsPerformed': False, 'ManualCacheStageStarted': False}))
