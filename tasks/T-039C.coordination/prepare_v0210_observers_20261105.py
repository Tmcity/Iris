"""Prepare official anonymous observers for the approved 0.2.10 source."""
from pathlib import Path

ROOT = Path("F:/Iris/tasks/T-039C.coordination")
for kind in ("release", "web"):
    source = ROOT / ("observe_v029_" + kind + "_20261105.py")
    text = source.read_text(encoding="utf-8-sig").replace("0.2.9", "0.2.10").replace("v029", "v0210")
    text = text.replace("0b3267f1f6cdc1a9fb8cfe1c93dde22da273ecd2", "227b3d88938543766a27d86a94ade52f9e5b89b7")
    text = text.replace(r"/v0\.2\.9/", r"/v0\.2\.10/")
    (ROOT / ("observe_v0210_" + kind + "_20261105.py")).open("x", encoding="utf-8", newline="\n").write(text)
print("Prepared official 0.2.10 observers without sending requests.")
