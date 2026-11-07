from pathlib import Path
import json
import subprocess

root = Path('F:/Iris')
paths = []
for folder in ('session3-diagnostic-repair-20261107-1','native-cli-review-20261108-1'):
    directory = root/'tasks/T-040.evidence'/folder
    for row in json.loads((directory/'binding.json').read_text(encoding='utf-8-sig'))['Files']:
        paths.append(str(directory/row['ArchivePath']))
directory = root/'tasks/T-040.evidence/session3-diagnostic-repair-20261107-1'
for row in json.loads((directory/'original-manifest.json').read_text(encoding='utf-8-sig')):
    paths.append(str(directory/'originals'/row['Path']))
subprocess.run(['git','add','-f','--',*paths],cwd=root,check=True)
print('Staged explicitly bound evidence paths:',len(paths))
