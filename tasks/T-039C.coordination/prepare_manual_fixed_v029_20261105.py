"""Prepare pinned read-only user-operated verification for the actual official 0.2.9."""
from pathlib import Path
import hashlib
import json

ROOT = Path("F:/Iris")
PUBLIC = ROOT / "artifacts/t039c/public-0.2.9-download-20261105-1"
INPUT = ROOT / "artifacts/t039c/manual-verification-v022-20261105-1/input"
REPORT = ROOT / "tasks/done/T-039C.evidence/manual-fixed-v029-input-20261105-1"
manifest = PUBLIC / "payload-manifest.json"
manifest_hash = hashlib.sha256(manifest.read_bytes()).hexdigest()
assert manifest_hash == "3f629d7717f9673a4739da8102b781ee9cd5e242d25930f8262ee3f96ba0ad6a"
source = INPUT / "verify-v027-install-readonly.ps1"
text = source.read_text(encoding="utf-8-sig").replace("0.2.7", "0.2.9").replace("V027", "V029").replace("v027", "v029")
text = text.replace("ccfc49c983054e4007dad14fe9222219df8ca2cd34230daeeae0b35e620371b3", manifest_hash)
assert "0.2.7" not in text and "V027" not in text
files = {
    "PUBLIC_V029_payload-manifest.json": manifest.read_bytes(),
    "verify-v029-install-readonly.ps1": text.encode("utf-8"),
    "Save-Fixed-V029-State.cmd": (
        '@echo off\r\n"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0save-update-evidence.ps1" -Stage updated\r\n'
        'if errorlevel 1 goto finished\r\n'
        '"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0verify-v029-install-readonly.ps1"\r\n'
        ':finished\r\necho.\r\necho Save the result and report any error shown above.\r\npause\r\n').encode("ascii"),
    "Capture-Fixed-V029-View.cmd": (
        '@echo off\r\n"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0capture-manual-view.ps1" -Stage fixed-v029\r\n'
        'echo.\r\npause\r\n').encode("ascii"),
    "Iris-0.2.9-Official-Download.url": (
        '[InternetShortcut]\r\nURL=https://github.com/Tmcity/Iris/releases/download/v0.2.9/Iris-0.2.9-Setup.exe\r\n').encode("ascii"),
}
for name, raw in files.items():
    (INPUT / name).open("xb").write(raw)
REPORT.mkdir(exist_ok=False)
names = list(files) + ["save-update-evidence.ps1", "manual-capture-state.ps1", "capture-manual-view.ps1", "inspect-old-ready-readonly.ps1"]
for name in names:
    (REPORT / name).open("xb").write((INPUT / name).read_bytes())
records = [{"Name": name, "Bytes": (INPUT / name).stat().st_size,
            "Sha256": hashlib.sha256((INPUT / name).read_bytes()).hexdigest()} for name in names]
(REPORT / "binding.json").open("x", encoding="utf-8", newline="\n").write(json.dumps({"Version": "0.2.9",
    "SourceCommit": "0b3267f1f6cdc1a9fb8cfe1c93dde22da273ecd2", "SourceGitTree": "fcb432c3df3b5930bbc2900f43235384130b97ae",
    "OfficialSetupBytes": 116768884, "OfficialSetupSha256": "69ab96bb1f700b21fa430d69eab85d6d4a5c606c377424222f0f97af0e7f2891",
    "OfficialFullBytes": 64628500, "OfficialFullSha256": "42c32435e5883608c77f097befd2b9a7f91845b35942a09966018361c40a779e",
    "PublicPayloadManifestSha256": manifest_hash, "PayloadEntries": 97,
    "UserPerformsProductOperations": True, "Fixed029InstalledVerified": False, "ScreenshotActuallyCaptured": False,
    "InputDirectory": str(INPUT), "Files": records}, indent=2) + "\n")
print(json.dumps({"PreparedVersion": "0.2.9", "ManifestSha256": manifest_hash,
                  "PreparedInputFiles": list(files), "UserOperationPerformed": False}, indent=2))
