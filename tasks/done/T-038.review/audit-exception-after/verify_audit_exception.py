"""Rebuild the submitted scanner in an owned copy and repeat the original UI mutation."""
import hashlib
import json
import shutil
import subprocess
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding='utf-8')
root = Path(r'F:\Iris')
destination = Path(sys.argv[1])
destination.mkdir(parents=True, exist_ok=False)
tree = destination / 'tree'
tool = destination / 'tool' / 'LocalizationAudit'
tool.mkdir(parents=True)
feed = destination / 'empty-local-feed'
feed.mkdir()
inputs = {}

def copy(relative, target):
    source = root / relative
    data = source.read_bytes()
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(data)
    inputs[relative] = hashlib.sha256(data).hexdigest()

for relative in ['Directory.Build.props', 'global.json', '.editorconfig']:
    copy(relative, destination / 'tool' / relative)
for name in ['Program.cs', 'Migration.cs', 'LocalizationAudit.csproj']:
    relative = 'build/LocalizationAudit/' + name
    copy(relative, tool / name)
for relative in ['src/Iris.App/App.xaml.cs', 'src/Iris.App/Resources/Strings.resx',
                 'src/Iris.App/Resources/Strings.en-US.resx', 'src/Iris.Core/Localization/Strings.resx',
                 'src/Iris.Core/Localization/Strings.en-US.resx', 'build/localization/audit.py',
                 'build/localization/exceptions.json', 'build/localization/app-catalog.json',
                 'build/localization/core-catalog.json']:
    copy(relative, tree / relative)
(destination / 'inputs.json').write_text(json.dumps(inputs, indent=2), encoding='utf-8')

def run(name, command):
    result = subprocess.run(command, cwd=destination, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    (destination / (name + '.log')).write_bytes(result.stdout)
    print(name + ' exit=' + str(result.returncode))
    print(result.stdout.decode('utf-8', errors='replace')[-2800:])
    return result.returncode

build = run('scanner-build', ['dotnet', 'build', str(tool / 'LocalizationAudit.csproj'), '-c', 'Release',
                              '-p:RestoreSources=' + str(feed)])
if build:
    raise SystemExit(build)
dll = tool / 'bin/Release/net10.0/LocalizationAudit.dll'
summary = []
source = tree / 'src/Iris.App/App.xaml.cs'
baseline = source.read_text(encoding='utf-8-sig')
historical = (root / 'tasks/done/T-038.review/audit-exception-before/tree/src/Iris.App/App.xaml.cs').read_text(encoding='utf-8-sig')
mutation = historical[historical.index('\ninternal static class ReviewLocalizationLeak'):]
for name, code, expected in [('baseline', baseline, 0), ('ui-mutation', baseline.rstrip() + '\n' + mutation, 1)]:
    source.write_text(code, encoding='utf-8')
    (destination / (name + '-source.cs')).write_text(code, encoding='utf-8')
    inventory = destination / (name + '-inventory.json')
    if run(name + '-inventory', ['dotnet', str(dll), str(tree), str(inventory)]):
        raise SystemExit(2)
    result_path = destination / (name + '-result.json')
    code = run(name + '-audit', [sys.executable, str(tree / 'build/localization/audit.py'), '--root', str(tree),
                                '--inventory', str(inventory), '--output', str(result_path)])
    report = json.loads(result_path.read_text(encoding='utf-8'))
    summary.append({'Case': name, 'InventoryEntries': report['sourceEntries'], 'AuditExit': code,
                    'ExpectedExit': expected, 'Unapproved': len(report['unapproved']), 'ResourceErrors': report['resourceErrors']})

changed = [relative for relative, digest in inputs.items() if hashlib.sha256((root / relative).read_bytes()).hexdigest() != digest]
result = {'Cases': summary, 'ScannerDllSha256': hashlib.sha256(dll.read_bytes()).hexdigest(),
          'ProductInputsChangedDuringRun': changed,
          'OriginalMutationSourceSha256': hashlib.sha256(historical.encode('utf-8')).hexdigest(),
          'Scope': 'Actual freshly rebuilt Roslyn scanner and actual checker on owned source copies; no product mutation.'}
(destination / 'result.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(result, ensure_ascii=False, indent=2))
if changed or any(row['AuditExit'] != row['ExpectedExit'] or row['ResourceErrors'] for row in summary):
    raise SystemExit(1)
