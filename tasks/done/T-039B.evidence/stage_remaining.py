from pathlib import Path
import shutil, hashlib, json
e = Path(__file__).resolve().parent
root = e.parents[2]
inputs = root / 'artifacts/t039b/sandbox/69e00710-ca7b-4fb0-83a7-6ceaa37781c2/input'
source = root / 'artifacts/t039b/release-0.2.0-final-3/Iris-0.2.0-Setup.exe'
target = inputs / 'Iris-bad-checksum-final.exe'
with source.open('rb') as src, target.open('xb') as dst:
    shutil.copyfileobj(src, dst)
    src.seek(-1, 2); value = src.read(1)[0]
    dst.seek(-1, 2); dst.write(bytes([value ^ 1]))
script = inputs / 'guest_remaining.ps1'
script.write_text((e / 'guest_remaining.ps1').read_text(encoding='utf-8'), encoding='utf-8-sig')
report = e / 'guest-remaining-final-1'
report.mkdir(exist_ok=False)
(report / 'input-binding.json').write_text(json.dumps({p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in [source, target, script]}, indent=2), encoding='utf-8')
