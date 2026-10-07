from pathlib import Path
import urllib.request,hashlib,json
here=Path(__file__).parent/'runtime-exact'
opener=urllib.request.build_opener(urllib.request.ProxyHandler({}))
for item in json.loads((here/'manifest.json').read_text()):
    with opener.open(item['url'],timeout=30) as response: data=response.read()
    assert hashlib.sha256(data).hexdigest()==item['sha256']
    (here/item['file']).write_bytes(data)
print('Exact runtime sources verified')
