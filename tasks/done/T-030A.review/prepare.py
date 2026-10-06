from pathlib import Path
from xml.sax.saxutils import escape
import hashlib,json,subprocess

root=Path('F:/Iris'); directory=Path(__file__).parent
output=root/'tests/Iris.Core.Tests/bin/Release/net10.0'
references=sorted(p for p in output.glob('*.dll') if not p.name.endswith('.Tests.dll'))
assert (output/'Iris.Core.dll').is_file()
xml='\n'.join(f'<Reference Include="{escape(p.stem)}"><HintPath>{escape(str(p))}</HintPath></Reference>' for p in references)
sources=['Program.cs','Boundaries.cs','RegistryChecks.cs','PublicationChecks.cs','FingerprintChecks.cs','OwnershipChecks.cs','CoordinatorChecks.cs']
compile_items=''.join(f'<Compile Include="{p}" />' for p in sources)
(directory/'Review.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>Iris.Core.Tests</AssemblyName><EnableDefaultCompileItems>false</EnableDefaultCompileItems><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup>'+compile_items+xml+'</ItemGroup></Project>',encoding='utf-8')
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
files=list((root/'src/Iris.Core/Ocr').glob('*.cs'))+list((root/'src/Iris.Core/DependencyInjection').glob('*.cs'))+[root/'src/Iris.Core/Translation/ProviderCredentialResolver.cs',root/'src/Iris.App/Hosting/AppBootstrapper.cs',root/'specs/CONTRACTS.cs']
manifest={'commit':subprocess.check_output(['git','rev-parse','HEAD'],cwd=root,text=True).strip(),'trackedDiff':subprocess.check_output(['git','diff','--name-only'],cwd=root,text=True).splitlines(),'sources':{p.relative_to(root).as_posix():sha(p) for p in files},'productDlls':{p.name:sha(p) for p in references if p.name.startswith('Iris.')},'probeSources':{p:sha(directory/p) for p in sources},'scope':'Actual Core Registry through DI; fake external store/factory/provider. No user content. SDK/Host/Coordinator checks must be added separately.'}
(directory/'manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
print('Prepared binding manifest only. No product validation executed.')
