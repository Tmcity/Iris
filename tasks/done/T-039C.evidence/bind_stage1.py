"""Bind local stage-one candidates and exact source files without self-reference or any publication."""
from pathlib import Path
import hashlib
import json
import subprocess
import sys

evidence = Path(__file__).resolve().parent
root = evidence.parents[2]
sys.path.insert(0, str(root / "build"))
from release_manifest import SOURCE_ROOTS, digest, read_json, require, source_tree, verify_directory

def record(path):
    return {"Path": path.relative_to(root).as_posix(), "Bytes": path.stat().st_size, "Sha256": digest(path)}

selection = read_json(evidence / "active-candidates.json")
target = evidence / "stage1-binding.json"
require(not target.exists(), "Stage-one binding already exists; use a new candidate round")
packages = []
for version in selection["Versions"]:
    directory = root / version["Directory"]
    verified = verify_directory(directory, version["Version"])
    require(verified["SourceCommit"] == selection["SourceCommit"], "Candidates use different source commits")
    packages.append({**verified, "Directory": version["Directory"], "Files": [record(path) for path in sorted(directory.iterdir())]})
entries = []
for row in subprocess.check_output(["git", "ls-tree", "-r", "-z", selection["SourceCommit"], "--", *SOURCE_ROOTS], cwd=root).decode().strip("\0").split("\0"):
    metadata, path = row.split("\t")
    mode, kind, blob = metadata.split()
    require(mode == "100644" and kind == "blob", "Unexpected mode")
    require(subprocess.check_output(["git", "hash-object", "--path", path, path], cwd=root).decode().strip() == blob, "Working source drift")
    entries.append({**record(root / path), "GitBlob": blob})
tree = source_tree(entries)
require(all(item["SourceGitTree"] == tree for item in packages), "Source tree differs from package provenance")
public_parent = "877491e1421197ed6394f53d7f4d6ef57ab22e5e"
for parent in [public_parent, "5077d5000de7304117eb54d3a89a5a23325e5360"]:
    require(subprocess.run(["git", "merge-base", "--is-ancestor", parent, selection["SourceCommit"]], cwd=root).returncode == 0, "History parent not preserved")
require((root / "tasks/current_task.md").read_bytes() == (evidence / "signed-order-stage1.md").read_bytes(), "Active order changed; preserve a new snapshot")
binding = {"Status": "LocalStage1ForArchitectReview_NotPublic_NotComplete", "RemoteWritesPerformed": False,
    "SourceCommit": selection["SourceCommit"], "SourceGitTree": tree, "SourceRoots": SOURCE_ROOTS, "SourceFiles": entries,
    "GitTreeSemantics": "Canonical committed source subset only; excludes tasks/artifacts/reports/binding. Final stage-one report commits may follow this source commit without altering this tree.",
    "ReleasePackages": packages, "PlannedRefs": selection["PlannedRefs"], "Order": record(evidence / "signed-order-stage1.md"),
    "Evidence": [record(path) for path in sorted(evidence.rglob("*")) if path.is_file() and path != target and "__pycache__" not in path.parts
        and not path.name.startswith(("stage1-binding-check", "checkpoint-commit-check"))],
    "Report": record(root / "tasks/T-039C.stage1.md"),
    "Outstanding": ["Architect checkpoint review before first remote write", "Actual tag CI and public asset readback", "Default clean runtime bootstrap with no prefilled cache/preinstall", "Actual production settings UI anonymous upgrade and process/data proof", "Independent C and parent acceptance"],
    "PreservedHistory": [public_parent, "5077d5000de7304117eb54d3a89a5a23325e5360"], "CredentialOrCodexFilesIncluded": False}
target.write_text(json.dumps(binding, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
print(json.dumps({"SourceCommit": binding["SourceCommit"], "SourceGitTree": tree, "SourceFiles": len(entries), "EvidenceFiles": len(binding["Evidence"]), "Packages": len(packages), "BindingSha256": digest(target)}, indent=2))
