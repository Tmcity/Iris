"""Prepare actual installed-payload verification for the official UI-applied 0.2.10 package."""
from pathlib import Path
import hashlib
import json

ROOT=Path("F:/Iris")
INPUT=ROOT/"artifacts/t039c/manual-verification-v022-20261105-1/input"
PUBLIC=ROOT/"artifacts/t039c/public-0.2.10-download-20261105-2"
manifest=(PUBLIC/"payload-manifest.json").read_bytes()
assert hashlib.sha256(manifest).hexdigest()=="516e959fb1b7015a51ee3da41e7de160dfb35607447e45cdbdd3190884e88686"
verifier=(INPUT/"verify-v029-install-readonly.ps1").read_text(encoding="utf-8-sig").replace("0.2.9","0.2.10").replace("V029","V0210").replace("v029","v0210")
verifier=verifier.replace("3f629d7717f9673a4739da8102b781ee9cd5e242d25930f8262ee3f96ba0ad6a","516e959fb1b7015a51ee3da41e7de160dfb35607447e45cdbdd3190884e88686")
files={"PUBLIC_V0210_payload-manifest.json":manifest,"verify-v0210-install-readonly.ps1":verifier.encode("utf-8"),
    "Save-Updated-V0210-State.cmd":('@echo off\r\n"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0save-ui-v0210-evidence.ps1" -Stage updated\r\nif errorlevel 1 goto finished\r\n"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0verify-v0210-install-readonly.ps1"\r\n:finished\r\necho.\r\necho Keep the shown result and report any error.\r\npause\r\n').encode("ascii"),
    "Capture-Updated-V0210-View.cmd":('@echo off\r\n"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0capture-manual-view.ps1" -Stage updated-v0210\r\necho.\r\npause\r\n').encode("ascii")}
for name,raw in files.items(): (INPUT/name).open("xb").write(raw)
REPORT=ROOT/"tasks/done/T-039C.evidence/ui-install-v0210-input-20261105-1"
REPORT.mkdir(exist_ok=False)
for name in files: (REPORT/name).open("xb").write((INPUT/name).read_bytes())
(REPORT/"binding.json").open("x",encoding="utf-8",newline="\n").write(json.dumps({"Version":"0.2.10","PublicFullSha256":"dca307a0afc1827e27bc7ec2614e5ead3a30978d4c5d318e4d0d842ebc73f136","PublicManifestSha256":"516e959fb1b7015a51ee3da41e7de160dfb35607447e45cdbdd3190884e88686","UserControlsRealUiInstall":True,"ProductOperationPerformed":False,"Files":[{"Path":name,"Bytes":len(raw),"Sha256":hashlib.sha256(raw).hexdigest()}for name,raw in files.items()]},indent=2)+"\n")
print("Prepared official 0.2.10 UI-installed payload verifier and user capture commands; not installed.")
