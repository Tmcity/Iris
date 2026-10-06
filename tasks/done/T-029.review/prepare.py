from pathlib import Path
from xml.sax.saxutils import escape
import json,hashlib,subprocess
root=Path('F:/Iris');directory=Path(__file__).parent
output=root/'tests/Iris.Platform.Windows.Tests/bin/Release/net10.0-windows10.0.19041.0'
references=sorted(p for p in output.glob('*.dll') if not p.name.endswith('.Tests.dll'))
for name in ['Iris.Core.dll','Iris.Platform.Windows.dll']:assert (output/name).is_file(),name
xml='\n'.join(f'<Reference Include="{escape(p.stem)}"><HintPath>{escape(str(p))}</HintPath></Reference>' for p in references)
(directory/'Review.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0-windows10.0.19041.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>Iris.Platform.Windows.Tests</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup>'+xml+'</ItemGroup></Project>',encoding='utf-8')
files=list((root/'src/Iris.Platform.Windows/Ocr').glob('*.cs'))+[root/'src/Iris.Core/Iris.Core.csproj',root/'src/Iris.Core/Ocr/IOcrProvider.cs',root/'src/Iris.Platform.Windows/DependencyInjection/PlatformWindowsServiceCollectionExtensions.cs',root/'specs/CONTRACTS.cs']
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
m={'commit':subprocess.check_output(['git','rev-parse','HEAD'],cwd=root,text=True).strip(),'trackedDiff':subprocess.check_output(['git','diff','--name-only'],cwd=root,text=True).splitlines(),'sources':{p.relative_to(root).as_posix():sha(p) for p in files},'productDlls':{p.name:sha(p) for p in references if p.name.startswith('Iris.')},'probeSources':{p.name:sha(p) for p in directory.glob('*.cs')},'fixtures':{p.name:sha(p) for p in (directory/'fixtures').iterdir()},'boundary':'Actual provider and WinRT image; fake OCR for fault/semantic cases, one explicitly allowed actual SDK synthetic smoke; no user content.'}
(directory/'manifest.json').write_text(json.dumps(m,indent=2),encoding='utf-8')
print('Bound current DLL/source/fixture fingerprints. No product check executed.')
