"""Re-run the real scanner on owned source copies: baseline, borrowed log text in UI, duplicate site."""
import argparse
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys

sys.stdout.reconfigure(encoding='utf-8')
parser = argparse.ArgumentParser()
parser.add_argument('--root', required=True)
parser.add_argument('--output', required=True)
args = parser.parse_args()
root = Path(args.root).resolve()
output = Path(args.output).resolve()
output.mkdir(parents=True, exist_ok=True)
source = (root / 'src/Iris.App/App.xaml.cs').read_text(encoding='utf-8-sig')
entries = json.loads((root / 'build/localization/exceptions.json').read_text(encoding='utf-8'))
target = next(e for e in entries if e['Path'] == 'src/Iris.App/App.xaml.cs' and 'LogExited(' in e.get('Owner', ''))
text = json.dumps(target['Text'], ensure_ascii=False)
insertion = '\n    private static void AuditUiMutation()\n    {\n        _ = new System.Windows.Controls.TextBlock { Text = ' + text + ' };\n    }\n'
mutant = source.rsplit('}', 1)[0] + insertion + '}\n'
attribute = next(line for line in source.splitlines() if target['Text'] in line)
duplicate = source.replace(attribute, attribute + '\n' + attribute, 1)
scanner = root / 'build/LocalizationAudit/bin/Release/net10.0/LocalizationAudit.dll'
results = []
for name, contents in [('baseline', source), ('ui-mutation', mutant), ('duplicate-site', duplicate)]:
    case = output / name
    (case / 'src/Iris.App').mkdir(parents=True, exist_ok=True)
    (case / 'src/Iris.App/App.xaml.cs').write_text(contents, encoding='utf-8')
    for project, folder in [('Iris.App', 'Resources'), ('Iris.Core', 'Localization')]:
        destination = case / 'src' / project / folder
        destination.mkdir(parents=True, exist_ok=True)
        for filename in ['Strings.resx', 'Strings.en-US.resx']:
            shutil.copyfile(root / 'src' / project / folder / filename, destination / filename)
    (case / 'build/localization').mkdir(parents=True, exist_ok=True)
    for filename in ['exceptions.json', 'app-catalog.json', 'core-catalog.json']:
        shutil.copyfile(root / 'build/localization' / filename, case / 'build/localization' / filename)
    inventory = case / 'inventory.json'
    scan = subprocess.run(['dotnet', str(scanner), str(case), str(inventory)], capture_output=True)
    (case / 'inventory.log').write_bytes(scan.stdout + scan.stderr)
    if scan.returncode:
        raise RuntimeError('Actual scanner failed: ' + name)
    checked = subprocess.run([sys.executable, str(root / 'build/localization/audit.py'), '--root', str(case), '--inventory', str(inventory), '--output', str(case / 'result.json')], capture_output=True)
    (case / 'audit.log').write_bytes(checked.stdout + checked.stderr)
    result = json.loads((case / 'result.json').read_text(encoding='utf-8'))
    results.append({'case': name, 'inventoryEntries': result['sourceEntries'], 'auditExit': checked.returncode, 'unapproved': len(result['unapproved'])})
    if (name == 'baseline' and checked.returncode != 0) or (name != 'baseline' and checked.returncode == 0):
        raise RuntimeError('Audit regression: ' + name)
(output / 'summary.json').write_text(json.dumps(results, indent=2), encoding='utf-8')
print(json.dumps(results))
