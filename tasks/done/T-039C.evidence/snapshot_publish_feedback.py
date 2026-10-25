from pathlib import Path
import hashlib
import json
import shutil
import subprocess

root = Path(__file__).resolve().parents[3]
evidence = Path(__file__).resolve().parent
destination = evidence / "before-source-feedback-1"
destination.mkdir(exist_ok=False)
records = []
for relative in ["build/publish_release.py", "build/tests/test_publish_release.py", "README.md", "build/release_pipeline.py", "build/release_manifest.py", "tasks/T-039C.coordination/source-feedback-1/README.md"]:
    source = root / relative
    target = destination / (relative.replace("/", "_") + ".txt")
    shutil.copyfile(source, target)
    records.append({"Path": relative, "Snapshot": target.name, "Bytes": source.stat().st_size, "Sha256": hashlib.sha256(source.read_bytes()).hexdigest()})
(destination / "binding.json").write_text(json.dumps({"Head": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root).decode().strip(), "Files": records,
    "Scope": "Preserve current intermediate source before the architect's local inference feedback; no real publishing failure is claimed."}, indent=2) + "\n", encoding="utf-8", newline="\n")
print(json.dumps({"PreservedFiles": len(records)}))
