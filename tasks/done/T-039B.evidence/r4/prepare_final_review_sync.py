"""Preserve the prior engineer report/binding, then retain runtime binaries only in artifacts."""
from pathlib import Path
import hashlib
import json
import shutil
from inspect_delivery import file_record, json_read, sha

evidence = Path(__file__).resolve().parent
root = evidence.parents[3]
snapshot = evidence / "before-final-review-sync-1"
snapshot.mkdir(exist_ok=False)
records = []
for path in [root / "tasks/done/T-039B.md", evidence.parent / "delivery-binding.json", evidence / "PROGRESS.md",
    evidence / "write_report.py", evidence / "finalize_binding_r4.py", evidence / "stage_delivery.py"]:
    target = snapshot / (path.name + ".txt")
    shutil.copyfile(path, target)
    records.append({"Original": file_record(path), "Snapshot": file_record(target)})
(snapshot / "binding.json").write_text(json.dumps(records, indent=2) + "\n", encoding="utf-8", newline="\n")
preserved_root = root / "artifacts/t039b/r4-runtime-cache-preserved-1"
preserved_root.mkdir(exist_ok=False)
relocations = []
duplicates = []
for label, subdirectory in [("guest-install-r4-6", "runtime-prefetch"), ("guest-update-r4-1", "update-r4/runtime-prefetch")]:
    directory = evidence / label
    intent = json_read(directory / "intent.json")
    duplicate = directory / "output" / subdirectory / "windowsdesktop-runtime-10.0.13-win-x64.exe"
    original = Path(intent["WritableOutput"]) / subdirectory / duplicate.name
    destination = preserved_root / label / duplicate.name
    assert duplicate.resolve().is_relative_to((directory / "output").resolve())
    assert original.resolve().is_relative_to((root / "artifacts/t039b/sandbox").resolve())
    assert destination.resolve().is_relative_to((root / "artifacts").resolve())
    assert original.stat().st_size == duplicate.stat().st_size and sha(original) == sha(duplicate)
    result = json_read(directory / "output" / ("update-r4" if label.startswith("guest-update") else "") / "result.json")
    signature = next(item["Value"] for item in result["Checks"] if item["Name"] == "runtime-prefetch-signature")
    with original.open("rb") as stream:
        assert hashlib.file_digest(stream, "sha512").hexdigest() == signature["Sha512"]
    destination.parent.mkdir()
    shutil.copyfile(original, destination)
    assert destination.stat().st_size == original.stat().st_size and sha(destination) == sha(original)
    relocations.append({"PreviousEvidenceDuplicate": file_record(duplicate), "OriginalArtifactOutputRetained": file_record(original),
        "PreservedArtifactCopy": file_record(destination), "Sha512": signature["Sha512"], "AllThreeByteSequencesMatchedBeforeRemoval": True,
        "Disposition": "Only the duplicate runtime exe under tasks evidence is removed; original artifact output, stable artifact copy, raw logs, metadata and screenshots remain."})
    duplicates.append(duplicate)
for duplicate in duplicates:
    duplicate.unlink()
assert not [p for p in evidence.rglob("*") if p.is_file() and p.suffix.lower() in {".exe", ".dll", ".pdb", ".nupkg", ".zip", ".dmp"}]
(evidence / "runtime-binary-relocation-1.json").write_text(json.dumps({"Files": relocations,
    "InstallerDependencyPackageOrDumpBinariesUnderR4Evidence": 0, "NoLogsOrRawResultsRewritten": True}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
print(json.dumps({"RuntimeCopiesVerifiedAndRetainedInArtifacts": len(relocations), "BinariesUnderR4Evidence": 0}))
