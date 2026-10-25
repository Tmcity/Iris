"""Acquire the pinned public actionlint into task artifacts and validate actual workflow/PS5.1 syntax."""
from pathlib import Path
import hashlib
import json
import os
import subprocess
import urllib.request
import zipfile

root = Path(__file__).resolve().parents[3]
destination = root / "artifacts/t039c/actionlint-1.7.7"
destination.mkdir(parents=True, exist_ok=False)
base = "https://github.com/rhysd/actionlint/releases/download/v1.7.7/"
records = []
for name in ["actionlint_1.7.7_checksums.txt", "actionlint_1.7.7_windows_amd64.zip"]:
    request = urllib.request.Request(base + name, headers={"User-Agent": "Iris-T039C-Tool-Validation"})
    with urllib.request.urlopen(request, timeout=90) as response:
        data = response.read(32 * 1024 * 1024 + 1)
        assert len(data) <= 32 * 1024 * 1024
    (destination / name).write_bytes(data)
    records.append({"Url": base + name, "Path": (destination / name).relative_to(root).as_posix(), "Bytes": len(data), "Sha256": hashlib.sha256(data).hexdigest()})
expected = next(line.split()[0] for line in (destination / "actionlint_1.7.7_checksums.txt").read_text().splitlines() if line.split()[-1] == "actionlint_1.7.7_windows_amd64.zip")
assert records[1]["Sha256"] == expected
with zipfile.ZipFile(destination / "actionlint_1.7.7_windows_amd64.zip") as archive:
    (destination / "actionlint.exe").write_bytes(archive.read("actionlint.exe"))
record_path = root / "tasks/done/T-039C.evidence/actionlint-tool-binding-1.json"
assert not record_path.exists()
record_path.write_text(json.dumps({"PinnedVersion": "1.7.7", "Files": records, "ReleaseChecksumMatched": True,
    "ExecutablePath": str(destination / "actionlint.exe"), "ExecutableSha256": hashlib.sha256((destination / "actionlint.exe").read_bytes()).hexdigest()}, indent=2) + "\n", encoding="utf-8", newline="\n")
result = subprocess.run([str(destination / "actionlint.exe"), "-shellcheck=", ".github/workflows/desktop-release.yml"], cwd=root)
raise SystemExit(result.returncode)
