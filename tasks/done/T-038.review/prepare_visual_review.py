import argparse
import hashlib
import json
import shutil
import textwrap
from collections import defaultdict
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

parser = argparse.ArgumentParser()
parser.add_argument('source', type=Path)
parser.add_argument('destination', type=Path)
parser.add_argument('--previous', type=Path)
args = parser.parse_args()
raw = args.destination / 'raw'
sheets = args.destination / 'sheets'
raw.mkdir(parents=True, exist_ok=True)
sheets.mkdir(parents=True, exist_ok=True)
entries = json.loads((args.source / 'manifest.json').read_text(encoding='utf-8-sig'))
previous = {}
if args.previous:
    previous = {e['File']: e['Sha256'].lower() for e in json.loads((args.previous / 'manifest.json').read_text(encoding='utf-8-sig'))}
groups = defaultdict(list)
bindings = []
shutil.copyfile(args.source / 'manifest.json', raw / 'manifest.json')
for entry in entries:
    name = entry['File']
    data = (args.source / name).read_bytes()
    digest = hashlib.sha256(data).hexdigest()
    assert digest == entry['Sha256'].lower(), name
    (raw / name).write_bytes(data)
    with Image.open(raw / name) as source_image:
        assert source_image.size == (entry['Width'], entry['Height']), name
    bindings.append({'File': name, 'Sha256': digest, 'Width': entry['Width'], 'Height': entry['Height'], 'Unchanged': previous.get(name) == digest})
    groups[name[:-4].split('-', 4)[3] + '-' + name[:-4].split('-', 4)[4]].append(entry)

font = ImageFont.truetype(r'C:\Windows\Fonts\consola.ttf', 14)
sheet_bindings = []
for number, (key, group) in enumerate(sorted(groups.items()), 1):
    if args.previous and all(previous.get(e['File']) == e['Sha256'].lower() for e in group):
        continue
    width = max(e['Width'] for e in group)
    height = max(e['Height'] for e in group)
    label_height = 64
    sheet = Image.new('RGB', (width * 2 + 24, (height + label_height) * 2 + 24), '#c3c3c3')
    draw = ImageDraw.Draw(sheet)
    for entry in group:
        column = 0 if entry['File'].startswith('zh-CN') else 1
        row = 0 if '-light-' in entry['File'] else 1
        x = 8 + column * (width + 8)
        y = 8 + row * (height + label_height + 8)
        title = '\n'.join(textwrap.wrap(entry['File'], width=max(16, width // 9)))
        draw.multiline_text((x, y + 4), title, font=font, fill='black', spacing=2)
        with Image.open(raw / entry['File']) as original:
            sheet.paste(original.convert('RGB'), (x, y + label_height))
    filename = f'sheet-{number:03d}-{key}.png'
    sheet.save(sheets / filename)
    sheet_bindings.append({'Sheet': filename, 'Sha256': hashlib.sha256((sheets / filename).read_bytes()).hexdigest(), 'Files': [e['File'] for e in group]})

result = {'Source': str(args.source), 'ManifestSha256': hashlib.sha256((raw / 'manifest.json').read_bytes()).hexdigest(), 'Previous': str(args.previous) if args.previous else None, 'Images': bindings, 'Sheets': sheet_bindings}
(args.destination / 'binding.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps({'images': len(entries), 'unchanged': sum(e['Unchanged'] for e in bindings), 'newOrChanged': sum(not e['Unchanged'] for e in bindings), 'sheets': len(sheet_bindings), 'directory': str(args.destination)}))
print('\n'.join(e['Sheet'] for e in sheet_bindings))
