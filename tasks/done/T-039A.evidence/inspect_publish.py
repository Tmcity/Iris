from pathlib import Path
import hashlib,json,shutil
e = Path(__file__).resolve().parent
w = e.parents[2]/'artifacts/t039a'
baseline=w/'iris-publish-baseline'; uia=w/'uia-publish'; composed=w/'iris-publish-composed'
assert not composed.exists()
shutil.copytree(baseline,composed,ignore=shutil.ignore_patterns('uia'))
shutil.copytree(uia,composed/'uia')
def inventory(folder):
 return [{'Path':p.relative_to(folder).as_posix(),'Bytes':p.stat().st_size,'Sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in sorted(folder.rglob('*')) if p.is_file()]
result={'BaselineUiaExpectedExists':(baseline/'uia/Iris.UiaHost.exe').exists(),'BaselineUiaActual':[p.relative_to(baseline).as_posix() for p in baseline.rglob('Iris.UiaHost.exe')],
 'ComposedUiaExpectedExists':(composed/'uia/Iris.UiaHost.exe').exists(),
 'AppRuntimeConfig':json.loads((composed/'Iris.App.runtimeconfig.json').read_text()),
 'UiaRuntimeConfig':json.loads((composed/'uia/Iris.UiaHost.runtimeconfig.json').read_text()),
 'Baseline':inventory(baseline),'StandaloneUia':inventory(uia),'Composed':inventory(composed)}
for p in ['Iris.App.exe','Iris.App.deps.json','en-US/Iris.App.resources.dll','en-US/Iris.Core.resources.dll','uia/Iris.UiaHost.exe','uia/Iris.UiaHost.deps.json','uia/en-US/Iris.Core.resources.dll']:
 assert (composed/p).exists(),p
assert not (composed/'coreclr.dll').exists()
(e/'publish-inventory.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
print(json.dumps({k:v for k,v in result.items() if k not in ['Baseline','StandaloneUia','Composed']}))
