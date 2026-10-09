"""Create original-resolution review sheets; carry review only for identical image bytes."""
import hashlib
import json
import argparse
from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[2]
evidence = root / 'tasks/done/T-038.evidence'
parser = argparse.ArgumentParser()
parser.add_argument('--source', default='visual-7')
parser.add_argument('--destination', default='visual-delivery-review')
parser.add_argument('--previous', default='visual-6')
parser.add_argument('--previous-review', default='visual-6-review')
args = parser.parse_args()
source = evidence / args.source
dest = evidence / args.destination
dest.mkdir(exist_ok=True)
old = {x['File']: x for x in json.loads((evidence / args.previous / 'manifest.json').read_text(encoding='utf-8-sig'))}
old_contacts = json.loads((evidence / args.previous_review / 'contacts.json').read_text(encoding='utf-8-sig'))
progress = json.loads((evidence / args.previous_review / 'review-progress.json').read_text(encoding='utf-8-sig'))
seen = {name for i, c in enumerate(old_contacts, 1) if i in progress['reviewed'] for name in c['Images']}
old_carry = evidence / args.previous_review / 'carry.json'
if old_carry.exists():
    seen.update(e['File'] for e in json.loads(old_carry.read_text(encoding='utf-8')))
for name in seen:
    assert hashlib.sha256((evidence / args.previous / name).read_bytes()).hexdigest().upper() == old[name]['Sha256']
manifest = json.loads((source / 'manifest.json').read_text(encoding='utf-8-sig'))
carry, pending = [], []
for entry in manifest:
    name = entry['File']
    digest = hashlib.sha256((source / name).read_bytes()).hexdigest().upper()
    assert digest == entry['Sha256']
    if name in seen and old[name]['Sha256'] == digest:
        carry.append({'File': name, 'Sha256': digest, 'ReviewedAt': args.previous_review})
    else:
        pending.append(entry)
contacts = []
for start in range(0, len(pending), 4):
    entries = pending[start:start + 4]
    originals = [Image.open(source / e['File']).convert('RGBA') for e in entries]
    w, h = max(i.width for i in originals), max(i.height for i in originals)
    sheet = Image.new('RGB', (w * 2, (h + 30) * 2), '#b0b0b0')
    draw = ImageDraw.Draw(sheet)
    for i, (entry, original) in enumerate(zip(entries, originals)):
        x, y = (i % 2) * w, (i // 2) * (h + 30)
        draw.text((x + 6, y + 6), entry['File'], fill='black')
        sheet.paste(original, (x, y + 30), original)
    name = f'contact-{len(contacts) + 1:03}.png'
    sheet.save(dest / name)
    contacts.append({'File': name, 'Images': [e['File'] for e in entries]})
(dest / 'contacts.json').write_text(json.dumps(contacts, indent=2), encoding='utf-8')
(dest / 'carry.json').write_text(json.dumps(carry, indent=2), encoding='utf-8')
print(json.dumps({'FinalImages': len(manifest), 'ExactShaReviewCarry': len(carry), 'PendingImages': len(pending), 'ContactSheets': len(contacts)}))
