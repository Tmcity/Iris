"""Correct the audit's file count while preserving the first report bytes."""
from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json

OUT = Path('F:/Iris/tasks/T-040.evidence/computer-control-hotkey-recheck-20261108-1')
prior = OUT / 'static-review.json'
facts = json.loads(prior.read_text(encoding='utf-8'))
rows = facts['OfficialSourceBindings']
for row in rows:
    actual = hashlib.sha256(Path(row['Path']).read_bytes()).hexdigest()
    if actual != row['ActualSha256']:
        raise RuntimeError('Source changed after inspection; preserve both observations')
if hashlib.sha256(Path(facts['WrapperPath']).read_bytes()).hexdigest() != facts['WrapperSha256']:
    raise RuntimeError('Wrapper changed after inspection')
facts.pop('All13OfficialFilesMatchPriorBindings')
facts['CorrectionRecordedUtc'] = datetime.now(timezone.utc).isoformat()
facts['CorrectsRecord'] = str(prior)
facts['PriorRecordPreserved'] = True
facts['CountCorrection'] = 'OfficialSourceBindings contains 12 files; wrapper makes 13 total. Original audit incorrectly required 13 official binding rows.'
facts['OfficialBindingFileCount'] = len(rows)
facts['TotalFileCountIncludingWrapper'] = len(rows) + 1
facts['AllOfficialFilesMatchPriorBindings'] = all(row['MatchesPriorBinding'] for row in rows)
facts['ParentReportedCurrentRecorderTrial'] = {'Requested': 'Ctrl+Alt+d', 'ActualRecorderDisplay': 'Ctrl+D',
                                            'ReportedBy': 'root', 'ChildDidNotPerformOrObserveInput': True,
                                            'OriginalDraftMustBeCancelled': True}
facts['ParentRecorderTrialPending'] = False
dest = OUT / 'static-review-corrected-count.json'
if dest.exists():
    raise RuntimeError('Do not replace a completed audit')
dest.write_text(json.dumps(facts, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'Output': str(dest), 'OfficialFiles': len(rows),
                  'FilesIncludingWrapper': len(rows) + 1,
                  'AllMatched': facts['AllOfficialFilesMatchPriorBindings'] and facts['WrapperMatchesPriorRepair']}, indent=2))
