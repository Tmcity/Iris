"""Validate the complete Roslyn/XAML inventory against explicit reviewed exceptions and both resource sets."""
import argparse
import hashlib
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path
sys.stdout.reconfigure(encoding='utf-8')

parser = argparse.ArgumentParser()
parser.add_argument('--root', required=True)
parser.add_argument('--inventory', required=True)
parser.add_argument('--output', required=True)
args = parser.parse_args()
root = Path(args.root)
entries = json.loads(Path(args.inventory).read_text(encoding='utf-8-sig'))
exceptions = json.loads((root / 'build/localization/exceptions.json').read_text(encoding='utf-8'))
approved = {e['Fingerprint']: e for e in exceptions}
unapproved = [e for e in entries if e['Kind'] == 'resource-cache' or e['Fingerprint'] not in approved]
errors = []
resource_counts = {}
native_english = {'App:LanguageChineseNative': '\u7b80\u4f53\u4e2d\u6587'}

def read_resources(path):
    result = {}
    for data in ET.parse(path).getroot().findall('data'):
        key = data.attrib['name']
        if key in result:
            errors.append('Duplicate key: ' + str(path) + ':' + key)
        result[key] = data.findtext('value') or ''
    return result

def placeholders(value):
    return sorted(re.findall(r'(?<!\{)\{(\d+)(?:,[^}:]+)?(?::([^}]+))?\}(?!\})', value))

for project, folder, name in [('Iris.App', 'Resources', 'App'), ('Iris.Core', 'Localization', 'Core')]:
    directory = root / 'src' / project / folder
    zh = read_resources(directory / 'Strings.resx')
    en = read_resources(directory / 'Strings.en-US.resx')
    catalog = json.loads((root / 'build/localization' / (name.lower() + '-catalog.json')).read_text(encoding='utf-8'))
    if zh.keys() != en.keys() or zh.keys() != catalog.keys():
        errors.append(name + ': key sets differ')
    for key in zh.keys() & en.keys():
        if not zh[key].strip() or not en[key].strip():
            errors.append(name + ':' + key + ': empty resource')
        if placeholders(zh[key]) != placeholders(en[key]):
            errors.append(name + ':' + key + ': format parameters differ')
        if re.search('[\u3400-\u9fff]', en[key]) and native_english.get(name + ':' + key) != en[key]:
            errors.append(name + ':' + key + ': unexplained Chinese in English resource')
        if catalog.get(key) != {'zh-CN': zh[key], 'en-US': en[key]}:
            errors.append(name + ':' + key + ': resource differs from reviewed bilingual catalog')
    resource_counts[name] = len(zh)

result = {
    'sourceEntries': len(entries),
    'commentEntries': sum(e['Kind'] == 'comment' for e in entries),
    'reviewedNonUiEntries': sum(e['Kind'] != 'comment' and e['Fingerprint'] in approved for e in entries),
    'unapproved': unapproved,
    'resourceCounts': resource_counts,
    'resourceErrors': errors,
    'englishNativeNameExceptions': native_english,
    'exceptionManifestSha256': hashlib.sha256((root / 'build/localization/exceptions.json').read_bytes()).hexdigest(),
}
Path(args.output).write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps({k: v for k, v in result.items() if k not in {'unapproved'}}, ensure_ascii=False))
if unapproved:
    for entry in unapproved:
        print(entry['Path'], entry.get('Line', ''), entry['Kind'], repr(entry['Text']))
raise SystemExit(1 if unapproved or errors else 0)
