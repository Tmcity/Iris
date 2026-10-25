"""Stage explicit verified source/evidence scopes; leave architect backlog/handoff and user tooling alone."""
from pathlib import Path
import json
import hashlib
import subprocess
from inspect_delivery import json_read, sha

evidence = Path(__file__).resolve().parent
root = evidence.parents[3]
active = json_read(evidence / "active_round.json")
binding = json_read(evidence.parent / "delivery-binding.json")
source = json_read(evidence / active["SourceBinding"])
assert binding["CompletionStatus"] == "ReadyForIndependentReview" and not binding["RequiredUnfinished"]
assert not (root / "tasks/current_task.md").exists()
assert not subprocess.check_output(["git", "diff", "--cached", "--name-only"], cwd=root)
assert subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root).decode().strip() == source["BaseCommit"]
known_sources = {entry["Path"] for entry in source["SourceFiles"]}
for entry in source["SourceFiles"]:
    assert sha(root / entry["Path"]) == entry["Sha256"], entry["Path"]
changed = subprocess.check_output(["git", "diff", "--name-only", "-z", "--", *source["Roots"]], cwd=root).decode().strip("\0").split("\0")
new = subprocess.check_output(["git", "ls-files", "--others", "--exclude-standard", "-z", "--", *source["Roots"]], cwd=root).decode().strip("\0").split("\0")
selected = {path for path in changed + new if path}
assert selected <= known_sources
selected.update(["tasks/done/T-039B.md", "tasks/done/T-039B.evidence/delivery-binding.json"])
allowed_suffixes = {".md", ".txt", ".json", ".log", ".xml", ".png", ".py", ".ps1", ".diff", ".cs", ".csproj", ".props", ".resx"}
evidence_roots = [evidence]
for directory in evidence_roots:
    for path in directory.rglob("*"):
        if path.is_file() and (path.suffix.lower() in allowed_suffixes or path.name == ".gitattributes") and "__pycache__" not in path.parts:
            selected.add(path.relative_to(root).as_posix())
assert all(not p.startswith((".codex/", "artifacts/")) and p not in {"tasks/BACKLOG.md", "tasks/current_task.md", "specs/PROJECT_HANDOFF.md", "specs/DEVELOPER_HANDOFF.md"} for p in selected)
selection = sorted(selected)
summary = root / "artifacts/t039b/r4-staged-selection-1.json"
if summary.exists():
    raise FileExistsError(summary)
for offset in range(0, len(selection), 64):
    subprocess.run(["git", "add", "-f", "--", *selection[offset:offset + 64]], cwd=root, check=True)
actual = subprocess.check_output(["git", "diff", "--cached", "--name-only", "-z"], cwd=root).decode().strip("\0").split("\0")
assert all(path in selected for path in actual)
raw_evidence_checks = 0
for entry in binding["Evidence"]:
    if entry["Path"].startswith("tasks/done/T-039B.evidence/r4/") and entry["Path"] in selected:
        staged = subprocess.check_output(["git", "show", ":" + entry["Path"]], cwd=root)
        assert len(staged) == entry["Bytes"] and hashlib.sha256(staged).hexdigest() == entry["Sha256"], entry["Path"]
        raw_evidence_checks += 1
summary.write_text(json.dumps({"ExplicitSelectedFiles": len(selection), "ChangedStagedFiles": len(actual), "Paths": actual,
    "PreservedUnstagedArchitectFiles": ["tasks/BACKLOG.md", "specs/PROJECT_HANDOFF.md", "specs/DEVELOPER_HANDOFF.md", "tasks/done/T-039B.review/"],
    "UserCodexStaged": False, "BinaryPackagesStaged": False, "ExactRawEvidenceGitBlobChecks": raw_evidence_checks}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
print(json.dumps({"ChangedStagedFiles": len(actual), "ExactRawEvidenceGitBlobChecks": raw_evidence_checks, "SelectionRecord": str(summary)}, ensure_ascii=False))
