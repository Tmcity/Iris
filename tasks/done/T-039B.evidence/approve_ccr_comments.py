from pathlib import Path
import json

root = Path(__file__).resolve().parents[3]
path = root / 'build/localization/exceptions.json'
entries = json.loads(path.read_text(encoding='utf-8'))
report = json.loads((root / 'tasks/done/T-039B.evidence/localization-audit-1/audit-result.json').read_text(encoding='utf-8'))
hits = report['unapproved']
assert len(hits) == 2 and all(hit['Kind'] == 'comment' for hit in hits)
assert {hit['Path'] for hit in hits} == {'src/Iris.Core/Settings/SettingsEditingModels.cs', 'src/Iris.Providers/Http/ProviderHttp.cs'}
for hit in hits:
    assert hit['SiteFingerprint'] not in {entry['SiteFingerprint'] for entry in entries}
    entries.append({**hit, 'AllowedOccurrences': 1, 'Reason': 'T-039B / CCR-039 approved XML documentation of the update preference and credential-free Updates client; not executable UI.'})
path.write_text(json.dumps(entries, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print('Added exactly two reviewed CCR-039 XML documentation sites; no executable literal approved.')
