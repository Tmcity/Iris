from pathlib import Path
from xml.sax.saxutils import escape
import json,hashlib,subprocess
r=Path('F:/Iris');d=Path(__file__).parent;o=r/'tests/Iris.Core.Tests/bin/Release/net10.0'
refs=sorted(p for p in o.glob('*.dll') if not p.name.endswith('.Tests.dll'))
sources=['Program.cs','Boundaries.cs','BehaviorChecks.cs','LifecycleChecks.cs','OwnershipChecks.cs','RegistryIntegrationChecks.cs','SelectionChecks.cs']
items=''.join(f'<Compile Include="{p}" />' for p in sources)+'<None Include="normalizer-vectors.json" CopyToOutputDirectory="PreserveNewest" /><Compile Include="F:/Iris/tasks/done/T-030A.review/Boundaries.cs" Link="RegistryBoundaries.cs" />'
items+=''.join(f'<Reference Include="{escape(p.stem)}"><HintPath>{escape(str(p))}</HintPath></Reference>' for p in refs)
(d/'Review.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Iris.Core.Tests</AssemblyName><OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup>'+items+'</ItemGroup></Project>',encoding='utf-8')
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
(d/'manifest.json').write_text(json.dumps({'commit':subprocess.check_output(['git','rev-parse','HEAD'],cwd=r,text=True).strip(),'sources':{p.relative_to(r).as_posix():sha(p) for p in (r/'src/Iris.Core/Ocr').glob('*.cs')},'probeSources':{p:sha(d/p) for p in sources},'productDlls':{p.name:sha(p) for p in refs if p.name.startswith('Iris.')},'vectors':sha(d/'normalizer-vectors.json'),'phase':'Binding only. Record execution separately; developer still implementing unless final delivery confirmed.'},indent=2),encoding='utf-8')
print('Bound actual product DLL and fixed vectors; no execution implied.')
