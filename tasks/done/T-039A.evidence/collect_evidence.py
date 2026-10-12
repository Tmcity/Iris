from pathlib import Path
import hashlib,json,shutil,zipfile,xml.etree.ElementTree as ET
e=Path(__file__).resolve().parent;w=e.parents[2]/'artifacts/t039a';source=w/'official-source/velopack-1.2.161'
docs=Path('C:/Users/28910/AppData/Local/Temp/iris-t039-architect')
for name in ['bootstrapping.html','bootstrapping.txt','runtime.html','runtime.txt','uninstalling.html','uninstalling.txt','github-actions.html','github-actions.txt','nuget-velopack.json','nuget-vpk.json']:
 shutil.copyfile(docs/name,e/'official-docs'/name)
selected=['src/lib-csharp/UpdateManager.cs','src/lib-csharp/VelopackApp.cs','src/lib-csharp/UpdateOptions.cs','src/lib-csharp/UpdateExe.cs',
 'src/lib-csharp/Sources/IUpdateSource.cs','src/lib-csharp/Sources/IFileDownloader.cs','src/lib-csharp/Sources/SimpleFileSource.cs','src/lib-csharp/Sources/GithubSource.cs','src/lib-csharp/Sources/GitBase.cs','src/lib-csharp/Sources/HttpClientFileDownloader.cs',
 'src/lib-csharp/Locators/TestVelopackLocator.cs','src/lib-csharp/Locators/IVelopackLocator.cs','src/lib-csharp/Locators/VelopackLocator.cs',
 'src/lib-csharp/Windows/RuntimeInfo.cs','src/lib-csharp/Windows/Runtimes.cs',
 'src/bins/src/shared/util_common.rs','src/bins/src/windows/util.rs','src/bins/src/windows/runtimes.rs','src/bins/src/windows/registry.rs',
 'src/bins/src/commands/uninstall.rs','src/bins/src/commands/install.rs','src/bins/src/commands/apply_windows_impl.rs',
 'src/vpk/Velopack.Packaging.Windows/Commands/WindowsPackCommandRunner.cs','src/vpk/Velopack.Packaging/PackageBuilder.cs']
bindings=[]
for name in selected:
 p=source/name;dest=e/'official-source'/name;dest.parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(p,dest)
 bindings.append({'Path':name,'Sha256':hashlib.sha256(p.read_bytes()).hexdigest()})
pinned=[]
for p in (docs/'pinned-csharp').rglob('*.cs'):
 local=source/'src/lib-csharp'/p.relative_to(docs/'pinned-csharp')
 pinned.append({'Path':str(p.relative_to(docs/'pinned-csharp')),'SameBytes':p.read_bytes()==local.read_bytes()})
 assert p.read_bytes()==local.read_bytes(),p
packages=[]
for folder in ['releases-v1','releases']:
 for p in sorted((w/folder).iterdir()):
  if not p.is_file(): continue
  item={'Path':p.relative_to(w).as_posix(),'Bytes':p.stat().st_size,'Sha256':hashlib.sha256(p.read_bytes()).hexdigest()}
  if p.suffix=='.nupkg':
   with zipfile.ZipFile(p) as z:
    nuspec=next(n for n in z.namelist() if n.endswith('.nuspec'))
    raw=z.read(nuspec);(e/(p.stem+'.nuspec')).write_bytes(raw)
    item['Metadata']={x.tag.split('}')[-1]:x.text for x in ET.fromstring(raw).iter() if len(x)==0}
    item['Files']=z.namelist()
    if p.name.endswith('-full.nupkg'):
     for relative in ['uia/Iris.UiaHost.exe','uia/Iris.UiaHost.runtimeconfig.json','en-US/Iris.App.resources.dll','en-US/Iris.Core.resources.dll']:
      name='lib/app/'+relative
      assert name in item['Files'],name
      item.setdefault('PublishedFileMatches',{})[relative]=hashlib.sha256(z.read(name)).hexdigest()==hashlib.sha256((w/'payload-v1'/relative).read_bytes()).hexdigest()
      assert item['PublishedFileMatches'][relative]
  packages.append(item)
for name in ['releases.win.json','RELEASES']:
 p=w/'releases'/name
 if p.exists():shutil.copyfile(p,e/name)
(e/'package-inventory.json').write_text(json.dumps(packages,indent=2),encoding='utf-8')
(e/'source-bindings.json').write_text(json.dumps({'Tag':'1.2.161','OfficialFiles':bindings,'ArchitectPinnedMatches':pinned},indent=2),encoding='utf-8')
print(json.dumps({'OfficialFiles':len(bindings),'ArchitectPinnedMatches':len(pinned),'PackageArtifacts':len(packages),'FullPackages':[{'Path':x['Path'],'Metadata':x['Metadata'],'PublishedFileMatches':x['PublishedFileMatches']} for x in packages if 'PublishedFileMatches' in x]}))
