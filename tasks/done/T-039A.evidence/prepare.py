"""Create an owned workspace and retain official document inputs, without modifying production files."""
from pathlib import Path
import hashlib
import json
import shutil
import subprocess
import uuid

root = Path(__file__).resolve().parents[3]
evidence = Path(__file__).resolve().parent
work = root / 'artifacts/t039a'
work.mkdir(parents=True, exist_ok=True)
config = work / 'NuGet.Config'
config.write_text('<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>',encoding='utf-8')
docs = evidence / 'official-docs'
docs.mkdir(exist_ok=True)
for name in ['velopack-csharp.html','velopack-csharp.html.txt','velopack-index.html','velopack-index.html.txt','velopack-latest.json','velopack-sitemap.xml']:
    p = Path('C:/Users/28910/AppData/Local/Temp/iris-t039-architect') / name
    shutil.copyfile(p,docs/p.name)
archive = work / 'iris-baseline.zip'
subprocess.run(['git','archive','--format=zip','--output',str(archive),'6f14e81ebd298d83eda7330008310015efa1937b','src','Directory.Build.props','Directory.Packages.props','global.json','.editorconfig'],cwd=root,check=True)
import zipfile
with zipfile.ZipFile(archive) as z: z.extractall(work/'iris-copy')
settings = {'Work':str(work),'PackId':'Iris.T039A.'+uuid.uuid4().hex,'Version':'1.2.161','ReleaseTarget':'https://github.com/Tmcity/Iris','Baseline':'6f14e81ebd298d83eda7330008310015efa1937b'}
(work/'probe-settings.json').write_text(json.dumps(settings,indent=2),encoding='utf-8')
(evidence/'preparation.json').write_text(json.dumps({'settings':settings,'baselineZipSha256':hashlib.sha256(archive.read_bytes()).hexdigest(),'initialDocuments':[{'File':p.name,'Sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in docs.iterdir()]},indent=2),encoding='utf-8')
print(json.dumps(settings))
