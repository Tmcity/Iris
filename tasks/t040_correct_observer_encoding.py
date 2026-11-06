"""Preserve the original collector and prepare BOM-tagged PS5.1-correct observer files."""
from pathlib import Path
import json

ROOT=Path("F:/Iris/artifacts/t040/manual-verification-20261106-2")
SOURCE=ROOT/"input/observe-process-metrics-readonly.ps1"
original=SOURCE.read_bytes()
(ROOT/"input/observe-process-metrics-v1-original.ps1.txt").open("xb").write(original)
fixed=original.decode("utf-8-sig")
(ROOT/"input/observe-process-metrics-v2-readonly.ps1").open("xb").write(b"\xef\xbb\xbf"+fixed.encode("utf-8"))
launcher=(ROOT/"input/start-metrics-observer.ps1").read_text(encoding="utf-8-sig").replace("observe-process-metrics-readonly.ps1","observe-process-metrics-v2-readonly.ps1")
(ROOT/"input/start-metrics-observer-v2.ps1").open("xb").write(b"\xef\xbb\xbf"+launcher.encode("utf-8"))
save=(ROOT/"input/save-metrics-state-readonly.ps1").read_text(encoding="utf-8-sig").replace("*observe-process-metrics-readonly.ps1*","*observe-process-metrics*readonly.ps1*")
(ROOT/"input/save-metrics-state-v2-readonly.ps1").open("xb").write(b"\xef\xbb\xbf"+save.encode("utf-8"))
pointer=ROOT/"output/active-metrics.json"
state=json.loads((ROOT/"output/metrics-3799919aaaaa40ae8babeb9826d3ba9f/status.json").read_text(encoding="utf-8-sig"))
assert state["Ready"] is False and state["Failure"] is None
(ROOT/"output/active-metrics-v1-preserved.json").open("xb").write(pointer.read_bytes())
pointer.unlink()
print("Preserved v1 metrics and prepared BOM-tagged v2; no Iris process was started or stopped.")
