from pathlib import Path
from xml.sax.saxutils import escape
import hashlib,json,subprocess
r=Path('F:/Iris');d=Path(__file__).parent;out=r/'tests/Iris.Core.Tests/bin/Release/net10.0'
refs=[p for p in out.glob('*.dll') if not p.name.endswith('.Tests.dll')]
sources=['Program.cs','Boundaries.cs','Scope.cs','CommitChecks.cs','DraftChecks.cs','CredentialChecks.cs','ResourceChecks.cs']
items=''.join(f'<Compile Include="{p}" />' for p in sources)
items+=''.join(f'<Reference Include="{escape(p.stem)}"><HintPath>{escape(str(p))}</HintPath></Reference>' for p in refs)
(d/'Review.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>Iris.Core.Tests</AssemblyName><EnableDefaultCompileItems>false</EnableDefaultCompileItems><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup>'+items+'</ItemGroup></Project>',encoding='utf-8')
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
(d/'manifest.json').write_text(json.dumps({'commit':subprocess.check_output(['git','rev-parse','HEAD'],cwd=r,text=True).strip(),'phase':'development binding, not final acceptance','sources':{p:sha(d/p) for p in sources},'dlls':{p.name:sha(p) for p in refs if p.name.startswith('Iris.')}},indent=2),encoding='utf-8')
