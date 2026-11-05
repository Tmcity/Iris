"""Prepare the existing anonymous official observers for the approved source and version."""
from pathlib import Path

ROOT = Path("F:/Iris/tasks/T-039C.coordination")
for kind in ("release", "web"):
    source = ROOT / ("observe_v028_" + kind + "_20261105.py")
    target = ROOT / ("observe_v029_" + kind + "_20261105.py")
    text = source.read_text(encoding="utf-8-sig").replace("0.2.8", "0.2.9").replace("v028", "v029")
    text = text.replace("0c9b0a1881d07224597163b1e9b2f080ab60d434", "0b3267f1f6cdc1a9fb8cfe1c93dde22da273ecd2")
    text = text.replace("publication-v0.2.9-20261105-1", "publication-v0.2.9-20261105-1")
    text = text.replace(r"/v0\.2\.7/", r"/v0\.2\.9/")
    assert "v028" not in text and "v0.2.8" not in text
    target.open("x", encoding="utf-8", newline="\n").write(text)
print("Prepared v0.2.9 official API and website observers.")
