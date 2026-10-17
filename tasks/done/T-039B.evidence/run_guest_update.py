"""Run the two-version local update in an owned Windows Sandbox."""
from pathlib import Path
import hashlib, json, shutil, subprocess, sys, time, uuid

sys.stdout.reconfigure(encoding='utf-8')
evidence = Path(__file__).resolve().parent
repo = evidence.parents[2]
label = sys.argv[1] if len(sys.argv) > 1 else 'guest-update-r3-1'
report = evidence / label
report.mkdir(exist_ok=False)

identifier = str(uuid.uuid4())
work = repo / 'artifacts/t039b/sandbox' / identifier
inputs = work / 'input'
output = work / 'output'
inputs.mkdir(parents=True)
output.mkdir()

# Stage inputs from final-4 packages and final-3 update host
shutil.copyfile(repo / 'artifacts/t039b/release-0.2.0-final-4/Iris-0.2.0-Setup.exe', inputs / 'Iris-0.2.0-Setup.exe')
shutil.copyfile(repo / 'artifacts/t039b/release-0.2.1-final-4/Iris-0.2.1-full.nupkg', inputs / 'Iris-0.2.1-full.nupkg')
shutil.copytree(repo / 'artifacts/t039b/guest-update-host-final-3', inputs / 'update-host-3', dirs_exist_ok=True)
shutil.copyfile(evidence / 'guest_common.ps1', inputs / 'guest_common.ps1')
shutil.copyfile(evidence / 'guest_update.ps1', inputs / 'guest_update.ps1')

cli = 'C:/Users/28910/AppData/Local/Microsoft/WindowsApps/wsb.exe'
config = '<Configuration><vGPU>Disable</vGPU><Networking>Enable</Networking><AudioInput>Disable</AudioInput><VideoInput>Disable</VideoInput><ClipboardRedirection>Disable</ClipboardRedirection><PrinterRedirection>Disable</PrinterRedirection><MemoryInMB>6144</MemoryInMB></Configuration>'
(report / 'intent.json').write_text(json.dumps({
    'SandboxId': identifier,
    'Config': config,
    'ReadOnlyInput': str(inputs),
    'WritableOutput': str(output),
    'Files': [{'Name': p.name, 'Bytes': p.stat().st_size, 'Sha256': hashlib.sha256(p.read_bytes()).hexdigest()} for p in inputs.rglob('*') if p.is_file()],
    'Purpose': 'Two-version update verification (helper handoff, native apply, work-* cleanup, exactly one restart, updated settings window).'
}, indent=2), encoding='utf-8')

def run(name, args):
    with (report / (name + '.log')).open('wb') as outputFile:
        r = subprocess.run([cli, *args], stdout=outputFile, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
    r.stdout = (report / (name + '.log')).read_bytes()
    (report / (name + '.command.json')).write_text(json.dumps({'Args': args, 'ExitCode': r.returncode}), encoding='utf-8')
    print(name + ' exit=' + str(r.returncode), flush=True)
    return r

before = run('list-before', ['list', '--raw'])
if before.returncode or json.loads(before.stdout.decode('utf-8-sig')).get('WindowsSandboxEnvironments'):
    raise RuntimeError('Existing sandbox or listing failure; no other instance touched.')

started = False
try:
    if run('start', ['start', '--id', identifier, '--config', config, '--raw']).returncode:
        raise RuntimeError('Start failed')
    started = True
    if run('share-input', ['share', '--id', identifier, '--host-path', str(inputs), '--sandbox-path', r'C:\IrisBInput', '--raw']).returncode:
        raise RuntimeError('Input mapping failed')
    if run('share-output', ['share', '--id', identifier, '--host-path', str(output), '--sandbox-path', r'C:\IrisBOutput', '--allow-write', '--raw']).returncode:
        raise RuntimeError('Output mapping failed')
    if run('connect', ['connect', '--id', identifier, '--raw']).returncode:
        raise RuntimeError('Guest login connection failed')

    for attempt in range(30):
        ready = run('login-ready-' + str(attempt), ['exec', '--id', identifier, '--command', 'cmd.exe /c exit 0', '--run-as', 'ExistingLogin', '--raw'])
        if ready.returncode == 0:
            break
        time.sleep(1)
    else:
        raise RuntimeError('Guest login did not become ready')

    command = subprocess.list2cmdline([
        r'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe',
        '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
        '-File', r'C:\IrisBInput\guest_update.ps1',
        '-Label', 'update-r3',
        '-HostDirectory', 'update-host-3'
    ])
    if run('execute', ['exec', '--id', identifier, '--command', command, '--run-as', 'ExistingLogin', '--raw']).returncode:
        raise RuntimeError('Guest exec failed')

    target_result = output / 'update-r3' / 'result.json'
    target_progress = output / 'update-r3' / 'progress.json'
    deadline = time.monotonic() + 1500
    last = None
    while not target_result.exists() and time.monotonic() < deadline:
        if target_progress.exists():
            try:
                text = target_progress.read_text(encoding='utf-8-sig')
                if text != last:
                    print(text[-1600:], flush=True)
                    last = text
            except Exception:
                pass
        time.sleep(1)

    if not target_result.exists():
        raise RuntimeError('Guest result not received; preserve evidence before ending owned environment')

    result = json.loads(target_result.read_text(encoding='utf-8-sig'))
    print(json.dumps(result, ensure_ascii=False), flush=True)
    if result.get('Failure'):
        raise RuntimeError('Real guest update validation failed: ' + str(result['Failure']))
finally:
    shutil.copytree(output, report / 'output', dirs_exist_ok=True)
    if started:
        run('stop', ['stop', '--id', identifier, '--raw'])
    run('list-after', ['list', '--raw'])
