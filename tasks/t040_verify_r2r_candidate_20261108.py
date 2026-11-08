"""Verify the actual package, including ReadyToRun headers and the IL-only control."""
from pathlib import Path
import argparse
import hashlib
import json
import struct
import sys
import zipfile

ROOT = Path('F:/Iris')
sys.path.insert(0, str(ROOT / 'build'))
from release_manifest import verify_directory

def native_header(raw):
    if raw[:2] != b'MZ':
        raise ValueError('Missing DOS header')
    pe = struct.unpack_from('<I', raw, 0x3c)[0]
    if raw[pe:pe + 4] != b'PE\0\0':
        raise ValueError('Missing PE header')
    machine, sections = struct.unpack_from('<HH', raw, pe + 4)
    optional_size = struct.unpack_from('<H', raw, pe + 20)[0]
    optional = pe + 24
    magic = struct.unpack_from('<H', raw, optional)[0]
    if magic not in (0x10b, 0x20b):
        raise ValueError('Unsupported optional header')
    directories = optional + (96 if magic == 0x10b else 112)
    clr_rva, clr_size = struct.unpack_from('<II', raw, directories + 14 * 8)
    section_table = optional + optional_size

    def offset(rva):
        for index in range(sections):
            section = section_table + index * 40
            virtual_size, start, size, file_offset = struct.unpack_from('<IIII', raw, section + 8)
            if start <= rva < start + max(virtual_size, size):
                result = file_offset + rva - start
                if result >= len(raw):
                    raise ValueError('RVA outside file data')
                return result
        raise ValueError('RVA outside image sections')

    if not clr_rva or clr_size < 72:
        raise ValueError('Managed CLI header is missing')
    clr = offset(clr_rva)
    rva, size = struct.unpack_from('<II', raw, clr + 64)
    signature = raw[offset(rva):offset(rva) + 4] if rva else b''
    return {'Machine': hex(machine), 'NativeHeaderRva': rva, 'NativeHeaderSize': size,
            'ReadyToRunSignature': signature == b'RTR\0', 'Bytes': len(raw),
            'Sha256': hashlib.sha256(raw).hexdigest()}

parser = argparse.ArgumentParser()
parser.add_argument('--candidate', type=Path, required=True)
parser.add_argument('--validation', type=Path, required=True)
parser.add_argument('--source', required=True)
parser.add_argument('--version', default='0.2.11')
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
candidate = args.candidate.resolve(strict=True)
validation = args.validation.resolve(strict=True)
summary = json.loads((validation / 'validation-summary.json').read_text(encoding='utf-8-sig'))
provenance = json.loads((candidate / 'release-provenance.json').read_text(encoding='utf-8-sig'))
assert summary['SourceCommit'] == args.source == provenance['SourceCommit']
assert summary['BuildWarnings'] == 0 and summary['BuildErrors'] == 0
assert summary['FullTests']['Failed'] == 0
assets = verify_directory(candidate, args.version)
required = ('Iris.App.dll', 'Iris.Core.dll', 'Iris.Infrastructure.dll', 'Iris.Platform.Windows.dll', 'Iris.Providers.dll', 'Wpf.Ui.dll')
headers = {}
with zipfile.ZipFile(candidate / ('Iris-' + args.version + '-full.nupkg')) as package:
    for name in required:
        info = native_header(package.read('lib/app/' + name))
        assert info['ReadyToRunSignature'] and info['Machine'] == '0x8664', name
        headers[name] = info
baseline = ROOT / 'artifacts/t040/memory-fix-local-candidate-20261107-1/Iris-0.2.11-full.nupkg'
with zipfile.ZipFile(baseline) as package:
    control = native_header(package.read('lib/app/Iris.App.dll'))
    assert not control['ReadyToRunSignature'] and control['NativeHeaderRva'] == 0
manifest = json.loads((candidate / 'payload-manifest.json').read_text(encoding='utf-8-sig'))
assert len(manifest) == 97
result = {
    'Version': args.version, 'SourceCommit': args.source, 'LocalCandidate': True,
    'PublicWritePerformed': False, 'FormalNfrResult': False,
    'Assets': assets, 'FullTests': summary['FullTests'], 'PayloadFiles': len(manifest),
    'ReadyToRunHeaders': headers, 'OriginalIlOnlyControl': control,
    'InstallerBytes': (candidate / ('Iris-' + args.version + '-Setup.exe')).stat().st_size,
    'InstallerSha256': hashlib.sha256((candidate / ('Iris-' + args.version + '-Setup.exe')).read_bytes()).hexdigest(),
    'ActualInstalledCandidateAndPostGcMemoryPending': True,
}
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
print(json.dumps({key: value for key, value in result.items() if key not in ('ReadyToRunHeaders', 'OriginalIlOnlyControl')}, indent=2))
