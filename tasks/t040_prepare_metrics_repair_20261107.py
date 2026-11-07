"""Prepare repaired metrics tools without replacing failure evidence or Sandbox."""
from pathlib import Path
import hashlib
import json
import shutil

ROOT = Path('F:/Iris')
SESSION = ROOT / 'artifacts/t040/manual-verification-20261107-3'
DEST = SESSION / 'repair-input'
ARCHIVE = ROOT / 'tasks/T-040.evidence/session3-diagnostic-repair-20261107-1'
OLD = SESSION / 'input'
for name in ('observe-process-metrics-v2-readonly.ps1', 'save-metrics-state-v2-readonly.ps1',
             'start-metrics-observer-v2.ps1', 't040_save_memory_candidate_installed_readonly.ps1'):
    assert (OLD / name).read_bytes() == (ARCHIVE / 'originals/input' / name).read_bytes()
DEST.mkdir(exist_ok=False)
names = ('t040_metrics_process_readonly.psm1', 'observe-process-metrics-v3-readonly.ps1',
         'start-metrics-observer-v3-readonly.ps1', 'save-metrics-state-v3-readonly.ps1',
         'validate-t040-metrics-v3-readonly.ps1')
for name in names:
    source = ROOT / 'tasks' / name
    raw = source.read_bytes()
    if any(b > 127 for b in raw) and not raw.startswith(b'\xef\xbb\xbf'):
        source.write_text(source.read_text(encoding='utf-8'), encoding='utf-8-sig')
    shutil.copyfile(source, DEST / name)
    shutil.copyfile(source, OLD / name)
for script in OLD.glob('*.ps1'):
    text = script.read_text(encoding='utf-8-sig')
    if "throw'" in text:
        script.write_text(text.replace("throw'", "throw '"), encoding='utf-8-sig')
candidate_save = OLD / 't040_save_memory_candidate_installed_readonly.ps1'
candidate_save.write_text(candidate_save.read_text(encoding='utf-8-sig').replace(
    'save-metrics-state-v2-readonly.ps1', 'save-metrics-state-v3-readonly.ps1'), encoding='utf-8-sig')
shutil.copyfile(ROOT / 'tasks/t040_begin_cold_install_diagnostics_manual.ps1', OLD / 't040_begin_cold_install_diagnostics_manual.ps1')
for name in ('LOCAL_0211_payload-manifest.json', 't040_collect_current_readonly.ps1',
             't040_save_memory_candidate_installed_readonly.ps1'):
    shutil.copyfile(OLD / name, DEST / name)
shutil.copytree(OLD / 'tools', DEST / 'tools')
manifest = []
for path in sorted(p for p in DEST.rglob('*') if p.is_file()):
    manifest.append({'Path': str(path.relative_to(DEST)), 'Bytes': path.stat().st_size,
                     'Sha256': hashlib.sha256(path.read_bytes()).hexdigest()})
(ARCHIVE / 'repair-input-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
(ARCHIVE / 'computer-use-setup-error.txt').write_text(
    "Package subpath './dist/project/cua/sky_js/src/targets/windows/internal/computer_use_client_base.js' "
    'is not defined by exports in C:\\Users\\28910\\AppData\\Local\\OpenAI\\Codex\\runtimes\\cua_node\\'
    'c4873bdffed0b168\\bin\\node_modules\\@oai\\sky\\package.json\n'
    'The same bootstrap error recurred after js_reset. No foreground input was issued.\n', encoding='utf-8')
print(json.dumps({'RepairInputDirectory': str(DEST), 'Files': len(manifest),
                  'FailureOriginalsPreserved': True, 'SandboxCreated': False, 'ProductOperationPerformed': False}, indent=2))
