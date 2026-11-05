"""Stage exact completed read-only execution proof and user-reviewable documents."""
from pathlib import Path
import hashlib
import json
import subprocess

ROOT = Path("F:/Iris")
PREFLIGHT = ROOT / "tasks/T-039C.coordination/v0210-execution-preflight-20261105T135730949323Z"
REPORT = ROOT / "tasks/done/T-039C.evidence/v0210-execution-readiness-20261105-1"
assert not subprocess.check_output(["git", "diff", "--cached", "--name-only"], cwd=ROOT).strip()
state = json.loads((PREFLIGHT / "state.json").read_text(encoding="utf-8-sig"))
assert state["SourceCommit"] == "227b3d88938543766a27d86a94ade52f9e5b89b7" and state["ReviewArchiveCommit"] == "d77973943421bd0aeb9d69d09edb4992e0c94688"
assert state["Stage"] == "preflight-verified-awaiting-explicit-user-approval" and not state["PublicationAuthorized"] and not state["PushRequestAttempted"]
assert not subprocess.check_output(["git", "tag", "--list", "v0.2.10"], cwd=ROOT).strip()
assert not (ROOT / "tasks/done/T-039C.evidence/publication-v0.2.10-20261105-1").exists()
REPORT.mkdir(exist_ok=False)


def copy(source, destination):
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.open("xb").write(source.read_bytes())


for path in sorted(PREFLIGHT.rglob("*")):
    if path.is_file() and path.name != "previous-public-full.body":
        copy(path, REPORT / "preflight" / path.relative_to(PREFLIGHT))
for label in ("v0210-review-stage-and-index-check-1", "v0210-review-archive-commit-1", "v0210-prepare-execution-1",
              "v0210-missing-approval-guard-1", "v0210-old029-approval-guard-1", "v0210-readonly-execution-preflight-1"):
    for suffix in (".log", ".command.json"):
        copy(ROOT / "tasks/T-039C.coordination" / (label + suffix), REPORT / "commands" / (label + suffix))
for label in ("v0210-missing-approval-guard-1", "v0210-old029-approval-guard-1"):
    record = json.loads((REPORT / "commands" / (label + ".command.json")).read_text(encoding="utf-8-sig"))
    assert record["exitCode"] == 2
for name in ("execute_v0210_20261105.py", "prepare_v0210_execution_20261105.py"):
    copy(ROOT / "tasks/T-039C.coordination" / name, REPORT / "executor" / name)
files = [{"Path": path.relative_to(REPORT).as_posix(), "Bytes": path.stat().st_size,
          "Sha256": hashlib.sha256(path.read_bytes()).hexdigest()} for path in sorted(REPORT.rglob("*")) if path.is_file()]
(REPORT / "binding.json").open("x", encoding="utf-8", newline="\n").write(json.dumps({"SourceCommit": state["SourceCommit"],
    "ReviewArchiveCommit": state["ReviewArchiveCommit"], "PreflightPassed": True, "OldOrMissingApprovalRejectedBeforeMutation": True,
    "PublicationAuthorized": False, "TagCreated": False, "PreviousFullBodyRetainedOutsideGit": True, "Files": files}, indent=2) + "\n")
explicit = ["tasks/T-039C.coordination/" + name for name in ("PREPUBLICATION_REVIEW_V0210_20261105.md", "CURRENT_RELEASE_GATE_V0210_20261105.md",
    "EXECUTION_READY_V0210_20261105.md", "execute_v0210_20261105.py", "prepare_v0210_execution_20261105.py", "archive_v0210_execution_20261105.py")]
raw_paths = [path for path in sorted(REPORT.rglob("*")) if path.is_file()]
relative_raw = [path.relative_to(ROOT).as_posix() for path in raw_paths]
paths = explicit + relative_raw
subprocess.run(["git", "add", "-f", "--pathspec-from-file=-", "--pathspec-file-nul"], cwd=ROOT,
               input=b"".join(name.encode("utf-8") + b"\0" for name in paths), check=True)
verified = []
for name in relative_raw:
    raw = (ROOT / name).read_bytes()
    assert subprocess.check_output(["git", "show", ":" + name], cwd=ROOT) == raw
    verified.append({"Path": name, "Bytes": len(raw), "Sha256": hashlib.sha256(raw).hexdigest()})
record = ROOT / "tasks/T-039C.coordination/v0210-execution-index-byte-check-1.json"
record.open("x", encoding="utf-8", newline="\n").write(json.dumps({"SourceCommit": state["SourceCommit"],
    "VerifiedRawFiles": len(verified), "VerifiedRawBytes": sum(row["Bytes"] for row in verified), "Files": verified}, indent=2) + "\n")
subprocess.run(["git", "add", "--", record.relative_to(ROOT).as_posix()], cwd=ROOT, check=True)
subprocess.run(["git", "diff", "--cached", "--check", "--", *explicit], cwd=ROOT, check=True)
subprocess.run(["git", "diff", "--quiet", state["SourceCommit"], "--", "src", "tests", "build", ".github", "docs", "README.md", "Iris.slnx", "Directory.Build.props", "Directory.Packages.props", "global.json"], cwd=ROOT, check=True)
print(json.dumps({"RawFilesVerified": len(verified), "RawBytesVerified": sum(row["Bytes"] for row in verified), "PublicationAuthorized": False}, indent=2))
