from pathlib import Path
from datetime import datetime, timezone
import json
import os
import subprocess
import sys

ROOT = Path('F:/Iris')
phase = sys.argv[1]
if phase not in ('before', 'after'):
    raise ValueError('Expected regression phase before or after')
folder = ROOT / 'tasks/T-040.evidence/history-close-defect-20261108-1'
folder.mkdir(parents=True, exist_ok=True)
command = ['dotnet', 'test', '--project', 'tests/Iris.App.Tests/Iris.App.Tests.csproj', '-c', 'Release',
           '--filter-method', '*Close_IdleRealWindow_UnwindsClosingEventBeforeCleanupAndReopens*', '--no-ansi', '--no-progress']
start = datetime.now(timezone.utc).isoformat()
environment = os.environ.copy()
environment['DOTNET_CLI_UI_LANGUAGE'] = 'en-US'
environment['PYTHONDONTWRITEBYTECODE'] = '1'
with (folder / (phase + '.log')).open('xb') as output:
    result = subprocess.run(command, cwd=ROOT, stdout=output, stderr=subprocess.STDOUT, env=environment)
(folder / (phase + '.command.json')).write_text(json.dumps({'Command': command, 'StartedUtc': start,
     'FinishedUtc': datetime.now(timezone.utc).isoformat(), 'ExitCode': result.returncode}, indent=2) + '\n', encoding='utf-8')
print((folder / (phase + '.log')).read_text(encoding='utf-8-sig'), flush=True)
print('REGRESSION_EXIT_CODE=' + str(result.returncode), flush=True)
raise SystemExit(result.returncode)
