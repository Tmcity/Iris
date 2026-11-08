"""Keep exact before/after regression command output for the progress defect."""
from datetime import datetime, timezone
from pathlib import Path
import json
import subprocess
import sys

root = Path('F:/Iris')
phase = sys.argv[1]
assert phase in ('before', 'before-corrected', 'before-runtime', 'after', 'after-corrected')
out = root / 'tasks/T-040.evidence/hotkey-initial-progress-defect-20261108-1'
out.mkdir(parents=True, exist_ok=True)
assert not (out / (phase + '.command.json')).exists(), 'Preserve original regression run'
command = ['dotnet', 'test', '--project', 'tests/Iris.App.Tests/Iris.App.Tests.csproj', '-c', 'Release',
           '--filter-method', '*InitialCaptureProgress*', '--report-xunit-xml', '--results-directory', str(out / (phase + '-reports')),
           '--no-ansi', '--no-progress']
started = datetime.now(timezone.utc).isoformat()
result = subprocess.run(command, cwd=root, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
(out / (phase + '.log')).write_bytes(result.stdout)
(out / (phase + '.command.json')).write_text(json.dumps({'Command': command, 'StartedUtc': started,
    'FinishedUtc': datetime.now(timezone.utc).isoformat(), 'ExitCode': result.returncode}, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'Phase': phase, 'ExitCode': result.returncode, 'Output': str(out / (phase + '.log'))}))
sys.exit(result.returncode)
