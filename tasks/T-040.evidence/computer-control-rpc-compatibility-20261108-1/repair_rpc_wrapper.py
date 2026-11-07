"""Compatibility fix using the existing official trusted Sky RPC adapter."""
from pathlib import Path
import datetime
import hashlib
import json

DEST = Path('F:/Iris/tasks/T-040.evidence/computer-control-rpc-compatibility-20261108-1')
PLUGIN = Path('C:/Users/28910/.codex/plugins/cache/openai-bundled/computer-use/26.609.41114')
CLIENT = PLUGIN / 'scripts/computer-use-client.mjs'
RUNTIME = Path('C:/Users/28910/AppData/Local/OpenAI/Codex/runtimes/cua_node/c4873bdffed0b168/bin/node_modules')
raw = CLIENT.read_bytes()
before_sha = hashlib.sha256(raw).hexdigest()
assert before_sha == 'bdc61752e0f9d9b1e73e3cba25ca71d3d9fe54d335ed8196378106081474d7c1'
DEST.mkdir(parents=True, exist_ok=False)
(DEST / 'computer-use-client.mjs.original').write_bytes(raw)
newline = '\r\n' if b'\r\n' in raw else '\n'
text = raw.decode('utf-8')
before = '''  if (platform() !== "win32") {
    const { sky } = await import("@oai/sky");
    return installComputerUseRuntime({ globals, sky });
  }
'''.replace('\n', newline)
after = '''  const useNativeWindowsPipe =
    platform() === "win32" &&
    typeof getComputerUsePrivilegedNodeRepl()?.nativePipe?.createConnection === "function";
  if (!useNativeWindowsPipe) {
    if (platform() === "win32" && typeof globals.nodeRepl?.rpc !== "function") {
      throw new Error("Computer Use requires a trusted Node REPL Sky service or native pipe");
    }
    const { sky } = await import("@oai/sky");
    if (
      platform() === "win32" &&
      (sky.target !== "windows" ||
        typeof sky.list_apps !== "function" ||
        typeof sky.get_window_state !== "function")
    ) {
      throw new Error("Computer Use requires the Windows window API from the trusted Sky service");
    }
    return installComputerUseRuntime({ globals, sky });
  }
'''.replace('\n', newline)
assert text.count(before) == 1
text = text.replace(before, after, 1)
before_meta = '''        globals.nodeRepl?.setResponseMeta({
          [TOOL_SURFACE_META_KEY]: {
            kind: "computerUse",
            app: getComputerUseAppReference(args[0]),
          },
        });
'''.replace('\n', newline)
after_meta = '''        if (typeof globals.nodeRepl?.setResponseMeta === "function") {
          globals.nodeRepl.setResponseMeta({
            [TOOL_SURFACE_META_KEY]: {
              kind: "computerUse",
              app: getComputerUseAppReference(args[0]),
            },
          });
        }
'''.replace('\n', newline)
assert text.count(before_meta) == 1
text = text.replace(before_meta, after_meta, 1)
patched = text.encode('utf-8')
CLIENT.write_bytes(patched)
(DEST / 'computer-use-client.mjs.patched').write_bytes(patched)
source_files = {
    'sky.js': RUNTIME / '@oai/sky/dist/project/cua/sky_js/src/sky.js',
    'sky_service_rpc.js': RUNTIME / '@oai/sky/dist/project/cua/sky_js/src/sky_service_rpc.js',
    'service.js': RUNTIME / '@oai/sky/dist/project/cua/sky_js/src/service.js',
    'windows-create-client.js': RUNTIME / '@oai/sky/dist/project/cua/sky_js/src/targets/windows/create_client.js',
    'windows-client.js': RUNTIME / '@oai/sky/dist/project/cua/sky_js/src/targets/windows/internal/computer_use_client.js',
    'windows-client-base.js': RUNTIME / '@oai/sky/dist/project/cua/sky_js/src/targets/windows/internal/computer_use_client_base.js',
    'windows-helper-transport.js': RUNTIME / '@oai/sky/dist/project/cua/sky_js/src/targets/windows/internal/helper_transport.js',
    'codex-turn-metadata.js': RUNTIME / '@oai/sky/dist/project/cua/sky_js/src/targets/windows/internal/codex_turn_metadata.js',
    'cua-repl-environment.js': RUNTIME / '@oai/cua-repl/dist/lib/js/oai_js_cua_repl/src/environment.js',
    'cua-repl-launch.js': RUNTIME / '@oai/cua-repl/dist/lib/js/oai_js_cua_repl/src/launch.js',
    'sky-package.json': RUNTIME / '@oai/sky/package.json',
    'cua-repl-package.json': RUNTIME / '@oai/cua-repl/package.json',
}
(DEST / 'official-source').mkdir()
rows = []
for name, path in source_files.items():
    value = path.read_bytes()
    (DEST / 'official-source' / name).write_bytes(value)
    rows.append({'Path': str(path), 'ArchivePath': 'official-source/' + name,
                 'Bytes': len(value), 'Sha256': hashlib.sha256(value).hexdigest()})
record = {
    'Utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'ClientPath': str(CLIENT), 'BeforeSha256': before_sha,
    'AfterSha256': hashlib.sha256(patched).hexdigest(),
    'Changes': ['Select the existing official trusted Sky RPC adapter when the direct native Node API is unavailable',
                'Use setResponseMeta only when the runtime supplies that optional function'],
    'SdkServiceBaseTransportChanged': False, 'ApprovalFunctionsFabricated': False,
    'ApprovalHeadersFabricated': False, 'NativePipeFabricated': False,
    'HelperProcessStartedDirectly': False, 'SharedKernelResetPerformed': False,
    'RootReportedOfficialRpcSetup': {'Target': 'windows', 'WindowApiMethodsPresent': True},
    'RootRuntimeVerificationPending': True, 'OfficialSourceBindings': rows,
}
(DEST / 'repair.json').write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
(DEST / 'rollback.py').write_text(
    'from pathlib import Path\nimport hashlib\n'
    'root = Path(__file__).resolve().parent\n'
    f'target = Path({str(CLIENT)!r})\n'
    f'expected = {record["AfterSha256"]!r}\n'
    'assert hashlib.sha256(target.read_bytes()).hexdigest() == expected, "Wrapper changed; inspect before restoring"\n'
    'target.write_bytes((root / "computer-use-client.mjs.original").read_bytes())\n'
    'print("Original wrapper restored")\n', encoding='utf-8')
for row in rows:
    assert hashlib.sha256(Path(row['Path']).read_bytes()).hexdigest() == row['Sha256']
print(json.dumps({'Archive': str(DEST), 'BeforeSha256': before_sha,
                  'AfterSha256': record['AfterSha256'], 'OnlyWrapperChanged': True,
                  'RootRuntimeVerificationPending': True}, indent=2))
