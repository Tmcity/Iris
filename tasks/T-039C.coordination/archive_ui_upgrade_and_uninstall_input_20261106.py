"""Archive the verified real UI upgrade and prepared manual default-retention uninstall input."""
from pathlib import Path
import hashlib
import json
import subprocess

ROOT=Path("F:/Iris")
INPUT=ROOT/"artifacts/t039c/manual-verification-v022-20261105-1/input"
SESSION=ROOT/"artifacts/t039c/manual-verification-v022-20261105-1/output/session-14d55ff50fd94d3abe8d4474aafd5365"
REPORT=ROOT/"tasks/done/T-039C.evidence/ui-uninstall-v0210-input-20261106-1"
assert not subprocess.check_output(["git","diff","--cached","--name-only"],cwd=ROOT).strip()
REPORT.mkdir(exist_ok=False)
for name in ("Save-Uninstalled-V0210-State.cmd","Capture-Uninstalled-V0210-View.cmd","save-ui-v0210-evidence.ps1","inspect-uninstall-v0210-input-readonly.ps1"):
    (REPORT/name).open("xb").write((INPUT/name).read_bytes())
inspection=sorted(SESSION.glob("uninstall-v0210-input-inspection-*.json"))[-1]
(REPORT/"guest-input-inspection.json").open("xb").write(inspection.read_bytes())
for label in ("v0210-ready-independent-review-1","v0210-ui-install-materials-1","v0210-install-verifier-powershell51-parse-1","v0210-install-input-readonly-share-1",
              "v0210-install-review-input-readonly-share-1","v0210-install-input-readonly-inspection-1","v0210-after-ui-upgrade-state-readonly-1","v0210-after-ui-upgrade-detail-readonly-1",
              "v0210-real-ui-upgrade-independent-1","v0210-uninstall-input-readonly-share-1","v0210-uninstall-review-input-readonly-share-1","v0210-uninstall-input-readonly-inspection-1"):
    for suffix in (".log",".command.json"):(REPORT/(label+suffix)).open("xb").write((ROOT/"tasks/T-039C.coordination"/(label+suffix)).read_bytes())
explicit=["tasks/T-039C.coordination/"+name for name in ("v0210-user-ui-install-feedback-20261106-1.json","archive_ui_ready_v0210_20261105.py","prepare_ui_install_v0210_20261105.py",
          "MANUAL_INSTALL_UI_V0210_20261105.md","review_ui_upgrade_v0210_20261106.py","MANUAL_UNINSTALL_V0210_20261106.md","archive_ui_upgrade_and_uninstall_input_20261106.py")]
paths=[p.relative_to(ROOT).as_posix() for name in ("ui-ready-v0210-20261105-1","ui-install-v0210-input-20261105-1","ui-upgrade-v0210-20261106-1",REPORT.name)
       for p in sorted((ROOT/"tasks/done/T-039C.evidence"/name).rglob("*"))if p.is_file()]
subprocess.run(["git","-c","core.longpaths=true","add","-f","--pathspec-from-file=-","--pathspec-file-nul"],cwd=ROOT,input=b"".join((p+"\0").encode("utf-8")for p in paths+explicit),check=True)
for name in paths:assert subprocess.check_output(["git","show",":"+name],cwd=ROOT)==(ROOT/name).read_bytes()
subprocess.run(["git","diff","--cached","--check","--",*explicit],cwd=ROOT,check=True)
print(json.dumps({"RealUiUpgradeVerified":True,"RawFilesMatchIndex":len(paths),"UserDefaultRetentionUninstallStillPending":True}))
