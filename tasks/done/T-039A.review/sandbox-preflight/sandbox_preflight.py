"""Inspect one owned, offline Windows Sandbox; never install or launch Iris."""
import json
import pathlib
import subprocess
import sys
import time
import uuid

sys.stdout.reconfigure(encoding='utf-8')
root = pathlib.Path(r'F:\Iris\artifacts\t039-sandbox-preflight')
sid = str(uuid.uuid4())
work = root / sid
output = work / 'output'
output.mkdir(parents=True)
cli = r'C:\Users\28910\AppData\Local\Microsoft\WindowsApps\wsb.exe'
config = '<Configuration><vGPU>Disable</vGPU><Networking>Disable</Networking><AudioInput>Disable</AudioInput><VideoInput>Disable</VideoInput><ClipboardRedirection>Disable</ClipboardRedirection><PrinterRedirection>Disable</PrinterRedirection><MemoryInMB>4096</MemoryInMB></Configuration>'
(work / 'intent.json').write_text(json.dumps({'SandboxId': sid, 'Configuration': config, 'OnlyHostShare': str(output), 'Purpose': 'OS/runtime inspection only; no Iris or installer execution'}, indent=2), encoding='utf-8')

def run(name, args):
    result = subprocess.run([cli, *args], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
    (work / (name + '.log')).write_bytes(result.stdout)
    (work / (name + '.command.json')).write_text(json.dumps({'Arguments': args, 'ExitCode': result.returncode}, indent=2), encoding='utf-8')
    print(name + ' exit=' + str(result.returncode), flush=True)
    print(result.stdout.decode('utf-8', errors='replace')[-4500:], flush=True)
    return result

before = run('list-before', ['list', '--raw'])
if before.returncode:
    raise SystemExit(before.returncode)
existing = json.loads(before.stdout.decode('utf-8-sig')).get('WindowsSandboxEnvironments', [])
if existing:
    print('An existing sandbox belongs to the user; preflight did not start another one.')
    raise SystemExit(3)

started = False
try:
    start = run('start', ['start', '--id', sid, '--config', config, '--raw'])
    if start.returncode:
        raise RuntimeError('Owned sandbox startup failed; no installation was attempted.')
    started = True
    shared = run('share-output', ['share', '--id', sid, '--host-path', str(output), '--sandbox-path', r'C:\IrisPreflightOutput', '--allow-write', '--raw'])
    if shared.returncode:
        raise RuntimeError('Cannot share the owned output directory.')
    command = "$os = Get-CimInstance Win32_OperatingSystem; $fx = Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\NET Framework Setup\\NDP\\v4\\Full' -ErrorAction SilentlyContinue; $runtimes = @(Get-ChildItem ($env:ProgramFiles + '\\dotnet\\shared') -Directory -ErrorAction SilentlyContinue | ForEach-Object { $family = $_.Name; Get-ChildItem $_.FullName -Directory | ForEach-Object { [pscustomobject]@{ Family=$family; Version=$_.Name } } }); [pscustomobject]@{ Caption=$os.Caption; Version=$os.Version; BuildNumber=$os.BuildNumber; FrameworkRelease=$fx.Release; SharedRuntimes=$runtimes; Context='Owned offline sandbox System inspection' } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath 'C:\\IrisPreflightOutput\\environment.json' -Encoding UTF8"
    invocation = subprocess.list2cmdline([r'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe', '-NoProfile', '-NonInteractive', '-Command', command])
    executed = run('inspect-environment', ['exec', '--id', sid, '--command', invocation, '--run-as', 'System', '--raw'])
    if executed.returncode:
        raise RuntimeError('Guest environment query failed.')
    result_file = output / 'environment.json'
    deadline = time.monotonic() + 30
    while not result_file.exists() and time.monotonic() < deadline:
        time.sleep(.5)
    if not result_file.exists():
        raise RuntimeError('Guest result did not arrive in the owned output directory.')
    print(result_file.read_text(encoding='utf-8-sig'), flush=True)
finally:
    if started:
        stop = run('stop', ['stop', '--id', sid, '--raw'])
        if stop.returncode:
            print('Owned sandbox may still be running; inspect the saved SandboxId before any retry.', flush=True)
    run('list-after', ['list', '--raw'])
    print('PREFLIGHT_DIRECTORY ' + str(work), flush=True)
