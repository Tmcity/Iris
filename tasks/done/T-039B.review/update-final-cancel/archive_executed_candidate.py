"""Archive already executed historical probe logs without rebuilding or rerunning."""
from pathlib import Path
import hashlib
import json
import shutil

ROOT = Path(__file__).resolve().parents[4]
HERE = Path(__file__).resolve().parent
WORK = ROOT / "artifacts/t039b/independent-update-review-1"
OUT = HERE / "executed-candidate-1"
OUT.mkdir(exist_ok=False)
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
binding = json.loads((WORK / "binding.json").read_text("utf-8-sig"))
candidate = json.loads((ROOT / "tasks/done/T-039B.evidence/review-final-candidate-1.json").read_text("utf-8-sig"))
comparisons = []
for item in binding["assemblies"]:
    frozen = Path(item["copy"])
    delivered = Path(candidate["ArtifactDirectory"]) / "App" / frozen.name
    comparisons.append(dict(name=frozen.name, expectedSha256=item["sha256"], probeFrozenSha256=sha(frozen), candidateSha256=sha(delivered)))
    assert sha(frozen) == sha(delivered) == item["sha256"]
for item in binding["probeSources"]:
    assert sha(WORK / item["name"]) == item["sha256"]
commands = []
for label in ["build-1", "late-cancel-1", "process-lock-1"]:
    record = json.loads((WORK / f"{label}.command.json").read_text("utf-8-sig"))
    assert record["exitCode"] == 0
    assert sha(WORK / f"{label}.log") == record["logSha256"]
    commands.append(record)
for label in ["late-cancel-1", "process-lock-1"]:
    result = json.loads((WORK / f"{label}.log").read_text("utf-8-sig"))
    assert result["Passed"] and result["ProbeError"] is None and result["AllObservedWorkJoined"] if label == "late-cancel-1" else result["Passed"] and result["ProbeError"] is None and result["AllWorkJoined"]
names = ["binding.json", "Review.csproj", "Program.cs", "CrossProcessSdkLock.cs", "NuGet.Config", "run_review.py"]
names += [f"{label}{suffix}" for label in ["build-1", "late-cancel-1", "process-lock-1"] for suffix in [".log", ".command.json"]]
files = []
for name in names:
    shutil.copyfile(WORK / name, OUT / name)
    assert sha(WORK / name) == sha(OUT / name)
    files.append(dict(name=name, bytes=(OUT / name).stat().st_size, sha256=sha(OUT / name)))
source_differences = [item["Path"] for item in candidate["Sources"] if not (ROOT / item["Path"]).is_file() or sha(ROOT / item["Path"]) != item["Sha256"]]
with (OUT / "archive-verification.json").open("x", encoding="utf-8") as output:
    json.dump(dict(semantics="Executed on 2026-10-15; archived on 2026-10-25. No compilation or probe execution occurred during archival. Preparation binding's probeCompiledOrRun=false predates execution; actual execution is proved by raw command/log records. Historical candidate, not final R3/R4 evidence.", assemblies=comparisons, commands=commands, sourceSnapshotCount=len(candidate["Sources"]), currentSourceDifferences=source_differences, archivedFiles=files), output, indent=2)
print(json.dumps(dict(archivedFiles=len(files), matchedFrozenAssemblies=len(comparisons), preservedSuccessfulCommands=len(commands), currentSourceDifferences=len(source_differences))))
