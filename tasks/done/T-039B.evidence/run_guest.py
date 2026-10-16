"""Run the actual Iris installer in one owned Sandbox; only dedicated artifacts/output are shared."""
from pathlib import Path
import hashlib,json,shutil,subprocess,sys,time,uuid
sys.stdout.reconfigure(encoding='utf-8')
e=Path(__file__).resolve().parent; repo=e.parents[2]
label=sys.argv[1]; installer=Path(sys.argv[2]).resolve()
retain='--retain' in sys.argv[3:]
report=e/label; report.mkdir(exist_ok=False)
identifier=str(uuid.uuid4()); work=repo/'artifacts/t039b/sandbox'/identifier
inputs=work/'input'; output=work/'output'; inputs.mkdir(parents=True);output.mkdir()
shutil.copyfile(installer,inputs/installer.name);shutil.copyfile(e/'guest_install.ps1',inputs/'guest_install.ps1')
cli='C:/Users/28910/AppData/Local/Microsoft/WindowsApps/wsb.exe'
config='<Configuration><vGPU>Disable</vGPU><Networking>Enable</Networking><AudioInput>Disable</AudioInput><VideoInput>Disable</VideoInput><ClipboardRedirection>Disable</ClipboardRedirection><PrinterRedirection>Disable</PrinterRedirection><MemoryInMB>6144</MemoryInMB></Configuration>'
(report/'intent.json').write_text(json.dumps({'SandboxId':identifier,'Config':config,'ReadOnlyInput':str(inputs),'WritableOutput':str(output),'Files':[{'Name':p.name,'Sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in inputs.iterdir()],'Purpose':'Real Iris installation/runtime bootstrap/application/UiaHost/silent uninstall with synthetic guest data; Microsoft runtime download allowed.'},indent=2),encoding='utf-8')
def run(name,args):
 with (report/(name+'.log')).open('wb') as outputFile:
  r=subprocess.run([cli,*args],stdout=outputFile,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW)
 r.stdout=(report/(name+'.log')).read_bytes()
 (report/(name+'.command.json')).write_text(json.dumps({'Args':args,'ExitCode':r.returncode}),encoding='utf-8')
 print(name+' exit='+str(r.returncode),flush=True);return r
before=run('list-before',['list','--raw'])
if before.returncode or json.loads(before.stdout.decode('utf-8-sig')).get('WindowsSandboxEnvironments'):raise RuntimeError('Existing sandbox or listing failure; no other instance touched.')
started=False
try:
 if run('start',['start','--id',identifier,'--config',config,'--raw']).returncode:raise RuntimeError('Start failed')
 started=True
 if run('share-input',['share','--id',identifier,'--host-path',str(inputs),'--sandbox-path',r'C:\IrisBInput','--raw']).returncode:raise RuntimeError('Input mapping failed')
 if run('share-output',['share','--id',identifier,'--host-path',str(output),'--sandbox-path',r'C:\IrisBOutput','--allow-write','--raw']).returncode:raise RuntimeError('Output mapping failed')
 if run('connect',['connect','--id',identifier,'--raw']).returncode:raise RuntimeError('Guest login connection failed')
 # ExistingLogin appears after the remote session has finished logging in; this is environment readiness, not product retry.
 for attempt in range(30):
  ready=run('login-ready-'+str(attempt),['exec','--id',identifier,'--command','cmd.exe /c exit 0','--run-as','ExistingLogin','--raw'])
  if ready.returncode==0:break
  time.sleep(1)
 else:raise RuntimeError('Guest login did not become ready')
 command=subprocess.list2cmdline([r'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',r'C:\IrisBInput\guest_install.ps1'])
 if run('execute',['exec','--id',identifier,'--command',command,'--run-as','ExistingLogin','--raw']).returncode:raise RuntimeError('Guest exec failed')
 deadline=time.monotonic()+1500; last=None
 while not (output/'result.json').exists() and time.monotonic()<deadline:
  if (output/'progress.json').exists():
   text=(output/'progress.json').read_text(encoding='utf-8-sig')
   if text!=last: print(text[-1600:],flush=True);last=text
  time.sleep(1)
 if not (output/'result.json').exists():raise RuntimeError('Guest result not received; preserve evidence before ending owned environment')
 result=json.loads((output/'result.json').read_text(encoding='utf-8-sig'));print(json.dumps(result,ensure_ascii=False),flush=True)
 if result['Failure']:raise RuntimeError('Real guest validation failed')
finally:
 shutil.copytree(output,report/'output',dirs_exist_ok=True)
 if started and retain:
  (report/'retained.json').write_text(json.dumps({'SandboxId':identifier,'Reason':'Owned environment retained for subsequent diagnosis and scenario validation; must explicitly stop after use.'},indent=2),encoding='utf-8')
 elif started:run('stop',['stop','--id',identifier,'--raw'])
 run('list-after',['list','--raw'])
