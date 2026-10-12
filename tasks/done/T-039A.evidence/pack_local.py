from pathlib import Path
import json,shutil,subprocess,sys
e=Path(__file__).resolve().parent;w=e.parents[2]/'artifacts/t039a'
config=json.loads((w/'probe-settings.json').read_text());version=sys.argv[1]
destination=w/('payload-v1' if version=='1.0.0' else 'payload-v2')
if version!='1.0.0':
 subprocess.run([sys.executable,str(e/'run.py'),'--label','payload-publish-v2','--','dotnet','publish',str(w/'Payload/Payload.csproj'),'-c','Release','-r','win-x64','--self-contained','false','-o',str(destination),'-p:Version='+version,'-p:RestoreConfigFile='+str(w/'NuGet.Config')],check=True)
# Actual separately published helper and English resource bytes are carried as passive package payload.
shutil.copytree(w/'uia-publish',destination/'uia')
(destination/'en-US').mkdir(exist_ok=True)
for name in ['Iris.App.resources.dll','Iris.Core.resources.dll']:
 shutil.copyfile(w/'iris-publish-baseline/en-US'/name,destination/'en-US'/name)
command=[str(w/'tools/vpk.exe'),'--skip-updates','pack','--packId',config['PackId'],'--packVersion',version,'--packDir',str(destination),'--mainExe','Payload.exe','--outputDir',str(w/'releases'),
 '--runtime','win10.0.19041-x64','--framework','net10.0-x64-desktop']
subprocess.run([sys.executable,str(e/'run.py'),'--label','vpk-pack-'+version,'--',*command],check=True)
