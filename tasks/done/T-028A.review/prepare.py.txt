from pathlib import Path
from xml.sax.saxutils import escape
import hashlib
import json
import subprocess

root = Path('F:/Iris')
directory = Path(__file__).parent
output = root / 'tests/Iris.App.Tests/bin/Release/net10.0-windows10.0.19041.0'
references = sorted(p for p in output.glob('*.dll') if not p.name.endswith('.Tests.dll'))
for name in ['Iris.Core.dll', 'Iris.Platform.Windows.dll', 'Iris.App.dll']:
    assert (output / name).is_file(), name
xml = '\n'.join(f'<Reference Include="{escape(p.stem)}"><HintPath>{escape(str(p))}</HintPath></Reference>' for p in references)
(directory / 'Review.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0-windows10.0.19041.0</TargetFramework><UseWPF>true</UseWPF><OutputType>Exe</OutputType><AssemblyName>Iris.App.Tests</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup>''' + xml + '</ItemGroup></Project>', encoding='utf-8')
sources = list((root / 'src/Iris.Platform.Windows/Imaging').glob('*.cs')) + [
    root / 'src/Iris.App/Hosting/AppBootstrapper.cs',
    root / 'src/Iris.Platform.Windows/DependencyInjection/PlatformWindowsServiceCollectionExtensions.cs',
    root / 'src/Iris.Core/Imaging/ImageData.cs',
    root / 'specs/CONTRACTS.cs',
]
manifest = {
    'commit': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip(),
    'trackedWorktreeDiff': subprocess.check_output(['git', 'diff', '--name-only'], cwd=root, text=True).splitlines(),
    'sources': {p.relative_to(root).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest() for p in sources},
    'productDlls': {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in references if p.name.startswith('Iris.')},
    'probeSources': {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in directory.glob('*.cs')},
    'fixtures': {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in (directory / 'fixtures').iterdir()},
    'boundaries': 'Actual product DLLs; independent public/geometry checks; only external codec failures are replaced. No product state machine copied.',
}
(directory / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
print('Prepared current product references and source/DLL/fixture fingerprints. No product check executed.')
