from pathlib import Path
import json,subprocess,sys,os
e=Path(__file__).resolve().parent;w=e.parents[2]/'artifacts/t039a'
config=json.loads((w/'probe-settings.json').read_text())
dll=w/'Probe/bin/Release/net10.0-windows10.0.19041.0/Probe.dll'
def run(label,*args):
 return subprocess.run([sys.executable,str(e/'run.py'),'--label',label,'--','dotnet',str(dll),*args]).returncode
if sys.argv[1]=='sdk':
 label='sdk-probes-'+(sys.argv[2] if len(sys.argv)>2 else '1')
 raise SystemExit(run(label,config['PackId'],str(w/'releases'),str(w/label)))
if sys.argv[1]=='auto':
 for mode in ['auto-on','auto-off']:
  if run('bootstrap-'+mode,'boot',str(w/('boot-'+mode)),mode,str(w/'releases')):raise SystemExit(1)
 raise SystemExit(0)
for name,mode,args in [
 ('install','record',['--veloapp-install','1.0.0']),('uninstall','record',['--veloapp-uninstall','1.0.0']),
 ('obsolete','record',['--veloapp-obsolete','1.0.0']),('updated','record',['--veloapp-updated','1.1.0']),
 ('uninstall-throws','throw',['--veloapp-uninstall','1.0.0']),('malformed-hook','record',['--veloapp-uninstall','invalid']),
 ('missing-version','record',['--veloapp-uninstall']),('ordinary-help','record',['--help']),
 ('real-exit','real-exit',['--veloapp-uninstall','1.0.0'])]:
 if run('bootstrap-'+name,'boot',str(w/('boot-'+name)),mode,*args):raise SystemExit(1)
for name,variable in [('first-run','VELOPACK_FIRSTRUN'),('restarted','VELOPACK_RESTART')]:
 os.environ[variable]='1'
 try:
  if run('bootstrap-'+name,'boot',str(w/('boot-'+name)),'record'):raise SystemExit(1)
 finally:os.environ.pop(variable)
