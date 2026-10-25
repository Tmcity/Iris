"""Keep actual prior inputs and an explicit fixture-only diff separate from product source."""
from pathlib import Path
import difflib
import json
import shutil
from inspect_delivery import file_record

evidence = Path(__file__).resolve().parent
root = evidence.parents[3]
target = evidence / "guest-fixture-changes-1"
target.mkdir(exist_ok=False)
pairs = [
    ("install", evidence / "guest-install-r4-2/input-scripts/guest_install.ps1.txt", evidence / "guest_install_r4.ps1"),
    ("update", evidence.parent / "guest_update.ps1", evidence / "guest_update_r4.ps1"),
    ("runner", evidence / "before-guest-validation-1/run_guest.py.txt", evidence / "run_guest.py"),
]
records = []
for name, prior, current in pairs:
    preserved = target / (name + "-prior-source.txt")
    shutil.copyfile(prior, preserved)
    diff = "".join(difflib.unified_diff(prior.read_text(encoding="utf-8-sig").splitlines(keepends=True),
        current.read_text(encoding="utf-8-sig").splitlines(keepends=True), fromfile=str(prior.relative_to(root)), tofile=str(current.relative_to(root))))
    (target / (name + ".diff")).write_text(diff, encoding="utf-8", newline="\n")
    records.append({"Name": name, "Prior": file_record(prior), "PreservedPrior": file_record(preserved),
        "Current": file_record(current), "Diff": file_record(target / (name + ".diff")),
        "Scope": "Actual initial R4 executed install input" if name == "install" else
            "Preserved historical update base; includes earlier R4 observation improvements and current cache/terminal observations. Not an executed pre-change R4 update proof." if name == "update" else
            "Host runner snapshot after initial diagnostic mode was added, before cache/DNS/terminal fixture changes; prior full snapshots also remain."})
(target / "binding.json").write_text(json.dumps({"Files": records,
    "ProductOrFinal8Changed": False, "RuntimePreinstalledByFixture": False,
    "Scope": "All new actually executed input scripts retain their raw bytes and SHA in each unique guest intent/input-scripts directory. Differences concern the owned guest fixture only; defaults and product source are unchanged."},
    ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
print(json.dumps({"Records": len(records), "Directory": str(target)}))
