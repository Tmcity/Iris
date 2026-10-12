"""Acquisition only: official fixed-tag source, never an update test against the internet."""
from pathlib import Path
import hashlib
import json
import urllib.request
import zipfile
evidence = Path(__file__).resolve().parent
work = evidence.parents[2] / 'artifacts/t039a'
url = 'https://codeload.github.com/velopack/velopack/zip/refs/tags/1.2.161'
target = work / 'velopack-1.2.161.zip'
with urllib.request.urlopen(url,timeout=45) as response, target.open('wb') as output:
    while chunk := response.read(1024*1024): output.write(chunk)
with zipfile.ZipFile(target) as z:
    destination = (work/'official-source').resolve()
    for entry in z.infolist():
        assert (destination/entry.filename).resolve().is_relative_to(destination)
    z.extractall(destination)
result={'URL':url,'File':str(target),'Sha256':hashlib.sha256(target.read_bytes()).hexdigest()}
(evidence/'official-source.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
print(json.dumps(result))
