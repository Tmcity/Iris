from pathlib import Path
from xml.sax.saxutils import escape
import hashlib,json,subprocess
root=Path('F:/Iris'); here=Path(__file__).parent
output=root/'tests/Iris.App.Tests/bin/Release/net10.0-windows10.0.19041.0'
refs=[p for p in output.glob('*.dll') if p.stem in ['Iris.Core','Iris.Infrastructure','Iris.Providers','Anthropic','Dapper','Microsoft.Data.Sqlite'] or p.stem.startswith(('Microsoft.Extensions.','Polly','SQLitePCLRaw.'))]
items=''.join(f'<Reference Include="{escape(p.stem)}"><HintPath>{escape(str(p))}</HintPath></Reference>' for p in refs)
(here/'Prototype.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0-windows10.0.19041.0</TargetFramework><OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors><AssemblyName>Iris.T036A.Prototype</AssemblyName></PropertyGroup><ItemGroup><Compile Include="*.cs" />'+items+'</ItemGroup></Project>',encoding='utf-8')
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
(here/'binding.json').write_text(json.dumps({'commit':subprocess.check_output(['git','rev-parse','HEAD'],cwd=root,text=True).strip(),'references':{p.name:sha(p) for p in refs if p.name.startswith('Iris.')}},indent=2))
