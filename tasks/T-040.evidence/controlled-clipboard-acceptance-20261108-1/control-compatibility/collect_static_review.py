"""Read-only recheck of the previously reviewed official Windows control path.

This does not import the runtime, invoke a control method, start a helper, or
change a plugin.  It compares only the explicit source paths in the prior
binding and creates a separate dated audit record.
"""
from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json

ROOT = Path('F:/Iris')
PRIOR = ROOT / 'tasks/T-040.evidence/computer-control-rpc-compatibility-20261108-1'
OUT = ROOT / 'tasks/T-040.evidence/computer-control-hotkey-recheck-20261108-1'
review = json.loads((PRIOR / 'repair.json').read_text(encoding='utf-8-sig'))
wrapper = Path(review['ClientPath'])
wrapper_raw = wrapper.read_bytes()
rows = []
for bound in review['OfficialSourceBindings']:
    path = Path(bound['Path'])
    raw = path.read_bytes()
    actual = hashlib.sha256(raw).hexdigest()
    rows.append({
        'Path': str(path), 'PriorArchive': str(PRIOR / bound['ArchivePath']),
        'Bytes': len(raw), 'ActualSha256': actual,
        'PriorExpectedSha256': bound['Sha256'], 'MatchesPriorBinding': actual == bound['Sha256'],
    })
excerpts = []
base_path = Path(next(row['Path'] for row in rows if row['Path'].endswith('computer_use_client_base.js')))
base_text = base_path.read_text(encoding='utf-8')
for needle in ('this.press_key=', 'function E(', 'function j('):
    offset = base_text.index(needle)
    excerpts.append({'Path': str(base_path), 'Needle': needle, 'Offset': offset,
                     'Excerpt': base_text[max(0, offset - 60):offset + 520]})
facts = {
    'RecordedUtc': datetime.now(timezone.utc).isoformat(),
    'Scope': 'Read-only static control-path recheck; parent owns all actual input and recorder trials',
    'WrapperPath': str(wrapper), 'WrapperBytes': len(wrapper_raw),
    'WrapperSha256': hashlib.sha256(wrapper_raw).hexdigest(),
    'WrapperMatchesPriorRepair': hashlib.sha256(wrapper_raw).hexdigest() == review['AfterSha256'],
    'OfficialSourceBindings': rows,
    'All13OfficialFilesMatchPriorBindings': len(rows) == 13 and all(row['MatchesPriorBinding'] for row in rows),
    'OfficialClientKeyNormalization': 'Split plus-separated tokens, trim whitespace, drop empty tokens; identity mapper; no modifier rewrite',
    'CurrentActualHotkeySuccessProvenByChild': False,
    'NativeParserCauseEstablished': False,
    'ParentRecorderTrialPending': True,
    'ChildDesktopInputPerformed': False,
    'WrapperChanged': False, 'SdkChanged': False, 'SkillChanged': False,
    'ApprovalOrInterruptionChanged': False, 'HelperStarted': False,
    'ProductSourceChanged': False, 'ProductTestsOrPackageRun': False,
    'HostSettingsChanged': False, 'PublicWritePerformed': False,
    'SourceExcerpts': excerpts,
}
OUT.mkdir(parents=True, exist_ok=True)
dest = OUT / 'static-review.json'
if dest.exists():
    raise RuntimeError('Preserve existing audit; do not replace it')
dest.write_text(json.dumps(facts, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'Output': str(dest),
                  'WrapperMatches': facts['WrapperMatchesPriorRepair'],
                  'All13OfficialFilesMatch': facts['All13OfficialFilesMatchPriorBindings']}, indent=2))
