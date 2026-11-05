"""Archive verified real offer and ready-stage user input without claiming download completion."""
from pathlib import Path
import hashlib
import json
import subprocess

ROOT=Path("F:/Iris")
INPUT=ROOT/"artifacts/t039c/manual-verification-v022-20261105-1/input"
REPORT=ROOT/"tasks/done/T-039C.evidence/ui-ready-v0210-input-20261105-1"
SESSION=ROOT/"artifacts/t039c/manual-verification-v022-20261105-1/output/session-14d55ff50fd94d3abe8d4474aafd5365"
assert not subprocess.check_output(["git","diff","--cached","--name-only"],cwd=ROOT).strip()
REPORT.mkdir(exist_ok=False)
for name in ("verify-ready-v0210-readonly.ps1","Save-Ready-V0210-State.cmd","Capture-Ready-V0210-View.cmd","save-ui-v0210-evidence.ps1","inspect-download-v0210-input-readonly.ps1"):
    (REPORT/name).open("xb").write((INPUT/name).read_bytes())
inspection=sorted(SESSION.glob("download-v0210-input-inspection-*.json"))[-1]
(REPORT/"guest-input-inspection.json").open("xb").write(inspection.read_bytes())
for label in ("v0210-user-offer-independent-review-1","v0210-ready-verifier-powershell51-parse-1","v0210-download-input-readonly-share-1","v0210-download-review-input-readonly-share-1","v0210-download-input-readonly-inspection-1"):
    for suffix in (".log",".command.json"):
        path=REPORT/(label+suffix);path.open("xb").write((ROOT/"tasks/T-039C.coordination"/(label+suffix)).read_bytes())
paths=[p.relative_to(ROOT).as_posix() for directory in (REPORT,ROOT/"tasks/done/T-039C.evidence/ui-offer-v0210-20261105-1") for p in sorted(directory.rglob("*")) if p.is_file()]
explicit=["tasks/T-039C.coordination/"+name for name in ("v0210-user-offer-feedback-20261105-1.json","archive_ui_offer_v0210_20261105.py","MANUAL_DOWNLOAD_V0210_20261105.md","archive_v0210_offer_and_ready_input_20261105.py")]
subprocess.run(["git","-c","core.longpaths=true","add","-f","--pathspec-from-file=-","--pathspec-file-nul"],cwd=ROOT,input=b"".join((p+"\0").encode("utf-8") for p in paths+explicit),check=True)
for name in paths:
    assert subprocess.check_output(["git","show",":"+name],cwd=ROOT)==(ROOT/name).read_bytes()
subprocess.run(["git","diff","--cached","--check","--",*explicit],cwd=ROOT,check=True)
print(json.dumps({"ActualUiOfferVerified":True,"ReadyInputPrepared":True,"RawFilesMatchedIndex":len(paths),"DownloadCompleteClaimed":False,"HistoryWarningsRetained":True}))
