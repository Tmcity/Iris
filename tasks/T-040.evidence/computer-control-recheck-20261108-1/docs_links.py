from pathlib import Path
from html.parser import HTMLParser
import json

class Links(HTMLParser):
    def __init__(self):
        super().__init__(); self.link=None; self.text=[]; self.results=[]
    def handle_starttag(self,tag,attrs):
        if tag=='a': self.link=dict(attrs).get('href'); self.text=[]
    def handle_data(self,data):
        if self.link is not None: self.text.append(data)
    def handle_endtag(self,tag):
        if tag=='a' and self.link is not None:
            text=' '.join(''.join(self.text).split())
            if any(word in (self.link+' '+text).lower() for word in ['computer','windows','troubleshoot','app/features']):
                self.results.append({'href':self.link,'label':text})
            self.link=None;self.text=[]

source=Path('F:/Iris/tasks/T-040.evidence/computer-control-recheck-20261108-1/official-search.html')
parser=Links();parser.feed(source.read_text(encoding='utf-8'))
unique=[]
for row in parser.results:
    if row not in unique: unique.append(row)
print(json.dumps(unique,indent=2))
