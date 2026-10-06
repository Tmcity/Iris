from pathlib import Path
from xml.sax.saxutils import escape
import hashlib,json,subprocess,shutil
r=Path('F:/Iris');d=Path(__file__).parent;o=r/'tests/Iris.App.Tests/bin/Release/net10.0-windows10.0.19041.0'
(d/'fixtures').mkdir(exist_ok=True)
shutil.copy2(r/'tasks/done/T-030A.review/fixtures/hello-123.png',d/'fixtures/hello-123.png')
refs=sorted(p for p in o.glob('*.dll') if not p.name.endswith('.Tests.dll'))
sources=['AppProgram.cs','AppChecks.cs','Boundaries.cs']
items=''.join(f'<Compile Include="{s}" />' for s in sources)+'<Compile Include="F:/Iris/tasks/done/T-030A.review/Boundaries.cs" Link="RegistryBoundaries.cs" /><None Include="fixtures/hello-123.png" CopyToOutputDirectory="PreserveNewest" />'
items+=''.join(f'<Reference Include="{escape(p.stem)}"><HintPath>{escape(str(p))}</HintPath></Reference>' for p in refs)
(d/'AppReview.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0-windows10.0.19041.0</TargetFramework><UseWPF>true</UseWPF><AssemblyName>Iris.App.Tests</AssemblyName><OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup>'+items+'</ItemGroup></Project>',encoding='utf-8')
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
(d/'app-manifest.json').write_text(json.dumps({'commit':subprocess.check_output(['git','rev-parse','HEAD'],cwd=r,text=True).strip(),'sources':{str(p.relative_to(r)):sha(p) for p in [r/'src/Iris.App/Hosting/AppBootstrapper.cs',r/'src/Iris.Core/Ocr/OcrService.cs']},'probeSources':{p:sha(d/p) for p in sources},'productDlls':{p.name:sha(p) for p in refs if p.name.startswith('Iris.')},'fixture':sha(d/'fixtures/hello-123.png'),'phase':'Binding only; no execution implied'},indent=2),encoding='utf-8')
print('Prepared actual App/Core/WinRT DLL references only.')
