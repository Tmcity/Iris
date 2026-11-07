from pathlib import Path
from html.parser import HTMLParser
import argparse
import datetime
import hashlib
import json
import sys
import urllib.error
import urllib.parse
import urllib.request

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

DEST = Path('F:/Iris/tasks/T-040.evidence/computer-control-recheck-20261108-1')
DEST.mkdir(parents=True, exist_ok=True)
parser = argparse.ArgumentParser()
parser.add_argument('url')
parser.add_argument('--label', required=True)
args = parser.parse_args()
allowed = {'developers.openai.com', 'platform.openai.com', 'learn.chatgpt.com'}
assert urllib.parse.urlparse(args.url).hostname in allowed

class OfficialRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        if urllib.parse.urlparse(newurl).hostname not in allowed:
            raise RuntimeError('Refusing non-official redirect')
        return super().redirect_request(req, fp, code, msg, headers, newurl)

class Text(HTMLParser):
    def __init__(self):
        super().__init__(); self.skip = 0; self.parts = []
    def handle_starttag(self, tag, attrs):
        if tag in ('script','style'): self.skip += 1
        if tag in ('p','div','li','h1','h2','h3','h4','section','br'): self.parts.append('\n')
    def handle_endtag(self, tag):
        if tag in ('script','style'): self.skip = max(0,self.skip-1)
    def handle_data(self, data):
        if not self.skip: self.parts.append(data)

started = datetime.datetime.now(datetime.timezone.utc).isoformat()
request = urllib.request.Request(args.url, headers={'User-Agent':'Mozilla/5.0'})
try:
    response = urllib.request.build_opener(OfficialRedirect).open(request, timeout=20)
    status = response.status; final = response.url; body = response.read()
except urllib.error.HTTPError as error:
    status = error.code; final = error.url; body = error.read()
base = DEST / args.label
base.with_suffix('.html').write_bytes(body)
page = Text(); page.feed(body.decode('utf-8', errors='replace'))
lines = [line.strip() for line in ''.join(page.parts).splitlines() if line.strip()]
text = '\n'.join(lines)
base.with_suffix('.txt').write_text(text, encoding='utf-8')
record = {'StartedUtc':started, 'FinishedUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
          'RequestedUrl':args.url, 'FinalUrl':final, 'Status':status,'Bytes':len(body),'Sha256':hashlib.sha256(body).hexdigest()}
base.with_suffix('.json').write_text(json.dumps(record,indent=2)+'\n',encoding='utf-8')
print(json.dumps(record,indent=2))
for index,line in enumerate(lines):
    if any(word in line.lower() for word in ['computer use','computer-use','windows','troubleshoot','restart']):
        print('\n'.join(lines[max(0,index-1):min(len(lines),index+4)]))
