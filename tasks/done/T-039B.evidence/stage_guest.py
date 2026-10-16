from pathlib import Path
import hashlib
import json
import shutil
import sys

evidence = Path(__file__).resolve().parent
root = evidence.parents[2]
sandbox, label = sys.argv[1:3]
inputs = root / 'artifacts/t039b/sandbox' / sandbox / 'input'
assert inputs.is_dir()
report = evidence / label
report.mkdir(exist_ok=False)
common = (evidence / 'guest_install.ps1').read_text(encoding='utf-8')
common = common[common.index('function Record'):common.index('\ntry {\n    $os')]
(evidence / 'guest_common.ps1').write_text(common, encoding='utf-8')
matrix = (evidence / 'guest_matrix.ps1').read_text(encoding='utf-8')
dialogs = matrix[matrix.index("Add-Type @'"):matrix.index("\ntry {\n    Require 'prior-silent-retention'")]
(evidence / 'guest_dialogs.ps1').write_text(dialogs, encoding='utf-8')
for name in ['guest_common.ps1', 'guest_dialogs.ps1', 'guest_coverage.ps1', 'guest_failure.ps1', 'guest_update.ps1']:
    source = (evidence / name).read_text(encoding='utf-8')
    (inputs / name).write_text(source, encoding='utf-8-sig')
    shutil.copyfile(inputs / name, report / (name + '.txt'))
driver = root / 'artifacts/t039b/guest-update-host-final-2'
shutil.copyfile(evidence / 'GuestUpdateHost/parent.ps1', driver / 'parent.ps1')
shutil.copytree(driver, inputs / 'update-host-final', dirs_exist_ok=True)
shutil.copyfile(root / 'artifacts/t039b/release-0.2.1-final-3/Iris-0.2.1-full.nupkg', inputs / 'Iris-0.2.1-full.nupkg')
shutil.copyfile(root / 'artifacts/t039b/release-0.2.0-final-3/Iris-0.2.0-Setup.exe', inputs / 'Iris-0.2.0-Setup.exe')
files = [{'Path': str(path.relative_to(inputs)), 'Bytes': path.stat().st_size, 'Sha256': hashlib.sha256(path.read_bytes()).hexdigest()} for path in inputs.rglob('*') if path.is_file()]
(report / 'input-binding.json').write_text(json.dumps({'Sandbox': sandbox, 'Files': files}, indent=2), encoding='utf-8')
print(f'Staged {len(files)} bound inputs for {label}')
