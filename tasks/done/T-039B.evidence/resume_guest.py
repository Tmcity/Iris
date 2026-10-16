"""Resume only the recorded guest whose controller was stopped before dispatching the product script."""
from pathlib import Path
import json,subprocess,time,shutil,sys
sys.stdout.reconfigure(encoding='utf-8')
e=Path(__file__).resolve().parent;report=e/'guest-install-2';intent=json.loads((report/'intent.json').read_text());identifier=intent['SandboxId'];output=Path(intent['WritableOutput'])
cli='C:/Users/28910/AppData/Local/Microsoft/WindowsApps/wsb.exe'
def run(name,args):
 with (report/(name+'.log')).open('wb') as target:r=subprocess.run([cli,*args],stdout=target,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW)
 (report/(name+'.command.json')).write_text(json.dumps({'Args':args,'ExitCode':r.returncode}),encoding='utf-8');print(name,r.returncode,flush=True);return r.returncode
try:
 if run('resume-login-ready',['exec','--id',identifier,'--command','cmd.exe /c exit 0','--run-as','ExistingLogin','--raw']):raise RuntimeError('Login unavailable')
 command=subprocess.list2cmdline([r'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',r'C:\IrisBInput\guest_install.ps1'])
 if run('resume-execute',['exec','--id',identifier,'--command',command,'--run-as','ExistingLogin','--raw']):raise RuntimeError('Guest exec failed')
 deadline=time.monotonic()+900;last=None
 while not (output/'result.json').exists() and time.monotonic()<deadline:
  if (output/'progress.json').exists():
   text=(output/'progress.json').read_text(encoding='utf-8-sig')
   if text!=last:print(text[-1800:],flush=True);last=text
  time.sleep(1)
 if not (output/'result.json').exists():raise RuntimeError('Guest validation did not finish')
 result=json.loads((output/'result.json').read_text(encoding='utf-8-sig'));print(json.dumps(result,ensure_ascii=False),flush=True)
 if result['Failure']:raise RuntimeError('Real guest validation failed')
finally:
 shutil.copytree(output,report/'output',dirs_exist_ok=True)
 run('resume-stop',['stop','--id',identifier,'--raw']);run('resume-list-after',['list','--raw'])
