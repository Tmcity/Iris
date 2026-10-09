"""Compare delivered English values against the architect's actual reviewed snapshots."""
import hashlib
import json
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

sys.stdout.reconfigure(encoding='utf-8')
review = Path(__file__).parent
root = Path(sys.argv[1])
read = lambda name: json.loads((review / name).read_text(encoding='utf-8-sig'))
known = {'App': read('app-en-candidate-1.json'), 'Core': read('candidate-1-errors.en.json')}
generic = read('candidate-1-generic.en.json')
for item in read('candidate-1-generic-catalog.json'):
    known[item['Project']][item['Key']] = item.get('English') or generic[item['Id']]
supplemental = read('supplemental-reviewed.json')
for project, entries in supplemental.items():
    for key, values in entries.items():
        known[project][key] = values['en-US']

result = {}
for project, folder in [('App', 'Resources'), ('Core', 'Localization')]:
    path = root / 'src' / ('Iris.' + project) / folder
    source = path / 'Strings.en-US.resx'
    actual = {e.attrib['name']: e.findtext('value') for e in ET.parse(source).getroot().findall('data')}
    zh = {e.attrib['name']: e.findtext('value') for e in ET.parse(path / 'Strings.resx').getroot().findall('data')}
    result[project] = {
        'SourceSha256': hashlib.sha256(source.read_bytes()).hexdigest(),
        'ActualKeys': len(actual),
        'ReviewedUnchanged': sum(known[project].get(k) == v for k, v in actual.items()),
        'UnreviewedKeys': sorted(set(actual) - set(known[project])),
        'ChangedKeys': sorted(k for k, v in actual.items() if k in known[project] and known[project][k] != v),
        'MissingKeys': sorted(set(known[project]) - set(actual)),
        'ChangedSupplementalChinese': sorted(k for k, values in supplemental[project].items() if zh.get(k) != values['zh-CN']),
    }
print(json.dumps(result, ensure_ascii=False, indent=2))
raise SystemExit(1 if any(entry[field] for entry in result.values()
                         for field in ('UnreviewedKeys', 'ChangedKeys', 'MissingKeys', 'ChangedSupplementalChinese')) else 0)
