"""Create local, synthetic browser and OCR content for actual UI acceptance."""
from pathlib import Path
import hashlib
import json
import subprocess
from PIL import Image, ImageDraw, ImageFont

ROOT = Path('F:/Iris')
SESSION = ROOT / 'artifacts/t040/manual-verification-20261107-3'
DEST = SESSION / 'ui-samples-input'
session = json.loads((SESSION / 'session.json').read_text(encoding='utf-8-sig'))
listed = json.loads(subprocess.check_output(['wsb.exe', 'list', '--raw']).decode('utf-8-sig'))
assert session['SandboxId'] in {item['Id'] for item in listed['WindowsSandboxEnvironments']}
DEST.mkdir(exist_ok=False)
bitmap = Image.new('RGB', (960, 260), 'white')
draw = ImageDraw.Draw(bitmap)
font = ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf', 56)
draw.text((28, 34), 'Iris T040 OCR sample 013', font=font, fill='black')
draw.text((28, 120), 'Read, edit, copy, translate.', font=font, fill='black')
bitmap.save(DEST / 'ocr-sample-013.png')
html = '''<!doctype html>
<html lang="en"><meta charset="utf-8"><title>Iris T040 owned UI scene</title>
<style>body{font:24px "Segoe UI",sans-serif;margin:24px;background:white;color:#111}textarea,input{font:24px "Segoe UI",sans-serif;padding:12px;width:820px}h1{font-size:30px}img{display:block;margin:20px 0;border:1px solid #777}p{margin:14px 0}</style>
<h1>Iris T040 owned UI scene</h1>
<p>Text selection sample</p>
<textarea rows="2" id="selection">Iris T040 selection sample 013</textarea>
<p id="static">Iris T040 static selection sample 013</p>
<img src="ocr-sample-013.png" width="960" height="260" alt="Synthetic OCR sample">
<p>Synthetic protected field; no account or sign-in form</p>
<input type="password" id="protected" value="Iris T040 fake protected sample 013" autocomplete="off">
</html>
'''
(DEST / 'Iris-T040-Owned-Scene.html').write_text(html, encoding='utf-8')
rows = [{'Path': p.name, 'Bytes': p.stat().st_size, 'Sha256': hashlib.sha256(p.read_bytes()).hexdigest()}
        for p in sorted(DEST.iterdir()) if p.is_file()]
(DEST / 'sample-manifest.json').write_text(json.dumps({'Files': rows, 'SyntheticOnly': True, 'NoExternalRequests': True}, indent=2) + '\n', encoding='utf-8')
subprocess.run(['wsb.exe', 'share', '--id', session['SandboxId'], '--host-path', str(DEST),
                '--sandbox-path', r'C:\IrisT040UiSamples', '--raw'], check=True, capture_output=True)
print(json.dumps({'GuestRoot': r'C:\IrisT040UiSamples', 'Files': rows, 'SyntheticOnly': True}, indent=2))
