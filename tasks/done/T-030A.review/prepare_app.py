from pathlib import Path
from xml.sax.saxutils import escape
import json,hashlib,subprocess
root=Path('F:/Iris'); directory=Path(__file__).parent
output=root/'tests/Iris.App.Tests/bin/Release/net10.0-windows10.0.19041.0'
references=sorted(p for p in output.glob('*.dll') if not p.name.endswith('.Tests.dll'))
xml=''.join(f'<Reference Include="{escape(p.stem)}"><HintPath>{escape(str(p))}</HintPath></Reference>' for p in references)
sources=['AppProgram.cs','AppChecks.cs','RegistryChecks.cs','Boundaries.cs','WindowsChecks.cs','OcrBoundary.cs','BaiduRegistryChecks.cs','HostChecks.cs']
items=''.join(f'<Compile Include="{p}" />' for p in sources)+'<None Include="fixtures/hello-123.png" CopyToOutputDirectory="PreserveNewest" />'
(directory/'AppReview.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0-windows10.0.19041.0</TargetFramework><UseWPF>true</UseWPF><OutputType>Exe</OutputType><AssemblyName>Iris.App.Tests</AssemblyName><EnableDefaultCompileItems>false</EnableDefaultCompileItems><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup>'+items+xml+'</ItemGroup></Project>',encoding='utf-8')
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
(directory/'app-manifest.json').write_text(json.dumps({'commit':subprocess.check_output(['git','rev-parse','HEAD'],cwd=root,text=True).strip(),'sources':{p.relative_to(root).as_posix():sha(p) for p in [root/'src/Iris.App/Hosting/AppBootstrapper.cs',root/'src/Iris.Core/Ocr/OcrProviderRegistry.cs',root/'src/Iris.Core/Ocr/OcrRegistrySnapshot.cs']},'probeSources':{p:sha(directory/p) for p in sources},'productDlls':{p.name:sha(p) for p in references if p.name.startswith('Iris.')},'fixtures':{p.name:sha(p) for p in (directory/'fixtures').iterdir()},'phase':'Preparation binding only, no execution implied'},indent=2),encoding='utf-8')
print('Prepared actual App DLL binding; no checks executed.')
