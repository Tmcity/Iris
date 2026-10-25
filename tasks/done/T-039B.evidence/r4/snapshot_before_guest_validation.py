from pathlib import Path
import hashlib
import json
import shutil

evidence = Path(__file__).resolve().parent
root = evidence.parents[3]
destination = evidence / "before-guest-validation-1"
destination.mkdir(exist_ok=False)
paths = ["tasks/done/T-039B.md", "tasks/done/T-039B.evidence/delivery-binding.json"]
paths += ["tasks/done/T-039B.evidence/r4/" + name for name in [
    "active_round.json", "PROGRESS.md", "run_guest.py", "write_report.py", "finalize_binding_r4.py", "inspect_delivery.py"]]
records = []
for relative in paths:
    source = root / relative
    target = destination / (source.name + ".txt")
    shutil.copyfile(source, target)
    records.append({"Path": relative, "Snapshot": target.name, "Bytes": source.stat().st_size,
        "Sha256": hashlib.sha256(source.read_bytes()).hexdigest()})
(destination / "binding.json").write_text(json.dumps(records, indent=2) + "\n", encoding="utf-8")
order_name = "signed-order-r4-sandbox-enabled-20261025.md"
assert not (evidence / order_name).exists()
shutil.copyfile(root / "tasks/current_task.md", evidence / order_name)
active_path = evidence / "active_round.json"
active = json.loads(active_path.read_text(encoding="utf-8"))
active["OrderSnapshot"] = order_name
active_path.write_text(json.dumps(active, indent=2) + "\n", encoding="utf-8", newline="\n")
print(json.dumps({"SnapshotFiles": len(records), "OrderSnapshot": order_name}))
