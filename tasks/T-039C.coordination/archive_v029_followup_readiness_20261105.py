"""Freeze the actual unchanged old instance and prepared follow-up checks without claiming user acceptance."""
from pathlib import Path
import datetime
import hashlib
import json
import subprocess

ROOT = Path("F:/Iris")
SESSION = ROOT / "artifacts/t039c/manual-verification-v022-20261105-1/output/session-14d55ff50fd94d3abe8d4474aafd5365"
REPORT = ROOT / "tasks/done/T-039C.evidence/manual-v029-followup-readiness-20261105-1"
assert not subprocess.check_output(["git", "diff", "--cached", "--name-only"], cwd=ROOT).strip()
progress_path = sorted(SESSION.glob("progress-v029-*.json"))[-1]
progress = json.loads(progress_path.read_text(encoding="utf-8-sig"))
assert progress["ReadOnlyProductInspection"] and progress["ProductOperationPerformed"] is False
assert progress["InstalledSource"]["Version"] == "0.2.7" and len(progress["Processes"]) == 1
assert progress["Processes"][0]["Pid"] == 8820
REPORT.mkdir(exist_ok=False)
files = {
    "live-progress.json": progress_path,
    "inspect-v029-progress-readonly.ps1": ROOT / "artifacts/t039c/manual-verification-v022-20261105-1/input/inspect-v029-progress-readonly.ps1",
    "review_manual_fixed_v029_20261105.py": ROOT / "tasks/T-039C.coordination/review_manual_fixed_v029_20261105.py",
    "supplement_v029_official_api_20261105.py": ROOT / "tasks/T-039C.coordination/supplement_v029_official_api_20261105.py",
    "v029-manual-progress-readonly-1.log": ROOT / "tasks/T-039C.coordination/v029-manual-progress-readonly-1.log",
    "v029-manual-progress-readonly-1.command.json": ROOT / "tasks/T-039C.coordination/v029-manual-progress-readonly-1.command.json",
}
records = []
for name, source in files.items():
    raw = source.read_bytes()
    (REPORT / name).open("xb").write(raw)
    records.append({"Path": name, "Bytes": len(raw), "Sha256": hashlib.sha256(raw).hexdigest()})
(REPORT / "binding.json").open("x", encoding="utf-8", newline="\n").write(json.dumps({
    "PreparedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "ObservedInstalledVersion": "0.2.7", "ObservedAppPid": 8820,
    "UserManual029FeedbackPending": True, "Manual029ActuallyAccepted": False, "UserScreenshotActuallyReviewed": False,
    "Official029ApiSupplementActuallyPerformed": False, "NewVersionAuthorized": False, "Files": records}, indent=2) + "\n")
explicit = ["tasks/T-039C.coordination/review_manual_fixed_v029_20261105.py",
    "tasks/T-039C.coordination/supplement_v029_official_api_20261105.py",
    "tasks/T-039C.coordination/archive_v029_followup_readiness_20261105.py"]
paths = explicit + [path.relative_to(ROOT).as_posix() for path in sorted(REPORT.iterdir()) if path.is_file()]
subprocess.run(["git", "add", "-f", "--pathspec-from-file=-", "--pathspec-file-nul"], cwd=ROOT,
               input=b"".join(path.encode("utf-8") + b"\0" for path in paths), check=True)
for path in REPORT.iterdir():
    if path.is_file():
        actual = subprocess.check_output(["git", "show", ":" + path.relative_to(ROOT).as_posix()], cwd=ROOT)
        assert actual == path.read_bytes()
subprocess.run(["git", "diff", "--cached", "--check", "--", *explicit], cwd=ROOT, check=True)
print(json.dumps({"PreparedFollowupChecks": True, "ActualObservedVersion": "0.2.7", "UserFeedbackPending": True,
                  "Manual029ActuallyAccepted": False, "RawEvidenceBytesMatchIndex": True}, indent=2))
