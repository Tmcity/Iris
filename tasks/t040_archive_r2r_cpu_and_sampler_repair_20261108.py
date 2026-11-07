"""Bind bounded CPU comparisons, serializer failures, and actual process recovery."""
from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json
import re
import shutil
import statistics
import subprocess

ROOT = Path('F:/Iris')
SESSION = ROOT / 'artifacts/t040/manual-verification-20261107-3'
OUT = SESSION / 'output'
EVIDENCE = ROOT / 'tasks/T-040.evidence'

def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))

def copy(source, destination):
    destination.parent.mkdir(parents=True, exist_ok=True)
    if source.is_dir():
        shutil.copytree(source, destination)
    else:
        shutil.copy2(source, destination)

def bind(directory):
    rows = [{'Path': file.relative_to(directory).as_posix(), 'Bytes': file.stat().st_size,
             'Sha256': hashlib.sha256(file.read_bytes()).hexdigest()}
            for file in sorted(directory.rglob('*')) if file.is_file() and file.name != 'binding.json']
    (directory / 'binding.json').write_text(json.dumps({'Files': rows, 'FormalNfrResult': False}, indent=2) + '\n', encoding='utf-8')
    for row in rows:
        assert hashlib.sha256((directory / row['Path']).read_bytes()).hexdigest() == row['Sha256']
    return len(rows)

repair = EVIDENCE / 'cpu-json-stall-repair-20261108-1'
for pattern in ('cpu-comparison-stall-*', 'cpu-worker-recovery-*', 'plain-log-json-validation-*', 'shared-log-reader-validation-*'):
    for directory in sorted(OUT.glob(pattern)):
        copy(directory, repair / 'originals' / directory.name)
for file in OUT.glob('metrics-worker-stack-*.json'):
    copy(file, repair / 'originals' / file.name)
failed = OUT / 'r2r-cpu-20261107T1922127330549Z'
failed_candidates = [directory for directory in OUT.glob('r2r-cpu-*')
                     if read(directory / 'state.json').get('Failure')]
for directory in failed_candidates:
    copy(directory, repair / 'originals' / directory.name)
copy(ROOT / 'artifacts/t040/toolsource/metrics-worker-stack-20261108-1/Program.cs', repair / 'tool-source/metrics-worker-stack/Program.cs')
copy(ROOT / 'artifacts/t040/toolsource/metrics-worker-stack-20261108-1/MetricsWorkerStack.csproj', repair / 'tool-source/metrics-worker-stack/MetricsWorkerStack.csproj')
for name in ('t040_compare_r2r_with_cpu.ps1', 't040_start_r2r_cpu_comparison.ps1',
             't040_capture_stalled_cpu_comparison_readonly.ps1', 't040_read_metrics_worker_stack.ps1',
             't040_recover_runaway_metrics_worker.ps1', 't040_json_line_repro.ps1',
             't040_validate_plain_log_json_ps51.ps1', 't040_validate_shared_log_reader_ps51.ps1',
             't040_recover_completed_cpu_comparison.ps1', 't040_verify_completed_r2r_cpu_readonly.ps1',
             't040_archive_r2r_cpu_and_sampler_repair_20261108.py'):
    copy(ROOT / 'tasks' / name, repair / 'repaired-tool-source' / name)
plain = read(sorted(OUT.glob('plain-log-json-validation-*'))[-1] / 'validation.json')
assert plain['Cases'][0]['WatchdogTerminated'] and not plain['Cases'][0]['State']['SerializationCompleted']
assert plain['Cases'][1]['ExitCode'] == 0 and plain['Cases'][1]['State']['SerializationCompleted']
shared = read(sorted(OUT.glob('shared-log-reader-validation-*'))[-1] / 'validation.json')
assert shared['OriginalReadAllLinesFailure'] == 'System.IO.IOException'
assert shared['SharedReaderTextMatches'] and shared['Properties'] == ['Length']
recovery = read(sorted(OUT.glob('cpu-worker-recovery-*'))[-1] / 'recovery.json')
assert recovery['WorkerExitCode'] == -1 and recovery['DiagnosticPrimaryExitCode'] == 0 and not recovery['ProductKilled']
repair_result = {'ReviewedUtc': datetime.now(timezone.utc).isoformat(),
                 'PowerShellVersion': plain['Cases'][1]['State']['PowerShellVersion'],
                 'AnnotatedStringSerializationExceededFourSecondWatchdog': True,
                 'PlainStringSerializationMs': plain['Cases'][1]['State']['SerializationMs'],
                 'SharedStringSerializationMs': shared['SerializationMs'],
                 'ReadAllLinesSharingFailureReproduced': True,
                 'FinalReaderUsesReadWriteAndDeleteSharing': True,
                 'OriginalRunawayWorkerTerminated': True, 'RunawayWorkerExitCode': -1,
                 'DiagnosticPrimaryExitedNormally': True,
                 'NoProductGcOrWorkingSetTrimRequested': True,
                 'MemoryPressureFromFailedCollectorExcludedFromFormalNfr': True,
                 'FormalNfrResult': False, 'PublicWritePerformed': False}
(repair / 'review.json').write_text(json.dumps(repair_result, indent=2) + '\n', encoding='utf-8')
repair_count = bind(repair)

run = sorted(OUT.glob('r2r-cpu-*'))[-1]
state = read(run / 'state.json')
terminal = read(run / 'independent-terminal-state.json')
assert state['Completed'] and state['Failure'] is None and state['InstalledSettingsAndSecretsUnchanged']
assert terminal['WorkerAbsent'] and terminal['DiagnosticPrimariesAbsent'] and terminal['RestoredPid'] == state['RestoredPid']
assert len(state['Runs']) == 4 and [row['Kind'] for row in state['Runs']] == ['baseline', 'r2r', 'r2r', 'baseline']
assert all(row['ExitCode'] == 0 and row['StartupCpuConditionProved'] for row in state['Runs'])
dest = EVIDENCE / 'r2r-cpu-comparison-review-20261108-1'
dest.mkdir(exist_ok=False)
copy(run, dest / 'originals' / run.name)
for name in ('t040_compare_r2r_with_cpu.ps1', 't040_start_r2r_cpu_comparison.ps1', 't040_verify_completed_r2r_cpu_readonly.ps1',
             't040_archive_r2r_cpu_and_sampler_repair_20261108.py'):
    copy(ROOT / 'tasks' / name, dest / 'tool-source' / name)
rows = []
for number, record in enumerate(state['Runs'], 1):
    line = record['ReadyLogLine'].strip()
    timestamp = re.match(r'(\d{4}-\d{2}-\d{2}) (\d{2}:\d{2}:\d{2}\.\d+) ([+-]\d{2}:\d{2})', line)
    assert timestamp is not None
    ready = datetime.fromisoformat(timestamp[1] + 'T' + timestamp[2] + timestamp[3]).astimezone(timezone.utc)
    created = datetime.fromisoformat(record['CreatedUtc'].replace('Z', '+00:00'))
    reported = re.search(r'AppBootstrapper\).*?\s([0-9.]+) ms', line)
    assert reported is not None
    peaks = [sample['SystemCpuPercentage'] for sample in record['CpuSamples']]
    assert peaks and max(peaks) < 30
    phases = {}
    for name in ('startup-prehost.txt', 'startup-phases.txt'):
        for text in (run / f"{number:02d}-{record['Kind']}" / name).read_text(encoding='utf-8-sig').splitlines():
            key, value = text.split('=')
            phases[key] = float(value)
    rows.append({'Kind': record['Kind'], 'Pid': record['Pid'], 'BootstrapReportedMs': float(reported[1]),
                 'CreationToReadyMs': (ready - created).total_seconds() * 1000,
                 'StartupCpuPeakPercentage': max(peaks), 'StartupCpuSamples': len(peaks),
                 'WorkingSetMiB': record['WorkingSetBytes'] / 1048576, 'PrivateBytesMiB': record['PrivateBytes'] / 1048576,
                 'ExitCode': record['ExitCode'], 'PhasesMs': phases})
medians = {kind: {field: statistics.median(row[field] for row in rows if row['Kind'] == kind)
                  for field in ('BootstrapReportedMs', 'CreationToReadyMs', 'WorkingSetMiB', 'PrivateBytesMiB')}
           for kind in ('baseline', 'r2r')}
result = {'ReviewedUtc': datetime.now(timezone.utc).isoformat(), 'DiagnosticOnly': True, 'FormalNfrResult': False,
          'Runs': rows, 'Medians': medians, 'CpuConditionProvedForAllFourRuns': True,
          'CreationToReadyReductionMs': medians['baseline']['CreationToReadyMs'] - medians['r2r']['CreationToReadyMs'],
          'WorkingSetIncreaseMiB': medians['r2r']['WorkingSetMiB'] - medians['baseline']['WorkingSetMiB'],
          'PrivateBytesIncreaseMiB': medians['r2r']['PrivateBytesMiB'] - medians['baseline']['PrivateBytesMiB'],
          'InstalledSettingsAndSecretsUnchanged': True, 'AllPrimariesExitedNormally': True,
          'WorkerActuallyAbsent': True, 'RestoredInstalledPid': terminal['RestoredPid'],
          'PostFullGcSteadyMemoryProved': False, 'ProductSourceChanged': False, 'NewCandidateCreated': False,
          'PublicWritePerformed': False}
manifests = {}
for kind, folder in (('baseline', 'startup-diagnostic-prehost'), ('r2r', 'startup-diagnostic-r2r')):
    directory = SESSION / 'cli-input/tools' / folder
    manifests[kind] = [{'Path': file.relative_to(directory).as_posix(), 'Bytes': file.stat().st_size,
                        'Sha256': hashlib.sha256(file.read_bytes()).hexdigest()}
                       for file in sorted(directory.rglob('*')) if file.is_file()]
(dest / 'diagnostic-binary-manifests.json').write_text(json.dumps(manifests, indent=2) + '\n', encoding='utf-8')
(dest / 'review.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
comparison_count = bind(dest)
assert subprocess.check_output(['git', 'diff', '861076b', 'HEAD', '--', 'src', 'tests', 'build'], cwd=ROOT) == b''
print(json.dumps({'RepairBoundFiles': repair_count, 'ComparisonBoundFiles': comparison_count,
                  **{key: value for key, value in result.items() if key != 'Runs'}}, indent=2))
