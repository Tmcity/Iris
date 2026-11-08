"""Fetch only explicitly selected public Microsoft keyboard-routing references."""
from pathlib import Path
from datetime import datetime, timezone
from concurrent.futures import ThreadPoolExecutor
import hashlib
import html
import json
import re
import urllib.request

OUT = Path('F:/Iris/tasks/T-040.evidence/computer-control-hotkey-recheck-20261108-1/official-keyboard-docs')
URLS = [
    ('sandbox-overview', 'https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-overview'),
    ('remote-shortcuts', 'https://learn.microsoft.com/en-us/windows/win32/termserv/terminal-services-shortcut-keys'),
    ('rdp-keyboardhookmode', 'https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpclientadvancedsettings-keyboardhookmode'),
]
OUT.mkdir(parents=True, exist_ok=True)

def fetch(pair):
    label, url = pair
    record = {'Label': label, 'RequestedUrl': url, 'StartedUtc': datetime.now(timezone.utc).isoformat()}
    try:
        with urllib.request.urlopen(urllib.request.Request(url, headers={'User-Agent': 'Iris-T040-readonly-reference'}), timeout=15) as response:
            raw = response.read()
            record.update({'Status': response.status, 'FinalUrl': response.url, 'Bytes': len(raw),
                           'Sha256': hashlib.sha256(raw).hexdigest()})
        dest = OUT / (label + '.html')
        if dest.exists():
            raise RuntimeError('Preserve earlier reference bytes')
        dest.write_bytes(raw)
        source = raw.decode('utf-8', errors='replace')
        main = re.search(r'<main\b[^>]*>([\s\S]*?)</main>', source, re.I)
        if main:
            source = main.group(1)
        source = re.sub(r'<script\b[^>]*>[\s\S]*?</script>', '', source, flags=re.I)
        text = html.unescape(re.sub(r'<[^>]+>', ' ', source))
        text = re.sub(r'\s+', ' ', text)
        record['KeyboardExcerpts'] = [text[max(0, m.start() - 180):m.end() + 420]
                                      for m in re.finditer(r'KeyboardHookMode|full.screen|keyboard shortcut|CTRL\+ALT\+BREAK|hooked', text, re.I)][:15]
        record['PlainTextPath'] = str(OUT / (label + '.txt'))
        (OUT / (label + '.txt')).write_text(text + '\n', encoding='utf-8')
    except Exception as error:
        record['Error'] = type(error).__name__ + ': ' + str(error)
    record['FinishedUtc'] = datetime.now(timezone.utc).isoformat()
    return record

with ThreadPoolExecutor(max_workers=3) as pool:
    rows = list(pool.map(fetch, URLS))
dest = OUT / 'fetch-record.json'
if dest.exists():
    raise RuntimeError('Do not overwrite prior official reference observations')
dest.write_text(json.dumps(rows, indent=2) + '\n', encoding='utf-8')
print(json.dumps(rows, indent=2))
