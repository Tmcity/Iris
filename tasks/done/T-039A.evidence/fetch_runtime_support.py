from pathlib import Path
import urllib.request,hashlib,json
e=Path(__file__).resolve().parent
url='https://raw.githubusercontent.com/dotnet/core/main/release-notes/10.0/supported-os.md'
with urllib.request.urlopen(url,timeout=30) as response: data=response.read()
p=e/'official-docs/dotnet10-supported-os.md';p.write_bytes(data)
(e/'official-docs/dotnet10-supported-os.source.json').write_text(json.dumps({'URL':url,'Sha256':hashlib.sha256(data).hexdigest()},indent=2),encoding='utf-8')
print(data.decode())
