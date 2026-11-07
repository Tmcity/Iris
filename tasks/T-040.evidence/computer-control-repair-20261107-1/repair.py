"""Repair the missing official Windows client export without changing its code."""
from pathlib import Path
import datetime
import hashlib
import json
import re

BACKUP = Path(__file__).resolve().parent
PACKAGE = Path('C:/Users/28910/AppData/Local/OpenAI/Codex/runtimes/cua_node/c4873bdffed0b168/bin/node_modules/@oai/sky/package.json')
PLUGIN = Path('C:/Users/28910/.codex/plugins/cache/openai-bundled/computer-use/26.609.41114/scripts/computer-use-client.mjs')
SUBPATH = './dist/project/cua/sky_js/src/targets/windows/internal/computer_use_client_base.js'
BASE = PACKAGE.parent / SUBPATH
raw = PACKAGE.read_bytes()
text = raw.decode('utf-8')
data = json.loads(text)
assert data['name'] == '@oai/sky' and data['version'] == '0.7.17'
assert SUBPATH not in data['exports']
assert BASE.is_file()
assert ('@oai/sky/' + SUBPATH[2:]) in PLUGIN.read_text(encoding='utf-8')
for name, path in [('package.json.original', PACKAGE), ('computer-use-client.mjs.original', PLUGIN), ('computer_use_client_base.js.original', BASE)]:
    with (BACKUP / name).open('xb') as writer:
        writer.write(path.read_bytes())
match = re.search(r'(^[ \t]*"\./client": [^\n]+)(\n)', text, re.M)
assert match and not match.group(1).rstrip().endswith(',')
indent = re.match(r'[ \t]*', match.group(1)).group(0)
replacement = match.group(1) + ',\n' + indent + json.dumps(SUBPATH) + ': ' + json.dumps(SUBPATH) + '\n'
patched = text[:match.start()] + replacement + text[match.end():]
updated = json.loads(patched)
expected = json.loads(text)
expected['exports'][SUBPATH] = SUBPATH
assert updated == expected
PACKAGE.write_bytes(patched.encode('utf-8'))
record = {
    'Utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'PackagePath': str(PACKAGE), 'Version': data['version'],
    'OnlySemanticChange': {'ExportsAdded': {SUBPATH: SUBPATH}},
    'OriginalPackageSha256': hashlib.sha256(raw).hexdigest(),
    'PatchedPackageSha256': hashlib.sha256(PACKAGE.read_bytes()).hexdigest(),
    'PluginClientSha256': hashlib.sha256(PLUGIN.read_bytes()).hexdigest(),
    'InternalClientBaseSha256': hashlib.sha256(BASE.read_bytes()).hexdigest(),
    'ClientCodeChanged': False, 'ApprovalLogicChanged': False,
    'InterruptionHandlingChanged': False, 'IrisOperated': False,
}
(BACKUP / 'repair.json').write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
(BACKUP / 'rollback.py').write_text(
    'from pathlib import Path\nimport hashlib\n'
    'backup = Path(__file__).resolve().parent\n'
    f'target = Path({str(PACKAGE)!r})\n'
    f'expected = {record["PatchedPackageSha256"]!r}\n'
    'assert hashlib.sha256(target.read_bytes()).hexdigest() == expected, "Package changed since repair; inspect before restoring"\n'
    'original = (backup / "package.json.original").read_bytes()\n'
    'target.write_bytes(original)\n'
    'print("Original package metadata restored")\n', encoding='utf-8')
print(json.dumps(record, indent=2))
